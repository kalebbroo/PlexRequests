using System.Text.RegularExpressions;

namespace PlexRequestsHosted.Shared.Releases;

/// <summary>
/// Magnet-URI helpers. The important one is <see cref="InfoHashFromMagnet"/>: every candidate carries a
/// magnet by construction (providers drop results they can't build one for), but only some indexers report
/// an info hash of their own. Deriving it means the hash is always available — which is what makes
/// deduplicating across indexers and blocklisting a failed release by hash actually work.
/// </summary>
public static partial class MagnetUtil
{
    [GeneratedRegex(@"xt=urn:btih:([A-Za-z0-9]+)", RegexOptions.IgnoreCase)]
    private static partial Regex BtihRegex();

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>
    /// The 40-character hex info hash in a magnet URI, or null. Magnets carry the hash as either hex (40
    /// chars) or base32 (32 chars); both are normalised to lower-case hex so the two encodings of the same
    /// torrent compare equal.
    /// </summary>
    public static string? InfoHashFromMagnet(string? magnet)
    {
        if (string.IsNullOrWhiteSpace(magnet)) return null;
        var m = BtihRegex().Match(magnet);
        if (!m.Success) return null;
        return Normalize(m.Groups[1].Value);
    }

    /// <summary>Normalise an info hash from either encoding to lower-case hex; null when unrecognisable.</summary>
    public static string? Normalize(string? infoHash)
    {
        if (string.IsNullOrWhiteSpace(infoHash)) return null;
        var v = infoHash.Trim();
        if (v.Length == 40 && v.All(Uri.IsHexDigit)) return v.ToLowerInvariant();
        if (v.Length == 32) return Base32ToHex(v);
        return null;
    }

    private static string? Base32ToHex(string base32)
    {
        var bits = 0;
        var value = 0;
        var bytes = new List<byte>(20);
        foreach (var c in base32.ToUpperInvariant())
        {
            var idx = Base32Alphabet.IndexOf(c);
            if (idx < 0) return null;
            value = (value << 5) | idx;
            bits += 5;
            if (bits < 8) continue;
            bytes.Add((byte)((value >> (bits - 8)) & 0xFF));
            bits -= 8;
        }
        return bytes.Count == 20 ? Convert.ToHexString(bytes.ToArray()).ToLowerInvariant() : null;
    }
}
