#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace AlgernonsTerrainSampler.Tiles;

/// <summary>What the service needs to know about whoever wants a tile.</summary>
/// <param name="Allowed">Whether they may ask at all.</param>
/// <param name="DeniedReason">Why not, when they may not.</param>
/// <param name="Limited">Whether the distance limit applies: true for players, false for mods on the server itself.</param>
/// <param name="X">The player's position, for the distance limit.</param>
/// <param name="Z">The player's position, for the distance limit.</param>
internal readonly record struct TerrainTileOwner(bool Allowed, string DeniedReason, bool Limited, double X, double Z)
{
    public static TerrainTileOwner Unlimited => new(true, "", false, 0, 0);

    public static TerrainTileOwner Denied(string reason) => new(false, reason, false, 0, 0);
}

/// <summary>
/// The server's tile scheduler and memory cache. Owners (players by their UID, mods on the server by a name of their
/// own) say which tiles they want, at what priority; the service answers each one once, from memory, from disk or by
/// sampling, and one tile is only ever made once however many owners want it.
/// </summary>
/// <remarks>
/// Everything here runs on the server's main thread except the sampling, encoding and disk writes, which run on the
/// workers, and the disk reads, which run on the disk cache's thread. Results cross back through queues drained in
/// <see cref="Tick"/>. Nothing here touches the game, so it can be tested without one.
/// </remarks>
internal sealed class TerrainTileService : IDisposable
{
    public sealed record Settings(
        int Workers,
        int MemoryCacheMegabytes,
        int MaxWantedTiles,
        int MaxDistancePerStep,
        bool SampleOnMainThread = false,
        double MainThreadBudgetMilliseconds = 4);

    private readonly int mapSizeX;
    private readonly int mapSizeZ;
    private readonly Func<int, int, TerrainColumnSample> sample;
    private readonly TerrainTileDiskCache? disk;
    private readonly Settings settings;
    private readonly Func<string, TerrainTileOwner> owners;
    private readonly Action<string, TerrainTileKey, byte[]> ready;
    private readonly Action<string, TerrainTileKey, string> refused;
    private readonly Action<string>? log;
    private readonly Func<long> clock;
    private readonly long memoryLimit;

    private readonly Dictionary<TerrainTileKey, Entry> entries = [];
    private readonly Dictionary<string, HashSet<TerrainTileKey>> wanted = new(StringComparer.Ordinal);
    private readonly List<string> ownerOrder = [];
    private readonly ConcurrentQueue<(TerrainTileKey Key, byte[]? Data)> reads = new();
    private readonly ConcurrentQueue<TerrainTileJob> finished = new();
    private readonly TerrainTileWorkers workers;
    private TerrainTileJob? mainThreadJob;
    private int running;
    private int ownerCursor;
    private long memoryBytes;
    private long serial;
    private long nextOwnerCheck;
    private volatile int throttle;
    private bool disposed;

    public TerrainTileService(
        int mapSizeX,
        int mapSizeZ,
        Func<int, int, TerrainColumnSample> sample,
        TerrainTileDiskCache? disk,
        Settings settings,
        Func<string, TerrainTileOwner> owners,
        Action<string, TerrainTileKey, byte[]> ready,
        Action<string, TerrainTileKey, string> refused,
        Action<string>? log = null,
        Func<long>? clock = null)
    {
        this.mapSizeX = mapSizeX;
        this.mapSizeZ = mapSizeZ;
        this.sample = sample;
        this.disk = disk;
        this.settings = settings;
        this.owners = owners;
        this.ready = ready;
        this.refused = refused;
        this.log = log;
        this.clock = clock ?? (() => Environment.TickCount64);
        this.memoryLimit = Math.Clamp(settings.MemoryCacheMegabytes, 1, 4096) * 1024L * 1024L;
        this.workers = new TerrainTileWorkers(settings.Workers, this.RunJob);
    }

    public long Generated { get; private set; }
    public long MemoryHits { get; private set; }
    public long DiskHits { get; private set; }
    public long Failures { get; private set; }
    public long Refusals { get; private set; }
    public double GenerateMilliseconds { get; private set; }
    public int Running => this.running;
    public int MemoryTiles => this.entries.Values.Count(e => e.Data != null);
    public long MemoryBytes => this.memoryBytes;
    public int Owners => this.wanted.Count;

    /// <summary>Tiles some owner wants that are neither in memory, nor being read, nor being made.</summary>
    public int Waiting => this.entries.Values.Count(e => e.Wanted.Count > 0 && e.Data == null && !e.Reading && !e.Generating);

    public int WantedBy(string owner) => this.wanted.TryGetValue(owner, out HashSet<TerrainTileKey>? keys) ? keys.Count : 0;

    /// <summary>
    /// Asks for a tile, or changes its priority if it is already wanted. Lower numbers come first. The answer is
    /// <c>ready</c> or <c>refused</c>, perhaps at once, perhaps in a later tick.
    /// </summary>
    public void Want(string owner, TerrainTileKey key, int priority)
    {
        if (this.disposed)
            return;

        if (!key.IsValid(this.mapSizeX, this.mapSizeZ))
        {
            this.Refuse(owner, key, "That tile is outside the world, or its step is not a power of two from 1 to 64.");
            return;
        }

        TerrainTileOwner info = this.owners(owner);
        if (!info.Allowed)
        {
            this.Refuse(owner, key, info.DeniedReason);
            return;
        }

        if (info.Limited && this.TooFar(key, info))
        {
            this.Refuse(owner, key, this.TooFarReason(key));
            return;
        }

        if (!this.wanted.TryGetValue(owner, out HashSet<TerrainTileKey>? keys))
        {
            keys = [];
            this.wanted[owner] = keys;
            this.ownerOrder.Add(owner);
        }

        if (!keys.Contains(key) && keys.Count >= this.settings.MaxWantedTiles)
        {
            this.Refuse(owner, key, string.Create(CultureInfo.InvariantCulture, $"More than {this.settings.MaxWantedTiles} tiles wanted at once."));
            return;
        }

        if (!this.entries.TryGetValue(key, out Entry? entry))
        {
            entry = new Entry(key);
            this.entries[key] = entry;
        }

        entry.Wanted[owner] = priority;
        _ = keys.Add(key);

        if (entry.Data != null)
        {
            this.MemoryHits++;
            this.Deliver(entry);
            return;
        }

        this.TryRead(entry);
    }

    /// <summary>No longer wants the tile. A tile already being made is finished and kept: it is cheap to keep and may be wanted again.</summary>
    public void Drop(string owner, TerrainTileKey key)
    {
        if (this.wanted.TryGetValue(owner, out HashSet<TerrainTileKey>? keys))
            _ = keys.Remove(key);

        if (this.entries.TryGetValue(key, out Entry? entry))
        {
            _ = entry.Wanted.Remove(owner);
            this.Forget(entry);
        }
    }

    /// <summary>Drops everything the owner wants, as when a player leaves.</summary>
    public void DropOwner(string owner)
    {
        if (!this.wanted.Remove(owner, out HashSet<TerrainTileKey>? keys))
            return;

        _ = this.ownerOrder.Remove(owner);
        foreach (TerrainTileKey key in keys)
        {
            if (this.entries.TryGetValue(key, out Entry? entry))
            {
                _ = entry.Wanted.Remove(owner);
                this.Forget(entry);
            }
        }
    }

    /// <summary>A client's want message: a reset, then its drops, then its adds, at most <see cref="TerrainTileProtocol.MaxKeysPerWant"/> of each.</summary>
    public void Apply(string owner, TerrainTileWant want)
    {
        if (want.Reset)
            this.DropOwner(owner);

        int drops = Math.Min(want.DropCount, TerrainTileProtocol.MaxKeysPerWant);
        for (int i = 0; i < drops; i++)
            this.Drop(owner, new TerrainTileKey(want.DropStep![i], want.DropX![i], want.DropZ![i]));

        int adds = Math.Min(want.AddCount, TerrainTileProtocol.MaxKeysPerWant);
        for (int i = 0; i < adds; i++)
            this.Want(owner, new TerrainTileKey(want.AddStep![i], want.AddX![i], want.AddZ![i]), want.AddPriority![i]);
    }

    /// <summary>The game process's load, from which the workers decide how much to give way.</summary>
    public void UpdateLoad(double processCpuPercent, long tickIntervalMilliseconds)
        => this.throttle = processCpuPercent >= 75 ? 20 : tickIntervalMilliseconds > 100 ? 10 : 0;

    public void Tick()
    {
        if (this.disposed)
            return;

        while (this.reads.TryDequeue(out (TerrainTileKey Key, byte[]? Data) read))
        {
            if (!this.entries.TryGetValue(read.Key, out Entry? entry))
                continue;

            entry.Reading = false;
            if (read.Data != null)
            {
                this.DiskHits++;
                this.Keep(entry, read.Data);
                this.Deliver(entry);
            }
            else
            {
                this.Forget(entry); // Missing or damaged: made again below if it is still wanted.
            }
        }

        while (this.finished.TryDequeue(out TerrainTileJob? job))
            this.Finish(job);

        if (this.mainThreadJob != null)
            this.AdvanceMainThreadJob();

        long now = this.clock();
        if (now >= this.nextOwnerCheck)
        {
            this.nextOwnerCheck = now + 2000;
            this.CheckOwners();
        }

        this.StartReads();
        this.StartJobs();
        this.Trim();
    }

    private void Finish(TerrainTileJob job)
    {
        this.running--;
        if (!this.entries.TryGetValue(job.Key, out Entry? entry))
            return;

        entry.Generating = false;
        if (job.Error == null && job.Result != null)
        {
            this.Generated++;
            this.GenerateMilliseconds += job.SampleMilliseconds;
            this.Keep(entry, job.Result);
            this.Deliver(entry);
            return;
        }

        if (job.Error is OperationCanceledException)
        {
            this.Forget(entry);
            return;
        }

        // Never kept: a failure is an answer for now, not for good. The next want samples it again.
        this.Failures++;
        this.log?.Invoke($"could not make {job.Key}: {job.Error}");
        string reason = $"The terrain sampler failed on this tile: {job.Error?.GetBaseException().Message}";
        foreach (string owner in entry.Wanted.Keys.ToArray())
        {
            if (this.wanted.TryGetValue(owner, out HashSet<TerrainTileKey>? keys))
                _ = keys.Remove(job.Key);

            this.Refuse(owner, job.Key, reason);
        }

        entry.Wanted.Clear();
        this.Forget(entry);
    }

    private void AdvanceMainThreadJob()
    {
        TerrainTileJob job = this.mainThreadJob!;
        try
        {
            job.Sample(this.sample, this.mapSizeX, this.mapSizeZ, this.settings.MainThreadBudgetMilliseconds, () => 0, this.workers.Token);
        }
        catch (Exception ex)
        {
            job.Error = ex;
        }

        if (job.Sampled || job.Error != null)
        {
            this.mainThreadJob = null;
            if (job.Error != null)
                this.finished.Enqueue(job);
            else
                this.workers.Submit(job); // Encoding and the disk write still happen off the main thread.
        }
    }

    // Owners whose permission went or who moved away lose what they can no longer have.
    private void CheckOwners()
    {
        foreach (string owner in this.ownerOrder.ToArray())
        {
            TerrainTileOwner info = this.owners(owner);
            if (info.Allowed && !info.Limited)
                continue;

            foreach (TerrainTileKey key in this.wanted[owner].ToArray())
            {
                if (!info.Allowed)
                    this.RefuseWanted(owner, key, info.DeniedReason);
                else if (this.TooFar(key, info))
                    this.RefuseWanted(owner, key, this.TooFarReason(key));
            }
        }
    }

    private void RefuseWanted(string owner, TerrainTileKey key, string reason)
    {
        this.Drop(owner, key);
        this.Refuse(owner, key, reason);
    }

    private void StartReads()
    {
        if (this.disk == null || !this.disk.Ready)
            return;

        foreach (Entry entry in this.entries.Values)
        {
            if (entry.Wanted.Count > 0 && entry.Data == null && !entry.Reading && !entry.Generating && !this.TryRead(entry) && this.disk.Contains(entry.Key))
                break; // The read queue is full; the rest wait for the next tick.
        }
    }

    private bool TryRead(Entry entry)
    {
        if (this.disk == null || !this.disk.Ready || entry.Reading || entry.Generating || !this.disk.Contains(entry.Key))
            return false;

        TerrainTileKey key = entry.Key;
        if (!this.disk.TryRead(key, data => this.reads.Enqueue((key, data))))
            return false;

        entry.Reading = true;
        return true;
    }

    private void StartJobs()
    {
        // Until the disk index is built nothing is known to be cached, so nothing is sampled either.
        if (this.disk != null && !this.disk.Ready)
            return;

        int capacity = this.settings.SampleOnMainThread
            ? (this.mainThreadJob == null && this.running < 2 ? 1 : 0)
            : this.workers.Count - this.running;

        while (capacity-- > 0 && this.TryPickCold(out Entry? entry))
        {
            entry!.Generating = true;
            TerrainTileJob job = new(entry.Key);
            this.running++;
            if (this.settings.SampleOnMainThread)
                this.mainThreadJob = job;
            else
                this.workers.Submit(job);
        }
    }

    // Round robin between owners, each owner's most urgent tile that nobody has made yet.
    private bool TryPickCold(out Entry? picked)
    {
        picked = null;
        for (int visited = 0; visited < this.ownerOrder.Count; visited++)
        {
            this.ownerCursor %= this.ownerOrder.Count;
            string owner = this.ownerOrder[this.ownerCursor++];
            int best = int.MaxValue;
            foreach (TerrainTileKey key in this.wanted[owner])
            {
                Entry entry = this.entries[key];
                if (entry.Data != null || entry.Reading || entry.Generating || (this.disk?.Contains(key) ?? false))
                    continue;

                int priority = entry.Wanted[owner];
                if (picked == null || priority < best)
                {
                    picked = entry;
                    best = priority;
                }
            }

            if (picked != null)
                return true;
        }

        return false;
    }

    private void RunJob(TerrainTileJob job, CancellationToken token)
    {
        try
        {
            if (!job.Sampled)
                job.Sample(this.sample, this.mapSizeX, this.mapSizeZ, double.PositiveInfinity, () => this.throttle, token);

            byte[] encoded = TerrainTileCodec.Encode(job.Tile);
            this.disk?.Store(job.Key, encoded);
            job.Result = encoded;
        }
        catch (Exception ex)
        {
            job.Error = ex;
        }

        this.finished.Enqueue(job);
    }

    private void Keep(Entry entry, byte[] data)
    {
        if (entry.Data == null)
            this.memoryBytes += data.Length;

        entry.Data = data;
        entry.Used = ++this.serial;
    }

    private void Deliver(Entry entry)
    {
        byte[] data = entry.Data!;
        entry.Used = ++this.serial;
        foreach (string owner in entry.Wanted.Keys.ToArray())
        {
            if (this.wanted.TryGetValue(owner, out HashSet<TerrainTileKey>? keys))
                _ = keys.Remove(entry.Key);

            this.ready(owner, entry.Key, data);
        }

        entry.Wanted.Clear();
    }

    private void Refuse(string owner, TerrainTileKey key, string reason)
    {
        this.Refusals++;
        this.refused(owner, key, reason);
    }

    // An entry with nothing to keep it goes.
    private void Forget(Entry entry)
    {
        if (entry.Wanted.Count == 0 && entry.Data == null && !entry.Reading && !entry.Generating)
            _ = this.entries.Remove(entry.Key);
    }

    // Least recently used tiles that nobody is waiting for leave memory first. They stay on disk.
    private void Trim()
    {
        if (this.memoryBytes <= this.memoryLimit)
            return;

        foreach (Entry entry in this.entries.Values.Where(e => e.Data != null && e.Wanted.Count == 0).OrderBy(e => e.Used).ToArray())
        {
            if (this.memoryBytes <= this.memoryLimit)
                break;

            this.memoryBytes -= entry.Data!.Length;
            entry.Data = null;
            this.Forget(entry);
        }
    }

    private bool TooFar(TerrainTileKey key, TerrainTileOwner info)
    {
        double limit = (double)this.settings.MaxDistancePerStep * key.Step;
        return key.DistanceSquaredTo(info.X, info.Z) > limit * limit;
    }

    private string TooFarReason(TerrainTileKey key)
        => string.Create(CultureInfo.InvariantCulture, $"Tiles at step {key.Step} are only served within {(long)this.settings.MaxDistancePerStep * key.Step} blocks of the player.");

    public string Describe()
    {
        StringBuilder text = new();
        _ = text.Append(CultureInfo.InvariantCulture, $"Tiles made {this.Generated}");
        if (this.Generated > 0)
            _ = text.Append(CultureInfo.InvariantCulture, $" ({this.GenerateMilliseconds / this.Generated:0} ms each to sample)");

        _ = text.Append(CultureInfo.InvariantCulture, $", from memory {this.MemoryHits}, from disk {this.DiskHits}, failed {this.Failures}, refused {this.Refusals}.\n");
        _ = text.Append(CultureInfo.InvariantCulture, $"Now: {this.running} being made on {this.workers.Count} worker(s), {this.Waiting} waiting, {this.Owners} owner(s) wanting tiles.\n");
        _ = text.Append(CultureInfo.InvariantCulture, $"Memory: {this.MemoryTiles} tiles, {this.memoryBytes / (1024.0 * 1024.0):0.0} of {this.memoryLimit / (1024 * 1024)} MiB.");
        if (this.disk?.Disabled == true)
            _ = text.Append("\nDisk: off after an error.");
        else if (this.disk != null)
            _ = text.Append(CultureInfo.InvariantCulture, $"\nDisk: {this.disk.Count} tiles, {this.disk.Bytes / (1024.0 * 1024.0):0.0} MiB{(this.disk.Ready ? "" : ", still indexing")}.");

        return text.ToString();
    }

    /// <summary>Stops the workers and waits for them, then closes the disk cache. Call before the sampler disposes its caches.</summary>
    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        this.workers.Dispose();
        this.disk?.Dispose();
        this.entries.Clear();
        this.wanted.Clear();
        this.ownerOrder.Clear();
    }

    private sealed class Entry
    {
        public readonly TerrainTileKey Key;
        public readonly Dictionary<string, int> Wanted = new(StringComparer.Ordinal);
        public byte[]? Data;
        public long Used;
        public bool Reading;
        public bool Generating;

        public Entry(TerrainTileKey key) => this.Key = key;
    }
}
