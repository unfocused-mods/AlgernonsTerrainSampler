# The terrain tile service

Since 1.4.0 the sampler can serve its samples to other mods as cached tiles, on the server and, over its own network
channel, on clients. A mod that wants the terrain far from any loaded chunk (a far-terrain renderer, a map that shows
unexplored land, a horizon) asks for tiles; the sampler makes each tile once, keeps it, and hands it to everyone who
asks.

## Why

The sampler only runs on the server. Until now every mod that wanted its samples on a client built its own bridge:

| Mod | Its bridge | Its cache |
| --- | --- | --- |
| Farseer | Heights only, per 512-block region at a 4-block step, on its own channel | SQLite per savegame |
| Fast Map | `fastmap-terrain-v7`: raw grids for its climate overlays, and map tiles rendered on the server | Grids in memory, keyed on the exact rectangle asked for; map tiles on disk |
| VSPT | `vsrtxfar`: every field per region, plus surfaces worked out from vanilla's rules | A file per region per savegame |

A server that wanted all three installed three server halves. Each sampled the same ground again and kept its own
cache, and none of them shared work or bytes with the others. A player on a server that ran only the sampler got
nothing, and that is most servers.

## Tiles

A tile is always 128 cells across: 129 x 129 point samples, one every *step* blocks. The last row and column lie on the
next tile's first, so neighbouring tiles agree along the edge they share. A tile spans 128 x *step* blocks and is aligned
to that span. Its key is `(step, tileX, tileZ)`, and its corner is `(tileX * 128 * step, tileZ * 128 * step)`.

| Step | Tile span, blocks | Typical use |
| --- | --- | --- |
| 1 | 128 | Exact ground by the loaded edge |
| 2 | 256 | The taps for a prefiltered step-4 height |
| 4 | 512, one map region | A far field, a map's unexplored land |
| 8 | 1,024 | |
| 16 | 2,048 | |
| 32 | 4,096 | The horizon |
| 64 | 8,192 | A world overview |

Every tile costs the same: 16,641 columns, roughly half a second of one core. A tile is made row by row, so a row band's
map regions and chunk contexts stay in the sampler's own caches while it is crossed.

**Point samples only.** Filtering is the consumer's business. The step-2 tiles under a step-4 tile hold exactly the
3 x 3 taps a box filter averages for a step-4 height, and a consumer that needs samples outside its area (for normals,
say) reads them from the neighbouring tiles. Samples past the world's edge are clamped to it; a tile that starts outside
the world is refused.

### Fields

| Field | Type | |
| --- | --- | --- |
| `Height` | `short[]` | `TerrainColumnSample.Height` |
| `ClimateR`, `ClimateG`, `ClimateB` | `byte[]` | The raw climate map: temperature, rainfall, geologic activity. `ClimateColor(i)` packs them |
| `Rainfall` | `byte[]` | Height-adjusted rainfall times 255 (it is an integer over 255 already, so exact) |
| `Forest`, `Shrub` | `ushort[]` | The densities times 65,535 |

`TerrainTile.Column(i)` gives a column back as a `TerrainColumnSample`. Index `i` is `iz * 129 + ix`.

### The format

The same bytes are written to disk and sent on the wire, so a cached tile goes out without being decoded. A 54-byte
header (magic `ATS1`, format version, step, tile X and Z, cells, field mask, payload length, SHA-256 of the payload),
then Brotli over the field planes. Each value is stored as its difference from the sample to its left, or above it at
the start of a row, and 16-bit planes are split into low and high bytes, so smooth ground compresses to very little.
Brotli is part of .NET, so the format adds no DLL.

## Caches and identity

Each tile is made once and kept in memory (64 MiB by default) and on disk (1 GiB), under
`ModData/algernonsterrainsampler/<savegame>/<identity>/`. The identity is a SHA-256 of everything that decides what
the sampler answers: the sampler's version and an algorithm revision, the seed, the map and region sizes, the sea level,
the whole world configuration, the patched `worldgen/landforms.json`, every public setting of `TerraGenConfig`, the
Watersheds handoff, an admin revision number, and a canary of 64 columns sampled at start. Anything that would change a
tile changes the identity, and a new identity is a new folder, so a stale tile is never served. Older folders count
against the quota until they are evicted, least recently used first.

Files are written to a temporary and moved into place; one that fails its checksum is deleted and sampled again; if the
storage itself fails, the cache turns itself off and the service carries on from memory.

**A failure is never kept.** The tile service samples through `SampleColumnOrThrow`, which shares `SampleColumn`'s code but
throws where `SampleColumn` falls back to sea level. A failed tile is refused, with the reason, and sampled again when it
is next asked for. On a world type the sampler does not know (superflat), the service says so and does not start.

## Scheduling

- Persistent worker threads, so the sampler's per-thread caches stay warm between tiles: one on a dedicated server, cores
  less four (one to four) otherwise. Below normal priority where the platform allows, and slower when the game process
  passes 75 % CPU or a tick runs over 100 ms.
- One job per tile, however many clients want it.
- Each owner's most urgent tile first, round robin between owners, so one player cannot starve the rest.
- Cache hits are answered at once, from memory, or from the disk cache's own thread, and never wait behind a tile being
  made.
- A tile whose owners all left is finished and kept: it may be wanted again.
- With Watersheds, sampling stays on the main thread, a few milliseconds a tick, because nobody has checked whether
  Watersheds is thread safe. Encoding and writing still run on a worker.

## The channel

Channel `algernonsterrainsampler`, protobuf messages, protocol 1. A tile's key names a request.

| Message | Direction | Carries |
| --- | --- | --- |
| `TerrainTileHello` | client to server | Its protocol |
| `TerrainTileStatus` | server to client | Available or why not, the identity, the limits |
| `TerrainTileWant` | client to server | Tiles to add, each with a priority, and tiles to drop |
| `TerrainTileProgress` | server to client | Every five seconds while tiles wait: wanted, on their way, being made, waiting |
| `TerrainTileData` | server to client | One fragment of a tile, at most 32 KiB |
| `TerrainTileRefused` | server to client | A tile it will not send, and why |

The server keeps each client's wanted set, so a want message changes it. Tile data goes out under a byte budget per
tick, all clients together, round robin, and each client gets one tile finished before the next is started.

A client keeps its own disk cache of tiles (512 MiB by default) under the server's identity, in
`ModData/algernonsterrainsampler/remote/`, so a reconnect costs the server nothing. It gathers the requests of every
mod on it, so when two mods want one tile it crosses the network once. In single player it uses the server's cache,
which is on the same machine.

## Settings

`ModConfig/algernonsterrainsampler.json`, written by the server with its defaults:

| Setting | Default | What it bounds |
| --- | --- | --- |
| `NetworkSampling` | `true` | Whether clients may ask at all. Mods on the server always may |
| `RequiredPrivilege` | `""` | The privilege a player needs. Empty lets everyone; `controlserver` only administrators |
| `MaxWantedTiles` | 1,024 | Tiles one client may want at once |
| `MaxDistancePerStep` | 4,096 | A tile at step *s* is only served within 4,096 x *s* blocks of the asking player (16 km at step 4), so nobody can ask for fine tiles across the whole world |
| `Workers` | 0 | Sampling threads; 0 picks as above |
| `DiskCacheMegabytes` | 1,024 | The server's disk cache; 0 turns it off |
| `MemoryCacheMegabytes` | 64 | Encoded tiles kept in memory |
| `TransferKilobytesPerTick` | 256 | Tile data sent per tick (20 a second), all clients together |
| `CacheRevision` | 0 | Bump it to discard every cache, for a change the identity cannot see |
| `ClientDiskCacheMegabytes` | 512 | A client's own cache of tiles from multiplayer servers |
| `LogTileStats` | `false` | A line in the server log every minute while tiles are being made or sent |

Every check is made when a request arrives and again while it waits: a player who loses the privilege, or moves away,
loses the tiles they can no longer have.

## Using it from another mod

`TerrainTileSystem.ServerInstance` on the server, `TerrainTileSystem.ClientInstance` on a client. Wait for `Available`,
or for `AvailabilityChanged`, then ask:

```csharp
TerrainTileSystem tiles = TerrainTileSystem.ClientInstance;
if (tiles?.Available == true)
{
    int step = 4;
    long handle = tiles.RequestTile(step, TerrainTileSystem.TileIndexOf(step, x), TerrainTileSystem.TileIndexOf(step, z), priority: 0,
        tile => Build(tile),          // on the main thread
        reason => Log(reason));       // instead, with why
    // later, if it is no longer wanted: tiles.CancelTile(handle);
}
```

| Member | |
| --- | --- |
| `static ServerInstance`, `static ClientInstance` | The service on each side, or null |
| `static int TileCells`, `static int TileSamples` | 128 and 129 |
| `static int TileIndexOf(int step, int world)` | The tile index holding a world coordinate, on either axis |
| `bool Available`, `string UnavailableReason` | Whether tiles can be asked for, and why not |
| `string Identity` | The cache identity: the same identity, the same tiles |
| `event Action AvailabilityChanged` | On the main thread |
| `long RequestTile(int step, int tileX, int tileZ, int priority, Action<TerrainTile> ready, Action<string> refused)` | Lower priorities first. Exactly one callback runs, on the main thread, never before this returns, unless cancelled first |
| `void CancelTile(long handle)` | Neither callback will run |

Tiles are read only and shared between everyone who asked for them.

**Without a compile-time reference.** The signatures use only .NET's own types and `TerrainTile`, so a mod can bind them by
reflection, as Fast Map and VSPT reach the sampler today: find `AlgernonsTerrainSampler.Tiles.TerrainTileSystem`, read
`ClientInstance`, and invoke `RequestTile` with an `Action<object>` where `Action<TerrainTile>` is expected (delegate
variance allows it), reading the tile's public fields through cached `FieldInfo`s.

## Commands

| Command | Side | |
| --- | --- | --- |
| `/terrainsampler tiles` | Server, `controlserver` | The service's state: tiles made, hits, the queue, memory, disk, bytes sent |
| `.terrainsampler tiles` | Client | Whether the server serves tiles, and what this client has had |
| `.terrainsampler fetch [step] [radius]` | Client | Asks for the square of tiles round you and reports how long they took and where they came from |

## Compatibility

- `requiredOnClient` and `requiredOnServer` are both false. A player can keep the sampler installed and join a server
  without it (the tile service reports itself unavailable there), and a server with it no longer makes every player
  install it. The client and server need the same version for tiles: the game turns off a client's copy that differs
  from the server's.
- A client and server on different protocol versions say so in the status, and do nothing.
- `SampleColumn` and `GetBlockColumnHeight` are unchanged.
- Landforms are now loaded at every world start, not once per process: they depend on the world height and on mods
  patching the asset, so a second world in the same run used to sample with the first world's.

## Measured

On a two-core dedicated server (Azure `Standard_D2alds_v6`, Linux, 1.22.7), one worker, through
`/terrainsampler tilefetch`, while Fast Map was rebuilding its own map cache on the main thread:

| | |
| --- | --- |
| Step 4, nine tiles round spawn | 2.7 s; 246 ms a tile to sample; 60 KiB a tile |
| Step 4, 81 tiles (4,608 blocks across) | 19.3 s; 227 ms a tile; 59 KiB a tile |
| Step 32, nine tiles (12,288 blocks across) | 5.4 s; 512 ms a tile, since every sample is in a new chunk; 98 KiB a tile |
| The same nine tiles again | 51 ms, all from memory, nothing sampled |
| The 81 again, after a restart | 235 ms, all from disk, nothing sampled; 90 tiles, 5.5 MiB indexed at start |
| The identity | Unchanged across three restarts |

## Not done yet

- Prewarming: making the coarse tiles round spawn and players while the server is idle.
- Fast Map, VSPT and Farseer moving their own bridges onto this.
- A build for 1.21, if that is wanted.
