#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace AlgernonsTerrainSampler.Tiles;

/// <summary>
/// Encoded tiles on disk, one file each under <c>root/identity/</c>. A different identity is a different folder, so
/// a world whose inputs change starts a fresh cache; older folders under the same root count against the quota until
/// they are evicted.
/// </summary>
/// <remarks>
/// The index is built on the cache's own thread at start, without reading a tile. Reads run on that same thread, so a
/// cached tile never waits behind a cold one being sampled. Writes come from the tile workers. A file that fails its
/// checks is deleted and its tile sampled again. If the storage itself fails, the cache turns itself off and the
/// service carries on without it.
/// </remarks>
internal sealed class TerrainTileDiskCache : IDisposable
{
    private const string Extension = ".ats";
    private const int MaxFiles = 65536;

    private readonly string root;
    private readonly string directory;
    private readonly long limit;
    private readonly Action<string>? log;
    private readonly object ioLock = new();
    private readonly ConcurrentDictionary<TerrainTileKey, long> index = new();
    private readonly Dictionary<string, (long Size, DateTime Used)> files = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<Action> reads = new(256);
    private readonly Thread thread;
    private long bytes;
    private volatile bool ready;
    private volatile bool disabled;
    private int reportedFailure;
    private int disposed;

    public TerrainTileDiskCache(string root, string identity, int megabytes, Action<string>? log = null)
    {
        if (!TerrainTileIdentity.IsValid(identity))
            throw new ArgumentException("Invalid cache identity.", nameof(identity));

        this.root = Path.GetFullPath(root);
        this.directory = Path.Combine(this.root, identity);
        this.limit = Math.Clamp(megabytes, 1, 65536) * 1024L * 1024L;
        this.log = log;
        this.thread = new Thread(this.Run) { IsBackground = true, Name = "Terrain Sampler tile disk cache" };
        this.thread.Start();
    }

    /// <summary>The index is built. Until then nothing is known to be on disk, so nothing should be sampled either.</summary>
    public bool Ready => this.ready;

    public bool Disabled => this.disabled;

    public int Count => this.index.Count;

    public long Bytes => Interlocked.Read(ref this.bytes);

    public string Folder => this.directory;

    public bool Contains(TerrainTileKey key) => !this.disabled && this.index.ContainsKey(key);

    private string PathFor(TerrainTileKey key) => Path.Combine(this.directory, $"{key.Step}_{key.TileX}_{key.TileZ}{Extension}");

    private void Run()
    {
        this.Initialise();
        foreach (Action read in this.reads.GetConsumingEnumerable())
            read();
    }

    private void Initialise()
    {
        try
        {
            _ = Directory.CreateDirectory(this.directory);
            lock (this.ioLock)
            {
                foreach (string folder in Directory.EnumerateDirectories(this.root))
                {
                    // Only this cache's own folders: hash-named, holding tile files.
                    if (!TerrainTileIdentity.IsValid(Path.GetFileName(folder)))
                        continue;

                    foreach (string temporary in Directory.EnumerateFiles(folder, "*" + Extension + ".tmp"))
                        File.Delete(temporary);

                    bool current = string.Equals(folder, this.directory, StringComparison.OrdinalIgnoreCase);
                    foreach (string path in Directory.EnumerateFiles(folder, "*" + Extension))
                    {
                        FileInfo file = new(path);
                        this.files[path] = (file.Length, file.LastWriteTimeUtc);
                        this.bytes += file.Length;
                        if (current && TryKey(path, out TerrainTileKey key) && file.Length > TerrainTileCodec.HeaderBytes)
                            this.index[key] = file.Length;
                    }
                }

                _ = this.Trim(0, null);
            }

            this.log?.Invoke($"tile disk cache ready: {this.Count} tiles, {this.Bytes / (1024.0 * 1024.0):0.0} MiB in {this.directory}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            this.disabled = true;
            this.index.Clear();
            this.ReportFailure(ex);
        }
        finally
        {
            this.ready = true;
        }
    }

    private static bool TryKey(string path, out TerrainTileKey key)
    {
        key = default;
        string[] parts = Path.GetFileNameWithoutExtension(path).Split('_');
        if (parts.Length != 3 || !int.TryParse(parts[0], out int step) || !int.TryParse(parts[1], out int x)
            || !int.TryParse(parts[2], out int z) || !TerrainTileKey.IsValidStep(step) || x < 0 || z < 0)
            return false;

        key = new TerrainTileKey(step, x, z);
        return true;
    }

    /// <summary>
    /// Queues a read. <paramref name="done"/> runs on the cache's thread with the tile's bytes, already checked, or null
    /// if the file was missing or damaged (and is now gone). False if the read could not be queued; try again later.
    /// </summary>
    public bool TryRead(TerrainTileKey key, Action<byte[]?> done)
    {
        if (!this.ready || this.disabled || !this.index.ContainsKey(key))
            return false;

        try
        {
            return this.reads.TryAdd(() => done(this.Load(key)));
        }
        catch (InvalidOperationException)
        {
            return false; // Disposed.
        }
    }

    private byte[]? Load(TerrainTileKey key)
    {
        lock (this.ioLock)
        {
            string path = this.PathFor(key);
            try
            {
                byte[] data = File.ReadAllBytes(path);
                if (!TerrainTileCodec.Verify(data, key))
                    throw new InvalidDataException($"{path} is damaged or names another tile.");

                // The write time is the eviction order, so it survives restarts. Failing to update it must not stop a
                // sound tile from being served.
                if (this.files.TryGetValue(path, out (long Size, DateTime Used) file))
                    this.files[path] = (file.Size, DateTime.UtcNow);
                try
                {
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }

                return data;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _ = this.index.TryRemove(key, out _);
                try
                {
                    this.Remove(path);
                }
                catch (Exception deleteError) when (deleteError is IOException or UnauthorizedAccessException)
                {
                }

                this.ReportFailure(ex);
                return null;
            }
        }
    }

    /// <summary>Writes a tile, replacing any older copy. Called from the tile workers.</summary>
    public void Store(TerrainTileKey key, byte[] encoded)
    {
        if (!this.ready || this.disabled || Volatile.Read(ref this.disposed) != 0)
            return;

        lock (this.ioLock)
        {
            string path = this.PathFor(key);
            string temporary = path + ".tmp";
            try
            {
                if (encoded.Length > this.limit || !this.Trim(encoded.Length, path))
                    return;

                using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(encoded);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporary, path, overwrite: true);
                if (this.files.TryGetValue(path, out (long Size, DateTime Used) previous))
                    this.bytes -= previous.Size;

                this.files[path] = (encoded.Length, DateTime.UtcNow);
                this.bytes += encoded.Length;
                this.index[key] = encoded.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                this.ReportFailure(ex);
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception deleteError) when (deleteError is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    // Least recently used first, any identity's folder. Caller holds the lock.
    private bool Trim(long needed, string? replacing)
    {
        long replaced = replacing != null && this.files.TryGetValue(replacing, out (long Size, DateTime Used) previous) ? previous.Size : 0;
        if (this.bytes - replaced + needed <= this.limit && this.files.Count < MaxFiles)
            return true;

        foreach (KeyValuePair<string, (long Size, DateTime Used)> file in this.files.OrderBy(f => f.Value.Used).ToArray())
        {
            if (this.bytes - replaced + needed <= this.limit && this.files.Count < MaxFiles)
                break;

            if (!string.Equals(file.Key, replacing, StringComparison.OrdinalIgnoreCase))
                this.Remove(file.Key);
        }

        return this.bytes - replaced + needed <= this.limit && this.files.Count < MaxFiles;
    }

    // Caller holds the lock.
    private void Remove(string path)
    {
        File.Delete(path);
        if (this.files.Remove(path, out (long Size, DateTime Used) file))
            this.bytes -= file.Size;

        if (string.Equals(Path.GetDirectoryName(path), this.directory, StringComparison.OrdinalIgnoreCase) && TryKey(path, out TerrainTileKey key))
            _ = this.index.TryRemove(key, out _);
    }

    private void ReportFailure(Exception ex)
    {
        if (Interlocked.Exchange(ref this.reportedFailure, 1) == 0)
            this.log?.Invoke($"tile disk cache could not use a file; affected tiles will be sampled again: {ex.Message}");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            return;

        this.reads.CompleteAdding();
        this.thread.Join();
        this.reads.Dispose();
    }
}
