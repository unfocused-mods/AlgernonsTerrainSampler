#nullable enable
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace AlgernonsTerrainSampler.Tiles;

/// <summary>
/// Terrain tiles for other mods, on either side. On the server, tiles come from the sampler through a scheduler, a memory
/// cache and a disk cache, and go out to clients over this mod's channel. On a client, they come from the server, through
/// the client's own caches. In single player both halves run, and the client uses the server's cache.
/// </summary>
/// <remarks>
/// <para>Ask <see cref="ServerInstance"/> on the server, <see cref="ClientInstance"/> on a client. Wait for
/// <see cref="Available"/>, or for <see cref="AvailabilityChanged"/>, then <see cref="RequestTile"/>; every call and every
/// callback is on the main thread. Tiles are read only and shared between consumers.</para>
/// <para>The signatures use only .NET's own types and <see cref="TerrainTile"/>, so a mod can call this by reflection with
/// no compile-time reference: an <c>Action&lt;object&gt;</c> may be passed for <c>Action&lt;TerrainTile&gt;</c>.</para>
/// </remarks>
public sealed class TerrainTileSystem : ModSystem
{
    // Not a possible player UID, so mods on the server never share an owner with a player.
    private const string LocalOwner = "\u0001server";
    private const string LogPrefix = "AlgernonsTerrainSampler: ";

    private readonly ConcurrentQueue<TerrainTile> localDecoded = new();
    private readonly ConcurrentQueue<(TerrainTileKey Key, string Reason)> localRefused = new();
    private readonly ConcurrentQueue<Action> deferred = new();
    private TerrainTileConfig config = new();
    private TerrainTileRequests? requests;

    private ICoreServerAPI? sapi;
    private IServerNetworkChannel? serverChannel;
    private TerrainTileService? service;
    private TerrainTileSender? sender;
    private long serverTickListener;
    private Process? process;
    private long lastTick;
    private long lastCpuSample;
    private double lastCpuMilliseconds;
    private double cpuPercent;
    private long lastStatsLog;
    private bool tickFailed;

    private ICoreClientAPI? capi;
    private IClientNetworkChannel? clientChannel;
    private TerrainTileClientCore? client;
    private long clientTickListener;
    private long lastHello = long.MinValue;
    private bool wasConnected;

    /// <summary>The server's tile service, once the server has started; null on a client and before then.</summary>
    public static TerrainTileSystem? ServerInstance { get; private set; }

    /// <summary>The client's tile service; null on a server.</summary>
    public static TerrainTileSystem? ClientInstance { get; private set; }

    /// <summary>Cells across every tile. A tile at step s spans <c>TileCells * s</c> blocks.</summary>
    public static int TileCells => TerrainTileKey.Cells;

    /// <summary>Samples across every tile: <see cref="TileCells"/> + 1, the last shared with the next tile.</summary>
    public static int TileSamples => TerrainTileKey.Samples;

    /// <summary>Whether tiles can be asked for. On a client: whether the server serves them to this player.</summary>
    public bool Available { get; private set; }

    /// <summary>Why not, when not <see cref="Available"/>.</summary>
    public string UnavailableReason { get; private set; } = "The terrain tile service has not started.";

    /// <summary>The cache identity: the same identity means the same tiles. Empty when not available.</summary>
    public string Identity { get; private set; } = "";

    /// <summary>Raised on the main thread when <see cref="Available"/> changes.</summary>
    public event Action? AvailabilityChanged;

    public override bool ShouldLoad(EnumAppSide side) => true;

    public override double ExecuteOrder() => 0.02;

    /// <summary>The index of the tile at <paramref name="step"/> holding world coordinate <paramref name="world"/>, on either axis.</summary>
    public static int TileIndexOf(int step, int world) => (int)Math.Floor(world / (double)(TerrainTileKey.Cells * step));

    /// <summary>
    /// Asks for the tile at <paramref name="step"/> (a power of two from 1 to 64) whose corner is
    /// (<c>tileX * TileCells * step</c>, <c>tileZ * TileCells * step</c>). Lower priorities come first. Exactly one of the
    /// callbacks runs, on the main thread, never before this returns, unless the request is cancelled first.
    /// </summary>
    /// <returns>A handle for <see cref="CancelTile"/>.</returns>
    public long RequestTile(int step, int tileX, int tileZ, int priority, Action<TerrainTile> ready, Action<string> refused)
    {
        ArgumentNullException.ThrowIfNull(ready);
        if (this.requests == null)
        {
            // Before the service starts there is nothing to ask; the refusal still waits for the next tick.
            string reason = this.UnavailableReason;
            this.deferred.Enqueue(() => refused?.Invoke(reason));
            return 0;
        }

        return this.requests.Request(new TerrainTileKey(step, tileX, tileZ), priority, ready, refused);
    }

    /// <summary>No longer wants the tile; neither callback will run. Unknown or finished handles are ignored.</summary>
    public void CancelTile(long handle) => this.requests?.Cancel(handle);

    public override void StartServerSide(ICoreServerAPI api)
    {
        this.sapi = api;
        ServerInstance = this;
        this.config = LoadConfig(api, store: true);
        this.serverChannel = api.Network.RegisterChannel(TerrainTileProtocol.ChannelName)
            .RegisterMessageType<TerrainTileHello>()
            .RegisterMessageType<TerrainTileStatus>()
            .RegisterMessageType<TerrainTileWant>()
            .RegisterMessageType<TerrainTileProgress>()
            .RegisterMessageType<TerrainTileData>()
            .RegisterMessageType<TerrainTileRefused>()
            .SetMessageHandler<TerrainTileHello>(this.OnHello)
            .SetMessageHandler<TerrainTileWant>(this.OnWant);

        this.sender = new TerrainTileSender(
            (uid, data) => this.SendTo(uid, data),
            (uid, refused) => this.SendTo(uid, refused),
            (uid, progress) => this.SendTo(uid, progress));

        // The sampler finds the game's generators at WorldReady; RunGame is after it.
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, this.StartService);
        api.Event.ServerRunPhase(EnumServerRunPhase.Shutdown, this.StopService);
        api.Event.PlayerDisconnect += this.OnPlayerDisconnect;
        this.serverTickListener = api.Event.RegisterGameTickListener(this.OnServerTick, 50);
    }

    // The tile service is an extra: nothing in it may stop the server from starting.
    private void StartService()
    {
        try
        {
            this.TryStartService();
        }
        catch (Exception ex)
        {
            this.service?.Dispose();
            this.service = null;
            this.requests = null;
            this.SetUnavailable($"The terrain tile service failed to start: {ex.GetBaseException().Message}");
            this.sapi!.Logger.Error("{0}", LogPrefix + "the terrain tile service failed to start; the server runs on without it: " + ex);
        }
    }

    private void TryStartService()
    {
        ICoreServerAPI api = this.sapi!;
        TerrainSamplerMod? mod = TerrainSamplerMod.Instance;
        if (mod?.GenTerra == null)
        {
            this.SetUnavailable("The terrain sampler did not start.");
            return;
        }

        if (!mod.GenTerra.IsReady)
        {
            this.SetUnavailable("This world's terrain is not one the sampler knows: only worlds of the standard type can be sampled.");
            api.Logger.Notification("{0}", LogPrefix + "terrain tile service off: " + this.UnavailableReason);
            return;
        }

        TerrainColumnSample Sample(int x, int z) => mod.SampleColumnOrThrow(new WorldMapCoordinate(x, z));

        string identity;
        try
        {
            identity = TerrainTileWorldIdentity.For(api, mod, this.config.CacheRevision, Sample);
        }
        catch (Exception ex)
        {
            this.SetUnavailable($"The sampler failed its first samples: {ex.GetBaseException().Message}");
            api.Logger.Error("{0}", LogPrefix + "terrain tile service off, the sampler failed its first samples: " + ex);
            return;
        }

        TerrainTileDiskCache? disk = null;
        if (this.config.DiskCacheMegabytes > 0)
        {
            string folder = api.GetOrCreateDataPath(Path.Combine("ModData", "algernonsterrainsampler", SafeFolderName(api.World.SavegameIdentifier)));
            disk = new TerrainTileDiskCache(folder, identity, this.config.DiskCacheMegabytes, this.Log);
        }

        int workers = this.config.Workers > 0 ? this.config.Workers
            : api.Server.IsDedicated ? 1 : Math.Clamp(Environment.ProcessorCount - 4, 1, 4);

        // Watersheds has not been checked for thread safety, so with it the sampling stays on the main thread, a few
        // milliseconds a tick. Encoding and writing still run on the workers.
        bool mainThread = mod.WatershedsLoaded;
        this.service = new TerrainTileService(
            api.WorldManager.MapSizeX,
            api.WorldManager.MapSizeZ,
            Sample,
            disk,
            new TerrainTileService.Settings(workers, this.config.MemoryCacheMegabytes, this.config.MaxWantedTiles, this.config.MaxDistancePerStep, mainThread),
            this.OwnerInfo,
            this.OnTileReady,
            this.OnTileRefused,
            this.Log);

        this.requests = new TerrainTileRequests(new LocalSource(this.service), this.Log);
        this.process = Process.GetCurrentProcess();
        this.lastCpuMilliseconds = this.process.TotalProcessorTime.TotalMilliseconds;
        this.lastTick = this.lastCpuSample = Environment.TickCount64;
        this.Identity = identity;
        this.SetAvailable();

        api.Logger.Notification("{0}", LogPrefix + string.Create(
            CultureInfo.InvariantCulture,
            $"terrain tile service ready: {workers} worker(s){(mainThread ? ", sampling on the main thread for Watersheds" : "")}, disk cache {(disk == null ? "off" : $"{this.config.DiskCacheMegabytes} MiB")}, network sampling {(this.config.NetworkSampling ? "on" : "off")}, identity {identity[..12]}."));
    }

    private void StopService()
    {
        if (this.service == null)
            return;

        this.service.Dispose();
        this.service = null;
        this.requests?.FailAll("The server is stopping.");
        this.requests = null;
        this.process?.Dispose();
        this.process = null;
        this.SetUnavailable("The server is stopping.");
    }

    private TerrainTileOwner OwnerInfo(string owner)
    {
        if (owner == LocalOwner)
            return TerrainTileOwner.Unlimited;

        if (!this.config.NetworkSampling)
            return TerrainTileOwner.Denied($"This server does not serve terrain tiles to clients: NetworkSampling is off in {TerrainTileConfig.FileName}.");

        if (this.sapi!.World.PlayerByUid(owner) is not IServerPlayer player)
            return TerrainTileOwner.Denied("Not connected.");

        if (!string.IsNullOrWhiteSpace(this.config.RequiredPrivilege) && !player.HasPrivilege(this.config.RequiredPrivilege))
            return TerrainTileOwner.Denied($"Terrain tiles on this server need the '{this.config.RequiredPrivilege}' privilege.");

        EntityPos position = player.Entity?.Pos ?? this.sapi.World.DefaultSpawnPosition;
        return new TerrainTileOwner(true, "", true, position?.X ?? 0, position?.Z ?? 0);
    }

    private void OnTileReady(string owner, TerrainTileKey key, byte[] data)
    {
        if (owner != LocalOwner)
        {
            this.sender!.Enqueue(owner, key, data);
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                this.localDecoded.Enqueue(TerrainTileCodec.Decode(data, key));
            }
            catch (Exception ex)
            {
                this.localRefused.Enqueue((key, $"The tile could not be read: {ex.Message}"));
            }
        });
    }

    private void OnTileRefused(string owner, TerrainTileKey key, string reason)
    {
        // A server mod hears on the next tick, never inside its own request.
        if (owner == LocalOwner)
            this.localRefused.Enqueue((key, reason));
        else
            this.sender!.Refuse(owner, key, reason);
    }

    private void OnHello(IServerPlayer player, TerrainTileHello hello)
    {
        string reason;
        bool available;
        if (hello.Protocol != TerrainTileProtocol.Version)
        {
            available = false;
            reason = $"This server speaks tile protocol {TerrainTileProtocol.Version}, the client {hello.Protocol}. Use the same terrain sampler version on both.";
        }
        else if (this.service == null)
        {
            available = false;
            reason = this.UnavailableReason;
        }
        else
        {
            TerrainTileOwner info = this.OwnerInfo(player.PlayerUID);
            available = info.Allowed;
            reason = info.DeniedReason;
        }

        this.serverChannel!.SendPacket(new TerrainTileStatus
        {
            Available = available,
            Reason = reason,
            Identity = available ? this.Identity : "",
            MaxWantedTiles = this.config.MaxWantedTiles,
            MaxDistancePerStep = this.config.MaxDistancePerStep
        }, player);
    }

    private void OnWant(IServerPlayer player, TerrainTileWant want)
    {
        string uid = player.PlayerUID;
        int adds = Math.Min(want.AddCount, TerrainTileProtocol.MaxKeysPerWant);
        if (this.service == null)
        {
            for (int i = 0; i < adds; i++)
                this.sender!.Refuse(uid, new TerrainTileKey(want.AddStep![i], want.AddX![i], want.AddZ![i]), this.UnavailableReason);
            return;
        }

        this.service.Apply(uid, want);
    }

    private void OnPlayerDisconnect(IServerPlayer player)
    {
        this.service?.DropOwner(player.PlayerUID);
        this.sender?.Remove(player.PlayerUID);
    }

    // A player who has gone is dropped by the disconnect event, never from here: the sender may be walking its clients.
    private void SendTo<T>(string uid, T message)
    {
        if (this.sapi?.World.PlayerByUid(uid) is IServerPlayer player && player.ConnectionState != EnumClientState.Offline)
            this.serverChannel?.SendPacket(message, player);
    }

    private void OnServerTick(float dt)
    {
        long now = Environment.TickCount64;
        long interval = now - this.lastTick;
        this.lastTick = now;

        this.RunDeferred();
        while (this.localRefused.TryDequeue(out (TerrainTileKey Key, string Reason) refusal))
            this.requests?.Fail(refusal.Key, refusal.Reason);

        if (this.service == null)
            return;

        if (this.process != null && now - this.lastCpuSample >= 1000)
        {
            this.process.Refresh();
            double cpu = this.process.TotalProcessorTime.TotalMilliseconds;
            this.cpuPercent = Math.Clamp((cpu - this.lastCpuMilliseconds) * 100 / ((now - this.lastCpuSample) * (double)Environment.ProcessorCount), 0, 100);
            this.lastCpuMilliseconds = cpu;
            this.lastCpuSample = now;
        }

        TerrainTileService current = this.service;
        try
        {
            current.UpdateLoad(this.cpuPercent, interval);
            current.Tick();
            this.sender!.Tick(this.config.TransferKilobytesPerTick * 1024, now, uid => current.WantedBy(uid), current.Running, current.Waiting);

            while (this.localDecoded.TryDequeue(out TerrainTile? tile))
                this.requests?.Complete(tile);
        }
        catch (Exception ex)
        {
            // Logged once, not twenty times a second; the next tick tries again.
            if (!this.tickFailed)
                this.sapi!.Logger.Error("{0}", LogPrefix + "the terrain tile service's tick failed: " + ex);

            this.tickFailed = true;
        }

        if (this.config.LogTileStats && now - this.lastStatsLog >= 60000 && (current.Running > 0 || current.Waiting > 0 || this.sender!.Clients > 0))
        {
            this.lastStatsLog = now;
            this.Log($"tile stats: {this.Describe().Replace('\n', ' ')}");
        }
    }

    /// <summary><c>/terrainsampler tilefetch [step] [radius]</c>: tiles round the caller, or spawn from the console, made as a server mod would ask for them.</summary>
    internal TextCommandResult ServerFetch(TextCommandCallingArgs args)
    {
        ICoreServerAPI api = this.sapi!;
        int step = (int)args[0];
        int radius = Math.Clamp((int)args[1], 0, 16);
        if (!TerrainTileKey.IsValidStep(step))
            return TextCommandResult.Error("The step is a power of two from 1 to 64.");

        if (!this.Available || this.service == null)
            return TextCommandResult.Error($"Terrain tiles are unavailable: {this.UnavailableReason}");

        EntityPos position = args.Caller.Player?.Entity?.Pos ?? api.World.DefaultSpawnPosition;
        TerrainTileKey centre = TerrainTileKey.Containing(step, (int)position.X, (int)position.Z);
        TerrainTileService current = this.service;
        long madeBefore = current.Generated, bytesBefore = current.GeneratedBytes, diskBefore = current.DiskHits, memoryBefore = current.MemoryHits;
        double sampleBefore = current.GenerateMilliseconds;
        int total = ((2 * radius) + 1) * ((2 * radius) + 1);
        int ready = 0, refused = 0;
        string firstRefusal = "";
        IServerPlayer? caller = args.Caller.Player as IServerPlayer;
        Stopwatch timer = Stopwatch.StartNew();

        void Report()
        {
            if (ready + refused < total)
                return;

            long made = current.Generated - madeBefore;
            string message = string.Create(
                CultureInfo.InvariantCulture,
                $"Terrain tiles at step {step} round {centre.TileX}, {centre.TileZ}: {ready} of {total} in {timer.ElapsedMilliseconds} ms; {made} made ({(made > 0 ? (current.GenerateMilliseconds - sampleBefore) / made : 0):0} ms each to sample), {current.DiskHits - diskBefore} from disk, {current.MemoryHits - memoryBefore} from memory; {(made > 0 ? (current.GeneratedBytes - bytesBefore) / 1024.0 / made : 0):0.0} KiB a tile made{(refused > 0 ? $"; {refused} refused: {firstRefusal}" : "")}.");
            this.Log(message);
            caller?.SendMessage(GlobalConstants.GeneralChatGroup, message, EnumChatType.Notification);
        }

        for (int dz = -radius; dz <= radius; dz++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                TerrainTileKey key = new(step, centre.TileX + dx, centre.TileZ + dz);
                _ = this.RequestTile(key.Step, key.TileX, key.TileZ, (dx * dx) + (dz * dz),
                    tile =>
                    {
                        ready++;
                        Report();
                    },
                    reason =>
                    {
                        refused++;
                        if (firstRefusal.Length == 0)
                            firstRefusal = reason;
                        Report();
                    });
            }
        }

        return TextCommandResult.Success(string.Create(CultureInfo.InvariantCulture, $"Asked for {total} tile(s) at step {step} round {centre.TileX}, {centre.TileZ}; the server log says when they are done."));
    }

    /// <summary>The service's state, for <c>/terrainsampler tiles</c>.</summary>
    public string Describe()
    {
        if (this.service == null)
            return $"The terrain tile service is not running: {this.UnavailableReason}";

        StringBuilder text = new();
        _ = text.Append(CultureInfo.InvariantCulture, $"Terrain tiles, identity {this.Identity[..12]}, network sampling {(this.config.NetworkSampling ? "on" : "off")}, game process at {this.cpuPercent:0}% CPU.\n");
        _ = text.Append(this.service.Describe());
        _ = text.Append(CultureInfo.InvariantCulture, $"\nSent: {this.sender!.TilesSent} tiles, {this.sender.BytesSent / (1024.0 * 1024.0):0.0} MiB, to {this.sender.Clients} client(s) seen.");
        return text.ToString();
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        this.capi = api;
        ClientInstance = this;
        this.config = LoadConfig(api, store: false);
        this.client = new TerrainTileClientCore(want => this.clientChannel?.SendPacket(want), this.OpenClientDisk, this.Log);
        this.client.AvailabilityChanged += this.SyncClient;
        this.requests = this.client.Requests;
        this.UnavailableReason = this.client.UnavailableReason;

        this.clientChannel = api.Network.RegisterChannel(TerrainTileProtocol.ChannelName)
            .RegisterMessageType<TerrainTileHello>()
            .RegisterMessageType<TerrainTileStatus>()
            .RegisterMessageType<TerrainTileWant>()
            .RegisterMessageType<TerrainTileProgress>()
            .RegisterMessageType<TerrainTileData>()
            .RegisterMessageType<TerrainTileRefused>()
            .SetMessageHandler<TerrainTileStatus>(this.client.OnStatus)
            .SetMessageHandler<TerrainTileProgress>(this.client.OnProgress)
            .SetMessageHandler<TerrainTileData>(this.client.OnData)
            .SetMessageHandler<TerrainTileRefused>(this.client.OnRefused);

        this.clientTickListener = api.Event.RegisterGameTickListener(this.OnClientTick, 50);
        this.RegisterClientCommands(api);
    }

    private void OnClientTick(float dt)
    {
        try
        {
            this.ClientTick();
        }
        catch (Exception ex)
        {
            if (!this.tickFailed)
                this.capi!.Logger.Error("{0}", LogPrefix + "the terrain tile client's tick failed: " + ex);

            this.tickFailed = true;
        }
    }

    private void ClientTick()
    {
        this.RunDeferred();
        TerrainTileClientCore core = this.client!;
        if (!this.clientChannel!.Connected)
        {
            if (this.wasConnected)
                core.OnDisconnected("Disconnected from the server.");

            this.wasConnected = false;
            if (!core.Available)
            {
                core.SetWaitingReason("This server does not serve terrain tiles: it does not run Algernon's Terrain Sampler 1.4.0 or later.");
                this.UnavailableReason = core.UnavailableReason;
            }

            core.Tick();
            return;
        }

        this.wasConnected = true;
        long now = Environment.TickCount64;
        if (core.Status == null && now - this.lastHello >= 5000)
        {
            this.lastHello = now;
            this.clientChannel.SendPacket(new TerrainTileHello());
        }

        core.Tick();
    }

    private void SyncClient()
    {
        TerrainTileClientCore core = this.client!;
        this.Available = core.Available;
        this.UnavailableReason = core.UnavailableReason;
        this.Identity = core.Identity;
        this.capi?.Logger.Notification("{0}", LogPrefix + (core.Available ? $"the server serves terrain tiles, identity {core.Identity[..12]}." : $"terrain tiles unavailable: {core.UnavailableReason}"));
        this.RaiseAvailabilityChanged();
    }

    private TerrainTileDiskCache? OpenClientDisk(string identity)
    {
        // In single player the server's cache is on this machine already.
        ICoreClientAPI api = this.capi!;
        if (api.IsSinglePlayer || this.config.ClientDiskCacheMegabytes <= 0)
            return null;

        try
        {
            string root = api.GetOrCreateDataPath(Path.Combine("ModData", "algernonsterrainsampler", "remote"));
            return new TerrainTileDiskCache(root, identity, this.config.ClientDiskCacheMegabytes, this.Log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            this.Log($"no client tile cache: {ex.Message}");
            return null;
        }
    }

    private void RegisterClientCommands(ICoreClientAPI api)
    {
        _ = api.ChatCommands.Create("terrainsampler")
            .WithDescription("Terrain Sampler, this client's side")
            .BeginSubCommand("tiles")
                .WithDescription("Show whether the server serves terrain tiles, and what this client has had from it")
                .HandleWith(_ => TextCommandResult.Success(this.DescribeClient()))
            .EndSubCommand()
            .BeginSubCommand("fetch")
                .WithDescription("Ask for the square of tiles round you, and report how long they took")
                .WithArgs(api.ChatCommands.Parsers.OptionalInt("step", 4), api.ChatCommands.Parsers.OptionalInt("radius", 1))
                .HandleWith(this.CmdFetch)
            .EndSubCommand();
    }

    private TextCommandResult CmdFetch(TextCommandCallingArgs args)
    {
        ICoreClientAPI api = this.capi!;
        int step = (int)args[0];
        int radius = Math.Clamp((int)args[1], 0, 8);
        if (!TerrainTileKey.IsValidStep(step))
            return TextCommandResult.Error("The step is a power of two from 1 to 64.");

        if (!this.Available)
            return TextCommandResult.Error($"Terrain tiles are unavailable: {this.UnavailableReason}");

        EntityPos position = api.World.Player.Entity.Pos;
        TerrainTileKey centre = TerrainTileKey.Containing(step, (int)position.X, (int)position.Z);
        TerrainTileClientCore core = this.client!;
        long diskBefore = core.FromDisk, memoryBefore = core.FromMemory, receivedBefore = core.Received, bytesBefore = core.ReceivedBytes;
        int total = ((2 * radius) + 1) * ((2 * radius) + 1);
        int ready = 0, refused = 0;
        string firstRefusal = "";
        string centreHeights = "";
        Stopwatch timer = Stopwatch.StartNew();

        void Report()
        {
            if (ready + refused < total)
                return;

            api.ShowChatMessage(string.Create(
                CultureInfo.InvariantCulture,
                $"Terrain tiles at step {step}: {ready} of {total} in {timer.ElapsedMilliseconds} ms ({core.Received - receivedBefore} from the server, {(core.ReceivedBytes - bytesBefore) / 1024.0:0} KiB; {core.FromDisk - diskBefore} from disk; {core.FromMemory - memoryBefore} from memory){(refused > 0 ? $"; {refused} refused: {firstRefusal}" : "")}.{centreHeights}"));
        }

        for (int dz = -radius; dz <= radius; dz++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                bool isCentre = dx == 0 && dz == 0;
                _ = this.RequestTile(step, centre.TileX + dx, centre.TileZ + dz, (dx * dx) + (dz * dz),
                    tile =>
                    {
                        ready++;
                        if (isCentre)
                            centreHeights = string.Create(CultureInfo.InvariantCulture, $" Your tile's heights run {tile.Height.Min()} to {tile.Height.Max()}.");
                        Report();
                    },
                    reason =>
                    {
                        refused++;
                        if (firstRefusal.Length == 0)
                            firstRefusal = reason;
                        Report();
                    });
            }
        }

        return TextCommandResult.Success(string.Create(CultureInfo.InvariantCulture, $"Asked for {total} tile(s) at step {step} round {centre.TileX}, {centre.TileZ}."));
    }

    private string DescribeClient()
    {
        TerrainTileClientCore core = this.client!;
        if (!core.Available)
            return $"Terrain tiles unavailable: {this.UnavailableReason}";

        StringBuilder text = new();
        _ = text.Append(CultureInfo.InvariantCulture, $"The server serves terrain tiles, identity {core.Identity[..12]}; up to {core.Status?.MaxWantedTiles} at once, within {core.Status?.MaxDistancePerStep} blocks per step.\n");
        _ = text.Append(CultureInfo.InvariantCulture, $"Received {core.Received} tiles, {core.ReceivedBytes / (1024.0 * 1024.0):0.0} MiB; from disk {core.FromDisk}, from memory {core.FromMemory}, refused {core.Refused}; {core.InFlight} in flight for {core.Requests.Pending} request(s).");
        if (core.Disk != null)
            _ = text.Append(CultureInfo.InvariantCulture, $"\nDisk: {core.Disk.Count} tiles, {core.Disk.Bytes / (1024.0 * 1024.0):0.0} MiB.");
        else if (this.capi!.IsSinglePlayer)
            _ = text.Append("\nDisk: the server's cache, on this machine.");

        if (core.LastProgress is TerrainTileProgress progress)
            _ = text.Append(CultureInfo.InvariantCulture, $"\nServer, last report: {progress.Wanted} wanted, {progress.Sending} on their way, {progress.Making} being made, {progress.Waiting} waiting.");

        return text.ToString();
    }

    private void SetAvailable()
    {
        this.Available = true;
        this.UnavailableReason = "";
        this.RaiseAvailabilityChanged();
    }

    private void SetUnavailable(string reason)
    {
        bool changed = this.Available;
        this.Available = false;
        this.UnavailableReason = reason;
        this.Identity = "";
        if (changed)
            this.RaiseAvailabilityChanged();
    }

    private void RaiseAvailabilityChanged()
    {
        try
        {
            this.AvailabilityChanged?.Invoke();
        }
        catch (Exception ex)
        {
            this.Log($"a consumer's availability handler threw: {ex}");
        }
    }

    private void RunDeferred()
    {
        while (this.deferred.TryDequeue(out Action? action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                this.Log($"a consumer's callback threw: {ex}");
            }
        }
    }

    private void Log(string message) => ((ICoreAPI?)this.sapi ?? this.capi)?.Logger.Notification("{0}", LogPrefix + message);

    private static TerrainTileConfig LoadConfig(ICoreAPI api, bool store)
    {
        TerrainTileConfig config;
        try
        {
            config = api.LoadModConfig<TerrainTileConfig>(TerrainTileConfig.FileName) ?? new TerrainTileConfig();
        }
        catch (Exception ex)
        {
            api.Logger.Warning("{0}", LogPrefix + $"{TerrainTileConfig.FileName} could not be read, so the defaults are used: {ex.Message}");
            config = new TerrainTileConfig();
            store = false; // Leave the broken file for its owner to fix.
        }

        config.Clamp();
        if (store)
            api.StoreModConfig(config, TerrainTileConfig.FileName);

        return config;
    }

    private static string SafeFolderName(string? savegame)
    {
        if (!string.IsNullOrEmpty(savegame) && savegame.Length <= 64 && savegame.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return savegame;

        return TerrainTileIdentity.Hash(writer => writer.Write(savegame ?? ""))[..32];
    }

    public override void Dispose()
    {
        if (this.sapi != null)
        {
            this.sapi.Event.UnregisterGameTickListener(this.serverTickListener);
            this.sapi.Event.PlayerDisconnect -= this.OnPlayerDisconnect;
            this.StopService();
            if (ServerInstance == this)
                ServerInstance = null;
        }

        if (this.capi != null)
        {
            this.capi.Event.UnregisterGameTickListener(this.clientTickListener);
            this.client?.Dispose();
            if (ClientInstance == this)
                ClientInstance = null;
        }

        this.AvailabilityChanged = null;
        base.Dispose();
    }

    private sealed class LocalSource : ITerrainTileSource
    {
        private readonly TerrainTileService service;

        public LocalSource(TerrainTileService service) => this.service = service;

        public void Want(TerrainTileKey key, int priority) => this.service.Want(LocalOwner, key, priority);

        public void Drop(TerrainTileKey key) => this.service.Drop(LocalOwner, key);
    }
}
