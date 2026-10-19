#nullable enable
using System;
using System.Collections.Generic;

namespace AlgernonsTerrainSampler.Tiles;

/// <summary>
/// The server's outgoing tiles, per client: fragments of at most <see cref="TerrainTileProtocol.FragmentBytes"/>, under a
/// byte budget per tick shared by every client, round robin so that one client's burst cannot starve the rest. A client
/// gets one tile finished before the next is started.
/// </summary>
/// <remarks>Main thread only.</remarks>
internal sealed class TerrainTileSender
{
    private const int ProgressMilliseconds = 5000;

    private readonly Action<string, TerrainTileData> sendData;
    private readonly Action<string, TerrainTileRefused> sendRefused;
    private readonly Action<string, TerrainTileProgress> sendProgress;
    private readonly Dictionary<string, Client> clients = new(StringComparer.Ordinal);
    private readonly List<string> order = [];
    private int cursor;

    public TerrainTileSender(Action<string, TerrainTileData> sendData, Action<string, TerrainTileRefused> sendRefused, Action<string, TerrainTileProgress> sendProgress)
    {
        this.sendData = sendData;
        this.sendRefused = sendRefused;
        this.sendProgress = sendProgress;
    }

    public long BytesSent { get; private set; }

    public long TilesSent { get; private set; }

    public int Clients => this.clients.Count;

    public int Queued(string uid) => this.clients.TryGetValue(uid, out Client? client) ? client.Queue.Count + (client.Current != null ? 1 : 0) : 0;

    public void Enqueue(string uid, TerrainTileKey key, byte[] data) => this.ClientFor(uid).Queue.Enqueue((key, data));

    public void Refuse(string uid, TerrainTileKey key, string reason)
        => this.sendRefused(uid, new TerrainTileRefused { Step = key.Step, TileX = key.TileX, TileZ = key.TileZ, Reason = reason });

    /// <summary>A client that says hello or wants tiles is known; its progress reports start from then.</summary>
    public void Touch(string uid) => _ = this.ClientFor(uid);

    public void Remove(string uid)
    {
        if (this.clients.Remove(uid))
            _ = this.order.Remove(uid);
    }

    /// <summary>Sends up to <paramref name="budgetBytes"/> of tile data, and any progress reports that are due.</summary>
    /// <param name="wanted">How many tiles a client still wants that are not yet on their way.</param>
    public void Tick(int budgetBytes, long now, Func<string, int> wanted, int making, int waiting)
    {
        int sent = 0;
        int idle = 0;
        while (this.order.Count > 0 && idle < this.order.Count && sent < budgetBytes)
        {
            this.cursor %= this.order.Count;
            Client client = this.clients[this.order[this.cursor]];
            if (client.Current == null)
            {
                if (!client.Queue.TryDequeue(out (TerrainTileKey Key, byte[] Data) next))
                {
                    idle++;
                    this.cursor++;
                    continue;
                }

                client.Current = next;
            }

            idle = 0;
            (TerrainTileKey key, byte[] data) = client.Current.Value;
            int count = Math.Min(TerrainTileProtocol.FragmentBytes, data.Length - client.Offset);
            this.sendData(client.Uid, new TerrainTileData
            {
                Step = key.Step,
                TileX = key.TileX,
                TileZ = key.TileZ,
                Total = data.Length,
                Offset = client.Offset,
                Data = data.AsSpan(client.Offset, count).ToArray()
            });

            client.Offset += count;
            sent += count;
            this.BytesSent += count;
            if (client.Offset >= data.Length)
            {
                client.Current = null;
                client.Offset = 0;
                this.TilesSent++;
            }

            this.cursor++;
        }

        foreach (string uid in this.order.ToArray())
        {
            Client client = this.clients[uid];
            int left = wanted(client.Uid);
            int sending = this.Queued(client.Uid);
            if ((left > 0 || sending > 0) && now - client.LastProgress >= ProgressMilliseconds)
            {
                client.LastProgress = now;
                this.sendProgress(client.Uid, new TerrainTileProgress { Wanted = left, Sending = sending, Making = making, Waiting = waiting });
            }
        }
    }

    private Client ClientFor(string uid)
    {
        if (!this.clients.TryGetValue(uid, out Client? client))
        {
            client = new Client(uid);
            this.clients[uid] = client;
            this.order.Add(uid);
        }

        return client;
    }

    private sealed class Client
    {
        public readonly string Uid;
        public readonly Queue<(TerrainTileKey Key, byte[] Data)> Queue = new();
        public (TerrainTileKey Key, byte[] Data)? Current;
        public int Offset;
        public long LastProgress;

        public Client(string uid) => this.Uid = uid;
    }
}
