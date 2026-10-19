#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace AlgernonsTerrainSampler.Tiles;

/// <summary>Where a side's tiles come from: the service on the server, the channel and its caches on a client.</summary>
internal interface ITerrainTileSource
{
    /// <summary>Wants the tile, or wants it sooner. Answered with <see cref="TerrainTileRequests.Complete"/> or <see cref="TerrainTileRequests.Fail"/>.</summary>
    void Want(TerrainTileKey key, int priority);

    /// <summary>Nobody on this side wants the tile any more.</summary>
    void Drop(TerrainTileKey key);
}

/// <summary>
/// Every consumer mod's requests on one side, gathered by tile: however many mods ask for a tile, the source is asked
/// once, at the most urgent of their priorities, and every one of them gets the same tile back.
/// </summary>
/// <remarks>Main thread only, as are the callbacks it makes.</remarks>
internal sealed class TerrainTileRequests
{
    private readonly ITerrainTileSource source;
    private readonly Action<string>? log;
    private readonly Dictionary<long, Entry> requests = [];
    private readonly Dictionary<TerrainTileKey, HashSet<long>> byKey = [];
    private readonly Dictionary<TerrainTileKey, int> asked = [];
    private long nextHandle;

    public TerrainTileRequests(ITerrainTileSource source, Action<string>? log = null)
    {
        this.source = source;
        this.log = log;
    }

    public int Pending => this.requests.Count;

    public int Tiles => this.byKey.Count;

    public bool IsWanted(TerrainTileKey key) => this.byKey.ContainsKey(key);

    public long Request(TerrainTileKey key, int priority, Action<TerrainTile> ready, Action<string>? refused)
    {
        long handle = ++this.nextHandle;
        this.requests[handle] = new Entry(key, priority, ready, refused);
        if (!this.byKey.TryGetValue(key, out HashSet<long>? handles))
        {
            handles = [];
            this.byKey[key] = handles;
        }

        _ = handles.Add(handle);
        if (!this.asked.TryGetValue(key, out int current) || priority < current)
        {
            this.asked[key] = priority;
            this.source.Want(key, priority);
        }

        return handle;
    }

    public void Cancel(long handle)
    {
        if (!this.requests.Remove(handle, out Entry? request))
            return;

        HashSet<long> handles = this.byKey[request.Key];
        _ = handles.Remove(handle);
        if (handles.Count == 0)
        {
            _ = this.byKey.Remove(request.Key);
            _ = this.asked.Remove(request.Key);
            this.source.Drop(request.Key);
        }
    }

    public void Complete(TerrainTile tile)
    {
        foreach (Entry request in this.Take(tile.Key))
        {
            try
            {
                request.Ready(tile);
            }
            catch (Exception ex)
            {
                this.log?.Invoke($"a consumer's tile callback threw: {ex}");
            }
        }
    }

    public void Fail(TerrainTileKey key, string reason)
    {
        foreach (Entry request in this.Take(key))
        {
            try
            {
                request.Refused?.Invoke(reason);
            }
            catch (Exception ex)
            {
                this.log?.Invoke($"a consumer's refusal callback threw: {ex}");
            }
        }
    }

    /// <summary>Refuses everything outstanding, as when the service goes away.</summary>
    public void FailAll(string reason)
    {
        foreach (TerrainTileKey key in this.byKey.Keys.ToArray())
            this.Fail(key, reason);
    }

    private List<Entry> Take(TerrainTileKey key)
    {
        _ = this.asked.Remove(key);
        if (!this.byKey.Remove(key, out HashSet<long>? handles))
            return [];

        List<Entry> taken = new(handles.Count);
        foreach (long handle in handles)
        {
            if (this.requests.Remove(handle, out Entry? request))
                taken.Add(request);
        }

        return taken;
    }

    private sealed record Entry(TerrainTileKey Key, int Priority, Action<TerrainTile> Ready, Action<string>? Refused);
}
