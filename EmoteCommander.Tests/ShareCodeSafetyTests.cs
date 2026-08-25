using System.IO.Compression;
using System.Text;
using EmoteCommander;
using Xunit;

namespace EmoteCommander.Tests;

/// <summary>
/// A share code comes from someone else. These cover what a hostile one can do.
/// </summary>
public class ShareCodeSafetyTests
{
    private static string Wrap(byte[] payload)
        => "[EC1]" + System.Convert.ToBase64String(payload) + "[/EC1]";

    private static byte[] Gzip(byte[] plain)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, true))
            gzip.Write(plain, 0, plain.Length);
        return output.ToArray();
    }

    [Fact]
    public void RejectsADecompressionBomb()
    {
        // ~8 MB of zeroes compresses to a few KB. Without a cap this would be
        // decompressed in full and could exhaust memory.
        var bomb = Gzip(new byte[8 * 1024 * 1024]);
        Assert.True(bomb.Length < 64 * 1024, "the bomb should be small when compressed");

        var ex = Assert.Throws<FormatException>(() => ShareCode.Decode(Wrap(bomb)));
        Assert.Contains("unreasonable size", ex.Message);
    }

    [Fact]
    public void RejectsAbsurdlyManyPresets()
    {
        var many = "[" + string.Join(",",
            Enumerable.Range(0, 600).Select(i => $"{{\"Command\":\"c{i}\"}}")) + "]";
        var ex = Assert.Throws<FormatException>(
            () => ShareCode.Decode(Wrap(Gzip(Encoding.UTF8.GetBytes(many)))));
        Assert.Contains("more than", ex.Message);
    }

    [Fact]
    public void AcceptsALegitimatelyLargeCode()
    {
        // 200 presets is unusual but not hostile - it must still work.
        var presets = Enumerable.Range(0, 200)
            .Select(i => new Preset { Command = $"cmd{i}", ModDirectory = "mod" })
            .ToList();
        Assert.Equal(200, ShareCode.Decode(ShareCode.Encode(presets)).Count);
    }

    [Fact]
    public void GarbageThatIsNotGzipIsAFormatException()
    {
        var notGzip = Encoding.UTF8.GetBytes("this is not compressed at all");
        Assert.Throws<FormatException>(() => ShareCode.Decode(Wrap(notGzip)));
    }

    [Fact]
    public void ValidJsonThatIsNotPresetsDoesNotCrash()
    {
        // Deserialising to List<Preset> must not be tricked into anything else;
        // System.Text.Json has no polymorphic type handling enabled.
        var hostile = Encoding.UTF8.GetBytes(
            "[{\"$type\":\"System.Diagnostics.Process\",\"Command\":\"x\"}]");
        var presets = ShareCode.Decode(Wrap(Gzip(hostile)));
        var p = Assert.Single(presets);
        Assert.Equal("x", p.Command);        // $type ignored, treated as data
    }
}
