#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AlgernonsTerrainSampler.Tiles;

/// <summary>
/// A client's tiles: from a small memory cache of encoded tiles, then the client's own disk cache under the server's
/// identity, then the server. Tiles arrive in fragments, are checked and decoded off the main thread, and are handed to
/// every consumer that asked through <see cref="Requests"/>.
/// </summary>
/// <remarks>
/// Main thread only, apart from decoding and disk writes, which run on the thread pool. It knows nothing of the game:
/// the mod system feeds it the server's messages and sends what it asks to send.
/// </remarks>
internal sealed class TerrainTileClientCore : ITerrainTileSource, IDisposable
{
    private const long RecentLimit = 32L * 1024 * 1024;

    private readonly Action<TerrainTileWant> send;
    private readonly Func<string, TerrainTileDiskCache?> openDisk;
    private readonly Action<string>? log;
    private readonly Dictionary<TerrainTileKey, Pending> pending = [];
    private readonly Dictionary<TerrainTileKey, Assembly> assemblies = [];
    private readonly Dictionary<TerrainTileKey, int> adds = [];
    private readonly HashSet<TerrainTileKey> drops = [];
    private readonly Dictionary<TerrainTileKey, (byte[] Data, long Used)> recent = [];
    private readonly ConcurrentQueue<(TerrainTileKey Key, byte[]? Data)> diskReads = new();
    private readonly ConcurrentQueue<Decoded> decoded = new();
    private readonly List<TerrainTileKey> refusedNow = [];
    private TerrainTileDiskCache? disk;
    private long recentBytes;
    private long serial;
    private bool disposed;

    public TerrainTileClientCore(Action<TerrainTileWant> send, Func<string, TerrainTileDiskCache?> openDisk, Action<string>? log = null)
    {
        this.send = send;
        this.openDisk = openDisk;
        this.log = log;
        this.Requests = new TerrainTileRequests(this, log);
    }

    public TerrainTileRequests Requests { get; }

    public bool Available { get; private set; }

    public string UnavailableReason { get; private set; } = "Waiting for the server.";

    public string Identity { get; private set; } = "";

    public TerrainTileStatus? Status { get; private set; }

    public TerrainTileProgress? LastProgress { get; private set; }

    public long Received { get; private set; }

    public long ReceivedBytes { get; private set; }

    public long FromDisk { get; private set; }

    public long FromMemory { get; private set; }

    public long Refused { get; private set; }

    public int InFlight => this.pending.Count;

    public TerrainTileDiskCache? Disk => this.disk;

    /// <summary>Raised on the main thread when <see cref="Available"/> changes.</summary>
    public event Action? AvailabilityChanged;

    public void Want(TerrainTileKey key, int priority)
    {
        if (!this.Available)
        {
            // Answered next tick rather than now, so a consumer never hears back before its request has returned.
            this.refusedNow.Add(key);
            return;
        }

        if (this.pending.TryGetValue(key, out Pending? existing))
        {
            if (existing.Stage == Stage.Server && priority < existing.Priority)
            {
                existing.Priority = priority;
                this.adds[key] = priority;
            }

            return;
        }

        Pending entry = new() { Priority = priority };
        this.pending[key] = entry;
        if (this.recent.TryGetValue(key, out (byte[] Data, long Used) kept))
        {
            this.FromMemory++;
            this.recent[key] = (kept.Data, ++this.serial);
            entry.Stage = Stage.Decoding;
            this.DecodeLater(key, kept.Data, store: false);
        }
        else if (this.disk != null && this.disk.Contains(key) && this.disk.TryRead(key, data => this.diskReads.Enqueue((key, data))))
        {
            entry.Stage = Stage.Disk;
        }
        else
        {
            this.AskServer(key, entry);
        }
    }

    public void Drop(TerrainTileKey key)
    {
        if (!this.pending.Remove(key, out Pending? entry) || entry.Stage != Stage.Server)
            return;

        if (!this.adds.Remove(key) || entry.Sent)
            _ = this.drops.Add(key);
    }

    private void AskServer(TerrainTileKey key, Pending entry)
    {
        entry.Stage = Stage.Server;
        this.adds[key] = entry.Priority;
        _ = this.drops.Remove(key);
    }

    public void OnStatus(TerrainTileStatus status)
    {
        this.Status = status;
        bool available = status.Protocol == TerrainTileProtocol.Version && status.Available && TerrainTileIdentity.IsValid(status.Identity);
        string reason = status.Protocol != TerrainTileProtocol.Version
            ? $"The server speaks tile protocol {status.Protocol}; this client speaks {TerrainTileProtocol.Version}. Update the terrain sampler on both."
            : status.Available ? "" : status.Reason;

        if (available && this.Identity != status.Identity)
        {
            this.disk?.Dispose();
            this.disk = this.openDisk(status.Identity);
            this.recent.Clear();
            this.recentBytes = 0;
        }

        this.Identity = available ? status.Identity : "";
        this.SetAvailable(available, reason);
    }

    public void OnDisconnected(string reason) => this.SetAvailable(false, reason);

    /// <summary>Why tiles are unavailable while there is no answer from the server, for the refusals until there is.</summary>
    public void SetWaitingReason(string reason)
    {
        if (!this.Available)
            this.UnavailableReason = reason;
    }

    private void SetAvailable(bool available, string reason)
    {
        bool changed = available != this.Available;
        this.Available = available;
        this.UnavailableReason = available ? "" : reason;
        if (!available)
        {
            this.pending.Clear();
            this.assemblies.Clear();
            this.adds.Clear();
            this.drops.Clear();
            this.Requests.FailAll(reason);
        }

        if (changed)
            this.AvailabilityChanged?.Invoke();
    }

    public void OnData(TerrainTileData data)
    {
        TerrainTileKey key = new(data.Step, data.TileX, data.TileZ);
        byte[]? piece = data.Data;
        if (piece == null || data.Total <= 0 || data.Total > TerrainTileProtocol.MaxTileBytes || data.Offset < 0 || data.Offset + piece.Length > data.Total)
            return;

        if (data.Offset == 0)
            this.assemblies[key] = new Assembly(new byte[data.Total]);

        if (!this.assemblies.TryGetValue(key, out Assembly? assembly) || assembly.Buffer.Length != data.Total || assembly.Received != data.Offset)
        {
            _ = this.assemblies.Remove(key); // Out of order: wait for the tile to start again.
            return;
        }

        piece.CopyTo(assembly.Buffer, data.Offset);
        assembly.Received += piece.Length;
        if (assembly.Received < data.Total)
            return;

        _ = this.assemblies.Remove(key);
        this.Received++;
        this.ReceivedBytes += data.Total;

        // A tile nobody wants any more is still worth keeping: it crossed the network already.
        if (this.pending.TryGetValue(key, out Pending? entry) && entry.Stage == Stage.Server)
            entry.Stage = Stage.Decoding;

        this.DecodeLater(key, assembly.Buffer, store: true);
    }

    public void OnRefused(TerrainTileRefused refused)
    {
        TerrainTileKey key = new(refused.Step, refused.TileX, refused.TileZ);
        this.Refused++;
        if (this.pending.Remove(key))
            this.Requests.Fail(key, refused.Reason);
    }

    public void OnProgress(TerrainTileProgress progress) => this.LastProgress = progress;

    public void Tick()
    {
        if (this.disposed)
            return;

        if (this.refusedNow.Count > 0)
        {
            TerrainTileKey[] keys = [.. this.refusedNow];
            this.refusedNow.Clear();
            foreach (TerrainTileKey key in keys)
                this.Requests.Fail(key, this.UnavailableReason);
        }

        while (this.diskReads.TryDequeue(out (TerrainTileKey Key, byte[]? Data) read))
        {
            if (!this.pending.TryGetValue(read.Key, out Pending? entry) || entry.Stage != Stage.Disk)
                continue;

            if (read.Data != null)
            {
                this.FromDisk++;
                entry.Stage = Stage.Decoding;
                this.DecodeLater(read.Key, read.Data, store: false);
            }
            else
            {
                this.AskServer(read.Key, entry); // Damaged on disk, and now gone from it.
            }
        }

        while (this.decoded.TryDequeue(out Decoded? result))
        {
            if (result.Data != null)
                this.Remember(result.Key, result.Data);

            if (!this.pending.Remove(result.Key))
                continue;

            if (result.Tile != null)
                this.Requests.Complete(result.Tile);
            else
                this.Requests.Fail(result.Key, result.Error ?? "The tile could not be read.");
        }

        this.SendWants();
    }

    private void SendWants()
    {
        if (!this.Available || (this.adds.Count == 0 && this.drops.Count == 0))
            return;

        // Most urgent first, so the server starts on them first even before priorities are compared.
        KeyValuePair<TerrainTileKey, int>[] added = [.. this.adds.OrderBy(pair => pair.Value)];
        TerrainTileKey[] dropped = [.. this.drops];
        this.adds.Clear();
        this.drops.Clear();
        foreach (KeyValuePair<TerrainTileKey, int> pair in added)
        {
            if (this.pending.TryGetValue(pair.Key, out Pending? entry))
                entry.Sent = true;
        }

        for (int start = 0; start < Math.Max(added.Length, dropped.Length); start += TerrainTileProtocol.MaxKeysPerWant)
        {
            KeyValuePair<TerrainTileKey, int>[] adding = [.. added.Skip(start).Take(TerrainTileProtocol.MaxKeysPerWant)];
            TerrainTileKey[] dropping = [.. dropped.Skip(start).Take(TerrainTileProtocol.MaxKeysPerWant)];
            this.send(new TerrainTileWant
            {
                AddStep = [.. adding.Select(p => p.Key.Step)],
                AddX = [.. adding.Select(p => p.Key.TileX)],
                AddZ = [.. adding.Select(p => p.Key.TileZ)],
                AddPriority = [.. adding.Select(p => p.Value)],
                DropStep = [.. dropping.Select(k => k.Step)],
                DropX = [.. dropping.Select(k => k.TileX)],
                DropZ = [.. dropping.Select(k => k.TileZ)]
            });
        }
    }

    private void DecodeLater(TerrainTileKey key, byte[] data, bool store)
    {
        TerrainTileDiskCache? cache = store ? this.disk : null;
        _ = Task.Run(() =>
        {
            try
            {
                TerrainTile tile = TerrainTileCodec.Decode(data, key);
                cache?.Store(key, data);
                this.decoded.Enqueue(new Decoded(key, tile, null, data));
            }
            catch (Exception ex)
            {
                this.log?.Invoke($"tile {key} from the server could not be read: {ex.Message}");
                this.decoded.Enqueue(new Decoded(key, null, $"The tile arrived damaged: {ex.Message}", null));
            }
        });
    }

    private void Remember(TerrainTileKey key, byte[] data)
    {
        if (this.recent.Remove(key, out (byte[] Data, long Used) previous))
            this.recentBytes -= previous.Data.Length;

        this.recent[key] = (data, ++this.serial);
        this.recentBytes += data.Length;
        if (this.recentBytes <= RecentLimit)
            return;

        foreach (KeyValuePair<TerrainTileKey, (byte[] Data, long Used)> old in this.recent.OrderBy(pair => pair.Value.Used).ToArray())
        {
            if (this.recentBytes <= RecentLimit)
                break;

            _ = this.recent.Remove(old.Key);
            this.recentBytes -= old.Value.Data.Length;
        }
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        this.disk?.Dispose();
        this.disk = null;
    }

    private enum Stage
    {
        Disk,
        Server,
        Decoding
    }

    private sealed class Pending
    {
        public Stage Stage;
        public int Priority;
        public bool Sent;
    }

    private sealed class Assembly
    {
        public readonly byte[] Buffer;
        public int Received;

        public Assembly(byte[] buffer) => this.Buffer = buffer;
    }

    private sealed record Decoded(TerrainTileKey Key, TerrainTile? Tile, string? Error, byte[]? Data);
}
