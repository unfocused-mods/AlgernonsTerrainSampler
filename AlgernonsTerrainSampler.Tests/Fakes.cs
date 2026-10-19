using System.Diagnostics;
using AlgernonsTerrainSampler.Tiles;

namespace AlgernonsTerrainSampler.Tests;

/// <summary>Smooth, deterministic terrain standing in for the sampler, counting every column it is asked for.</summary>
internal sealed class FakeTerrain
{
    private long samples;

    public long Samples => Interlocked.Read(ref this.samples);

    /// <summary>When set, a column inside this tile throws, as a broken generator would.</summary>
    public TerrainTileKey? FailIn { get; set; }

    public TerrainColumnSample Sample(int x, int z)
    {
        _ = Interlocked.Increment(ref this.samples);
        if (this.FailIn is TerrainTileKey failing && x >= failing.OriginX && x <= failing.OriginX + failing.Span && z >= failing.OriginZ && z <= failing.OriginZ + failing.Span)
            throw new InvalidOperationException("broken generator");

        return Column(x, z);
    }

    public static TerrainColumnSample Column(int x, int z) => new()
    {
        Height = 110 + (int)(40 * Math.Sin(x / 97.0) * Math.Cos(z / 131.0)) + ((x ^ z) & 3),
        ClimateColor = (((x / 64) & 0xFF) << 16) | (((z / 64) & 0xFF) << 8) | ((x + z) & 0x3F),
        Rainfall = ((x / 32) & 0xFF) / 255f,
        Temperature = ((x / 64) & 0xFF) / 255f,
        ForestDensity = (float)(0.5 + (0.5 * Math.Sin((x + z) / 211.0))),
        ShrubDensity = (float)(0.25 + (0.25 * Math.Cos((x - z) / 173.0)))
    };
}

internal static class Wait
{
    /// <summary>Calls <paramref name="tick"/> until <paramref name="done"/>, or fails after twenty seconds.</summary>
    public static void Until(Func<bool> done, Action tick, string what)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!done())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(20))
                Assert.Fail($"Timed out waiting for {what}.");

            tick();
            Thread.Sleep(1);
        }
    }
}

/// <summary>A folder of its own for one test, deleted after it.</summary>
internal sealed class TemporaryFolder : IDisposable
{
    public TemporaryFolder()
    {
        // Beside the test binaries rather than in the system temp folder.
        this.Path = System.IO.Path.Combine(AppContext.BaseDirectory, "test-temp", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(this.Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
