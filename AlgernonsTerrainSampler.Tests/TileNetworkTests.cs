using System.IO;
using System.Reflection;
using AlgernonsTerrainSampler.Tiles;
using ProtoBuf;

namespace AlgernonsTerrainSampler.Tests;

public class TileRequestTests
{
    private sealed class Source : ITerrainTileSource
    {
        public readonly List<(TerrainTileKey Key, int Priority)> Wants = [];
        public readonly List<TerrainTileKey> Drops = [];

        public void Want(TerrainTileKey key, int priority) => this.Wants.Add((key, priority));

        public void Drop(TerrainTileKey key) => this.Drops.Add(key);
    }

    private static readonly TerrainTileKey Key = new(4, 10, 20);

    [Fact]
    public void Mods_asking_for_one_tile_ask_the_source_once_at_their_most_urgent()
    {
        Source source = new();
        TerrainTileRequests requests = new(source);
        long first = requests.Request(Key, 5, _ => { }, null);
        long second = requests.Request(Key, 9, _ => { }, null);
        long third = requests.Request(Key, 1, _ => { }, null);

        Assert.Equal([(Key, 5), (Key, 1)], source.Wants);
        requests.Cancel(first);
        requests.Cancel(second);
        Assert.Empty(source.Drops);
        requests.Cancel(third);
        Assert.Equal([Key], source.Drops);
        Assert.Equal(0, requests.Pending);
    }

    [Fact]
    public void Every_asker_gets_the_tile_even_if_one_of_them_throws()
    {
        TerrainTileRequests requests = new(new Source());
        int ready = 0;
        _ = requests.Request(Key, 0, _ => throw new InvalidOperationException("consumer bug"), null);
        _ = requests.Request(Key, 0, _ => ready++, null);
        requests.Complete(new TerrainTile(Key));

        Assert.Equal(1, ready);
        Assert.Equal(0, requests.Pending);
    }

    [Fact]
    public void A_refusal_reaches_every_asker_and_a_cancelled_one_hears_nothing()
    {
        TerrainTileRequests requests = new(new Source());
        List<string> reasons = [];
        _ = requests.Request(Key, 0, _ => { }, reasons.Add);
        long cancelled = requests.Request(Key, 0, _ => { }, reasons.Add);
        requests.Cancel(cancelled);
        requests.Fail(Key, "too far");

        Assert.Equal(["too far"], reasons);
    }

    [Fact]
    public void A_mod_without_a_reference_can_pass_an_action_of_object()
    {
        // As a consumer binding by reflection would: Action<object> for Action<TerrainTile>, by delegate variance.
        TerrainTileRequests requests = new(new Source());
        MethodInfo request = typeof(TerrainTileRequests).GetMethod("Request", BindingFlags.Instance | BindingFlags.Public)!;
        object? received = null;
        Action<object> ready = tile => received = tile;
        Action<string> refused = _ => { };
        _ = request.Invoke(requests, [Key, 0, ready, refused]);
        requests.Complete(new TerrainTile(Key));

        TerrainTile tile = Assert.IsType<TerrainTile>(received);
        FieldInfo height = tile.GetType().GetField("Height")!;
        Assert.Equal(TerrainTileKey.ColumnCount, ((short[])height.GetValue(tile)!).Length);
    }
}

/// <summary>A server and clients joined by real protobuf serialisation, as the channel would carry them.</summary>
public class TileNetworkTests
{
    private const int World = 1024000;
    private const string Player = "player-uid";
    private static readonly string Identity = TerrainTileIdentity.Hash(writer => writer.Write("network world"));

    private static T Wire<T>(T message)
    {
        using MemoryStream stream = new();
        Serializer.Serialize(stream, message);
        stream.Position = 0;
        return Serializer.Deserialize<T>(stream);
    }

    private sealed class Server : IDisposable
    {
        public readonly FakeTerrain Terrain = new();
        public readonly Queue<object> ToClient = new();
        public readonly TerrainTileService Service;
        public readonly TerrainTileSender Sender;
        public (double X, double Z) Position = (512000, 512000);

        public Server()
        {
            this.Sender = new TerrainTileSender(
                (_, data) => this.ToClient.Enqueue(Wire(data)),
                (_, refused) => this.ToClient.Enqueue(Wire(refused)),
                (_, progress) => this.ToClient.Enqueue(Wire(progress)));
            this.Service = new TerrainTileService(
                World,
                World,
                this.Terrain.Sample,
                null,
                new TerrainTileService.Settings(2, 64, 1024, 4096),
                _ => new TerrainTileOwner(true, "", true, this.Position.X, this.Position.Z),
                this.Sender.Enqueue,
                this.Sender.Refuse);
        }

        public TerrainTileStatus Status => Wire(new TerrainTileStatus { Available = true, Identity = Identity, MaxWantedTiles = 1024, MaxDistancePerStep = 4096 });

        public void Tick(long now)
        {
            this.Service.Tick();
            this.Sender.Tick(64 * 1024, now, this.Service.WantedBy, this.Service.Running, this.Service.Waiting);
        }

        public void Dispose() => this.Service.Dispose();
    }

    private sealed class Client : IDisposable
    {
        public readonly Queue<TerrainTileWant> ToServer = new();
        public readonly TerrainTileClientCore Core;
        public readonly List<TerrainTile> Tiles = [];
        public readonly List<string> Refusals = [];
        public int WantsSent;
        public int Hellos;

        public Client(string? diskRoot)
        {
            this.Core = new TerrainTileClientCore(
                want =>
                {
                    this.WantsSent++;
                    this.ToServer.Enqueue(Wire(want));
                },
                identity => diskRoot == null ? null : new TerrainTileDiskCache(diskRoot, identity, 64),
                sendHello: () => this.Hellos++);
        }

        public void Ask(TerrainTileKey key, int priority = 0) => _ = this.Core.Requests.Request(key, priority, this.Tiles.Add, this.Refusals.Add);

        public void Dispose() => this.Core.Dispose();
    }

    // Runs both sides, carrying messages between them, until every request is answered.
    private static void Run(Server server, Client client, int answers)
    {
        long now = 0;
        Wait.Until(() => client.Tiles.Count + client.Refusals.Count >= answers, () =>
        {
            now += 50;
            client.Core.Tick();
            while (client.ToServer.TryDequeue(out TerrainTileWant? want))
                server.Service.Apply(Player, want);

            server.Tick(now);
            while (server.ToClient.TryDequeue(out object? message))
            {
                switch (message)
                {
                    case TerrainTileData data: client.Core.OnData(data); break;
                    case TerrainTileRefused refused: client.Core.OnRefused(refused); break;
                    case TerrainTileProgress progress: client.Core.OnProgress(progress); break;
                }
            }
        }, $"{answers} answer(s)");
    }

    private static List<TerrainTileKey> Square(int step, int radius)
    {
        TerrainTileKey centre = TerrainTileKey.Containing(step, 512000, 512000);
        List<TerrainTileKey> keys = [];
        for (int dz = -radius; dz <= radius; dz++)
        {
            for (int dx = -radius; dx <= radius; dx++)
                keys.Add(new TerrainTileKey(step, centre.TileX + dx, centre.TileZ + dz));
        }

        return keys;
    }

    [Fact]
    public void A_client_gets_the_tiles_round_it_whole_and_exact()
    {
        using Server server = new();
        using Client client = new(null);
        client.Core.OnStatus(server.Status);
        Assert.True(client.Core.Available);

        foreach (TerrainTileKey key in Square(4, 1))
            client.Ask(key);

        Run(server, client, 9);
        Assert.Empty(client.Refusals);
        Assert.Equal(9, client.Tiles.Count);
        Assert.Equal(9L * TerrainTileKey.ColumnCount, server.Terrain.Samples);
        foreach (TerrainTile tile in client.Tiles)
        {
            int last = TerrainTile.Index(TerrainTileKey.Cells, TerrainTileKey.Cells);
            Assert.Equal(FakeTerrain.Column(tile.Key.SampleX(TerrainTileKey.Cells), tile.Key.SampleZ(TerrainTileKey.Cells)).Height, tile.Height[last]);
        }
    }

    [Fact]
    public void A_large_tile_goes_in_fragments_under_the_budget_and_clients_take_turns()
    {
        List<(string Uid, TerrainTileData Data)> sent = [];
        TerrainTileSender sender = new((uid, data) => sent.Add((uid, Wire(data))), (_, _) => { }, (_, _) => { });
        byte[] big = new byte[100 * 1024];
        new Random(7).NextBytes(big);
        sender.Enqueue("a", new TerrainTileKey(4, 1, 1), big);
        sender.Enqueue("b", new TerrainTileKey(4, 2, 2), big);

        sender.Tick(64 * 1024, 0, _ => 0, 0, 0);
        Assert.Equal(["a", "b"], sent.Select(s => s.Uid));
        Assert.All(sent, s => Assert.Equal(TerrainTileProtocol.FragmentBytes, s.Data.Data!.Length));

        for (int tick = 1; tick < 10; tick++)
            sender.Tick(64 * 1024, tick * 50, _ => 0, 0, 0);

        foreach (string uid in new[] { "a", "b" })
        {
            byte[] joined = [.. sent.Where(s => s.Uid == uid).OrderBy(s => s.Data.Offset).SelectMany(s => s.Data.Data!)];
            Assert.Equal(big, joined);
        }

        Assert.Equal(2, sender.TilesSent);
    }

    [Fact]
    public void A_reconnecting_client_reads_its_own_cache_and_asks_the_server_for_nothing()
    {
        using TemporaryFolder folder = new();
        using Server server = new();
        using (Client first = new(folder.Path))
        {
            first.Core.OnStatus(server.Status);
            foreach (TerrainTileKey key in Square(8, 1))
                first.Ask(key);

            Run(server, first, 9);
            Wait.Until(() => first.Core.Disk!.Count == 9, () => { }, "the client's disk writes");
        }

        long sampled = server.Terrain.Samples;
        using Client again = new(folder.Path);
        again.Core.OnStatus(server.Status);
        Wait.Until(() => again.Core.Disk!.Ready, () => { }, "the client's disk index");
        foreach (TerrainTileKey key in Square(8, 1))
            again.Ask(key);

        Run(server, again, 9);
        Assert.Equal(9, again.Core.FromDisk);
        Assert.Equal(0, again.WantsSent);
        Assert.Equal(sampled, server.Terrain.Samples);
    }

    [Fact]
    public void Two_mods_on_one_client_download_a_tile_once()
    {
        using Server server = new();
        using Client client = new(null);
        client.Core.OnStatus(server.Status);
        TerrainTileKey key = Square(4, 0)[0];
        client.Ask(key, 3);
        client.Ask(key, 1);

        Run(server, client, 2);
        Assert.Equal(1, client.Core.Received);
        Assert.Same(client.Tiles[0], client.Tiles[1]);
    }

    [Fact]
    public void A_refusal_crosses_the_wire_with_its_reason()
    {
        using Server server = new();
        using Client client = new(null);
        client.Core.OnStatus(server.Status);
        TerrainTileKey far = TerrainTileKey.Containing(1, 512000 + 20000, 512000);
        client.Ask(far);

        Run(server, client, 1);
        Assert.Contains("4096 blocks", Assert.Single(client.Refusals));
    }

    [Fact]
    public void A_cancelled_tile_is_dropped_on_the_server_too()
    {
        using Server server = new();
        using Client client = new(null);
        client.Core.OnStatus(server.Status);
        TerrainTileKey key = Square(4, 0)[0];
        long handle = client.Core.Requests.Request(key, 0, client.Tiles.Add, client.Refusals.Add);
        client.Core.Tick();
        server.Service.Apply(Player, client.ToServer.Dequeue());
        Assert.Equal(1, server.Service.WantedBy(Player));

        client.Core.Requests.Cancel(handle);
        client.Core.Tick();
        server.Service.Apply(Player, client.ToServer.Dequeue());
        Assert.Equal(0, server.Service.WantedBy(Player));
    }

    [Fact]
    public void Without_a_server_requests_are_refused_on_the_next_tick_with_the_reason()
    {
        using Client client = new(null);
        client.Core.SetWaitingReason("No sampler here.");
        bool answeredInside = false;
        bool insideRequest = true;
        _ = client.Core.Requests.Request(new TerrainTileKey(4, 1, 1), 0, _ => { }, reason =>
        {
            answeredInside = insideRequest;
            client.Refusals.Add(reason);
        });
        insideRequest = false;

        Assert.Empty(client.Refusals);
        client.Core.Tick();
        Assert.Equal(["No sampler here."], client.Refusals);
        Assert.False(answeredInside);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(123456789L)]
    [InlineData(long.MaxValue - 100000)]
    public void A_connected_client_says_hello_at_once_and_again_until_answered(long start)
    {
        using Server server = new();
        using Client client = new(null);
        client.Core.Tick(connected: true, start);
        Assert.Equal(1, client.Hellos);

        client.Core.Tick(connected: true, start + 4999);
        Assert.Equal(1, client.Hellos);
        client.Core.Tick(connected: true, start + 5000);
        Assert.Equal(2, client.Hellos);

        client.Core.OnStatus(server.Status);
        client.Core.Tick(connected: true, start + 20000);
        Assert.Equal(2, client.Hellos);
        Assert.True(client.Core.Available);
    }

    [Fact]
    public void A_client_whose_channel_never_connects_says_why_after_a_while()
    {
        using Client client = new(null);
        client.Core.Tick(connected: false, 1000);
        Assert.Equal("Waiting for the server.", client.Core.UnavailableReason);

        client.Core.Tick(connected: false, 11000);
        Assert.Contains("does not run Algernon's Terrain Sampler", client.Core.UnavailableReason);
        Assert.Equal(0, client.Hellos);
    }

    [Fact]
    public void A_client_that_loses_its_channel_refuses_what_it_waited_for_and_greets_the_next_one()
    {
        using Server server = new();
        using Client client = new(null);
        client.Core.Tick(connected: true, 0);
        client.Core.OnStatus(server.Status);
        client.Ask(Square(4, 0)[0]);
        client.Core.Tick(connected: true, 50);

        client.Core.Tick(connected: false, 100);
        Assert.False(client.Core.Available);
        Assert.Equal(["Disconnected from the server."], client.Refusals);

        client.Core.Tick(connected: true, 150);
        Assert.Equal(2, client.Hellos);
    }

    [Fact]
    public void A_server_that_says_no_is_heard_and_its_reason_kept()
    {
        using Client client = new(null);
        client.Core.Tick(connected: true, 0);
        client.Core.OnStatus(Wire(new TerrainTileStatus { Available = false, Reason = "Not for you." }));
        Assert.False(client.Core.Available);
        Assert.Equal("Not for you.", client.Core.UnavailableReason);

        client.Core.Tick(connected: true, 60000);
        Assert.Equal(1, client.Hellos);
    }

    [Fact]
    public void A_server_on_another_protocol_is_not_used()
    {
        using Client client = new(null);
        client.Core.OnStatus(Wire(new TerrainTileStatus { Protocol = TerrainTileProtocol.Version + 1, Available = true, Identity = Identity }));
        Assert.False(client.Core.Available);
        Assert.Contains("protocol", client.Core.UnavailableReason);
    }
}
