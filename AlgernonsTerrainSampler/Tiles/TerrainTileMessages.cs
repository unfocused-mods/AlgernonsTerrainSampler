#nullable enable
using System;
using ProtoBuf;

namespace AlgernonsTerrainSampler.Tiles;

/// <summary>The tile channel's protocol. A tile's key names a request, so there are no request ids.</summary>
internal static class TerrainTileProtocol
{
    public const string ChannelName = "algernonsterrainsampler";

    /// <summary>Bump on any change to the messages below; a client and server that differ say so and do nothing.</summary>
    public const int Version = 1;

    /// <summary>Largest piece of a tile in one packet.</summary>
    public const int FragmentBytes = 32 * 1024;

    /// <summary>Largest tile either side accepts: far above any real tile, so a corrupt length cannot exhaust memory.</summary>
    public const int MaxTileBytes = 1024 * 1024;

    /// <summary>Most keys in one want message.</summary>
    public const int MaxKeysPerWant = 4096;
}

/// <summary>Client to server, when its channel connects: which protocol it speaks.</summary>
[ProtoContract]
public sealed class TerrainTileHello
{
    [ProtoMember(1)] public int Protocol { get; set; } = TerrainTileProtocol.Version;
}

/// <summary>Server to client, in answer to a hello: whether it serves tiles, and on what terms.</summary>
[ProtoContract]
public sealed class TerrainTileStatus
{
    [ProtoMember(1)] public int Protocol { get; set; } = TerrainTileProtocol.Version;
    [ProtoMember(2)] public bool Available { get; set; }
    [ProtoMember(3)] public string Reason { get; set; } = "";
    [ProtoMember(4)] public string Identity { get; set; } = "";
    [ProtoMember(5)] public int Cells { get; set; } = TerrainTileKey.Cells;
    [ProtoMember(6)] public int MinStep { get; set; } = TerrainTileKey.MinStep;
    [ProtoMember(7)] public int MaxStep { get; set; } = TerrainTileKey.MaxStep;
    [ProtoMember(8)] public int MaxWantedTiles { get; set; }
    [ProtoMember(9)] public int MaxDistancePerStep { get; set; }
}

/// <summary>
/// Client to server: changes to the set of tiles it wants. The server keeps that set, so a client that reconnects
/// sends it again with <see cref="Reset"/>. Adding a key that is already wanted changes its priority.
/// </summary>
[ProtoContract]
public sealed class TerrainTileWant
{
    [ProtoMember(1)] public bool Reset { get; set; }
    [ProtoMember(2, IsPacked = true)] public int[]? AddStep { get; set; }
    [ProtoMember(3, IsPacked = true)] public int[]? AddX { get; set; }
    [ProtoMember(4, IsPacked = true)] public int[]? AddZ { get; set; }
    [ProtoMember(5, IsPacked = true)] public int[]? AddPriority { get; set; }
    [ProtoMember(6, IsPacked = true)] public int[]? DropStep { get; set; }
    [ProtoMember(7, IsPacked = true)] public int[]? DropX { get; set; }
    [ProtoMember(8, IsPacked = true)] public int[]? DropZ { get; set; }

    internal int AddCount => Math.Min(Math.Min(this.AddStep?.Length ?? 0, this.AddX?.Length ?? 0), Math.Min(this.AddZ?.Length ?? 0, this.AddPriority?.Length ?? 0));

    internal int DropCount => Math.Min(this.DropStep?.Length ?? 0, Math.Min(this.DropX?.Length ?? 0, this.DropZ?.Length ?? 0));
}

/// <summary>Server to client, every few seconds while it waits on tiles: that the server is working, and how much is left.</summary>
[ProtoContract]
public sealed class TerrainTileProgress
{
    [ProtoMember(1)] public int Wanted { get; set; }
    [ProtoMember(2)] public int Sending { get; set; }
    [ProtoMember(3)] public int Making { get; set; }
    [ProtoMember(4)] public int Waiting { get; set; }
}

/// <summary>Server to client: one piece of an encoded tile. The pieces of one tile arrive in order.</summary>
[ProtoContract]
public sealed class TerrainTileData
{
    [ProtoMember(1)] public int Step { get; set; }
    [ProtoMember(2)] public int TileX { get; set; }
    [ProtoMember(3)] public int TileZ { get; set; }
    [ProtoMember(4)] public int Total { get; set; }
    [ProtoMember(5)] public int Offset { get; set; }
    [ProtoMember(6)] public byte[]? Data { get; set; }
}

/// <summary>Server to client: a tile it will not send, and why.</summary>
[ProtoContract]
public sealed class TerrainTileRefused
{
    [ProtoMember(1)] public int Step { get; set; }
    [ProtoMember(2)] public int TileX { get; set; }
    [ProtoMember(3)] public int TileZ { get; set; }
    [ProtoMember(4)] public string Reason { get; set; } = "";
}
