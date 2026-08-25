// Explicit usings on purpose: this file is LINKED into the test project, which
// has a different ImplicitUsings setting.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EmoteCommander;

/// <summary>
/// Turns presets into a string that can be pasted into a Penumbra mod
/// description or a Discord message, and back again.
///
/// Format: <c>[EC1]base64(gzip(utf8 json))[/EC1]</c>
///
/// The version lives in the marker rather than inside the payload so an older
/// build meeting a newer code can refuse it cleanly instead of decoding
/// something it does not understand. The whole thing is a single line, because
/// descriptions and chat both mangle wrapped text.
/// </summary>
public static partial class ShareCode
{
    private const int CurrentVersion = 1;

    [GeneratedRegex(@"\[EC(\d+)\](.*?)\[/EC\1\]", RegexOptions.Singleline)]
    private static partial Regex CodeBlock();

    /// <summary>Encode presets into a single-line share code.</summary>
    public static string Encode(IEnumerable<Preset> presets)
    {
        ArgumentNullException.ThrowIfNull(presets);

        var json = JsonSerializer.SerializeToUtf8Bytes(presets.ToList());

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(json, 0, json.Length);

        var payload = Convert.ToBase64String(output.ToArray());
        return $"[EC{CurrentVersion}]{payload}[/EC{CurrentVersion}]";
    }

    /// <summary>
    /// Decode a share code. Throws <see cref="FormatException"/> for anything
    /// malformed, unknown-version, or not a share code at all - callers have
    /// exactly one exception type to handle, because every one of these arrives
    /// from a user pasting something.
    /// </summary>
    public static IReadOnlyList<Preset> Decode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new FormatException("No share code given.");

        var match = CodeBlock().Match(code.Trim());
        if (!match.Success)
            throw new FormatException("That does not look like a share code.");

        if (!int.TryParse(match.Groups[1].Value, out var version) || version != CurrentVersion)
            throw new FormatException(
                $"Share code version {match.Groups[1].Value} is not supported by this version " +
                $"of Emote Commander (it understands version {CurrentVersion}).");

        var payload = match.Groups[2].Value.Trim();
        if (payload.Length == 0)
            throw new FormatException("Share code is empty.");

        try
        {
            var compressed = Convert.FromBase64String(payload);

            using var input = new MemoryStream(compressed);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var plain = new MemoryStream();
            gzip.CopyTo(plain);

            var presets = JsonSerializer.Deserialize<List<Preset>>(plain.ToArray());
            if (presets is null)
                throw new FormatException("Share code contained no presets.");

            return presets;
        }
        catch (FormatException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Base64, gzip and JSON each throw their own thing; the user pasted
            // a bad string either way.
            throw new FormatException($"Share code could not be read: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Every share code embedded in a block of text, in order. Used to pull
    /// codes out of a mod description that also contains ordinary prose.
    /// Never throws - malformed codes are found here and rejected by Decode.
    /// </summary>
    public static IReadOnlyList<string> ExtractFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        return CodeBlock().Matches(text)
                          .Select(m => m.Value)
                          .ToList();
    }
}
