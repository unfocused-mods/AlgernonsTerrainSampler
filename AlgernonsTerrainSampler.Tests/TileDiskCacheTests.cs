using AlgernonsTerrainSampler.Tiles;

namespace AlgernonsTerrainSampler.Tests;

public class TileDiskCacheTests
{
    private static readonly string Current = TerrainTileIdentity.Hash(writer => writer.Write("current"));
    private static readonly string Old = TerrainTileIdentity.Hash(writer => writer.Write("old"));

    private static TerrainTileDiskCache Open(string root, string identity, int megabytes)
    {
        TerrainTileDiskCache cache = new(root, identity, megabytes);
        Wait.Until(() => cache.Ready, () => { }, "the disk index");
        return cache;
    }

    private static byte[] Blob(int kibibytes) => new byte[kibibytes * 1024];

    [Fact]
    public void The_least_recently_used_tile_goes_when_the_quota_is_full()
    {
        using TemporaryFolder folder = new();
        using TerrainTileDiskCache cache = Open(folder.Path, Current, 1);
        cache.Store(new TerrainTileKey(4, 1, 1), Blob(400));
        Thread.Sleep(20);
        cache.Store(new TerrainTileKey(4, 2, 2), Blob(400));
        Thread.Sleep(20);
        cache.Store(new TerrainTileKey(4, 3, 3), Blob(400));

        Assert.True(cache.Bytes <= 1024 * 1024);
        Assert.False(cache.Contains(new TerrainTileKey(4, 1, 1)));
        Assert.True(cache.Contains(new TerrainTileKey(4, 3, 3)));
    }

    [Fact]
    public void An_older_identity_counts_against_the_quota_and_goes_first()
    {
        using TemporaryFolder folder = new();
        using (TerrainTileDiskCache old = Open(folder.Path, Old, 64))
            old.Store(new TerrainTileKey(4, 9, 9), Blob(700));

        using TerrainTileDiskCache cache = Open(folder.Path, Current, 1);
        Assert.Equal(0, cache.Count);
        Assert.Equal(700 * 1024, cache.Bytes);

        cache.Store(new TerrainTileKey(4, 1, 1), Blob(500));
        Assert.True(cache.Contains(new TerrainTileKey(4, 1, 1)));
        Assert.Equal(500 * 1024, cache.Bytes);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(folder.Path, Old)));
    }

    [Fact]
    public void A_file_that_is_not_a_sound_tile_is_never_served()
    {
        using TemporaryFolder folder = new();
        using TerrainTileDiskCache cache = Open(folder.Path, Current, 64);
        TerrainTileKey key = new(4, 5, 6);
        cache.Store(key, Blob(10));

        byte[]? read = [];
        bool done = false;
        Assert.True(cache.TryRead(key, data => { read = data; done = true; }));
        Wait.Until(() => done, () => { }, "the read");

        Assert.Null(read);
        Assert.False(cache.Contains(key));
        Assert.Equal(0, cache.Bytes);
    }
}
