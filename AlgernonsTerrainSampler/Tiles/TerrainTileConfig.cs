#nullable enable
using System;

namespace AlgernonsTerrainSampler.Tiles;

/// <summary><c>ModConfig/algernonsterrainsampler.json</c>. The server writes it back with its defaults filled in; a client only reads it.</summary>
public sealed class TerrainTileConfig
{
    public const string FileName = "algernonsterrainsampler.json";

    /// <summary>Whether clients may ask for tiles. Mods on the server itself always may.</summary>
    public bool NetworkSampling { get; set; } = true;

    /// <summary>The privilege a player needs to ask for tiles. Empty lets everyone; <c>controlserver</c> only administrators.</summary>
    public string RequiredPrivilege { get; set; } = "";

    /// <summary>Tiles one client may want at once.</summary>
    public int MaxWantedTiles { get; set; } = 1024;

    /// <summary>A tile at step s is only served within this many times s blocks of the asking player: 16 km at step 2, 32 km at step 4.</summary>
    public int MaxDistancePerStep { get; set; } = 8192;

    /// <summary>Sampling threads. 0 picks: one on a dedicated server, otherwise cores less four, from one to four.</summary>
    public int Workers { get; set; }

    /// <summary>The server's disk cache. 0 turns it off.</summary>
    public int DiskCacheMegabytes { get; set; } = 1024;

    /// <summary>Encoded tiles the server keeps in memory.</summary>
    public int MemoryCacheMegabytes { get; set; } = 64;

    /// <summary>Tile data the server sends per tick (20 a second), all clients together.</summary>
    public int TransferKilobytesPerTick { get; set; } = 256;

    /// <summary>Bump to make every cache start again, for a change the identity cannot see.</summary>
    public int CacheRevision { get; set; }

    /// <summary>A client's own disk cache of tiles from multiplayer servers. 0 turns it off.</summary>
    public int ClientDiskCacheMegabytes { get; set; } = 512;

    /// <summary>A line in the server log every minute while tiles are being made or sent.</summary>
    public bool LogTileStats { get; set; }

    internal void Clamp()
    {
        this.RequiredPrivilege ??= "";
        this.MaxWantedTiles = Math.Clamp(this.MaxWantedTiles, 1, 65536);
        this.MaxDistancePerStep = Math.Clamp(this.MaxDistancePerStep, 128, 1 << 20);
        this.Workers = Math.Clamp(this.Workers, 0, 16);
        this.DiskCacheMegabytes = Math.Clamp(this.DiskCacheMegabytes, 0, 65536);
        this.MemoryCacheMegabytes = Math.Clamp(this.MemoryCacheMegabytes, 1, 4096);
        this.TransferKilobytesPerTick = Math.Clamp(this.TransferKilobytesPerTick, 32, 4096);
        this.ClientDiskCacheMegabytes = Math.Clamp(this.ClientDiskCacheMegabytes, 0, 65536);
    }
}
