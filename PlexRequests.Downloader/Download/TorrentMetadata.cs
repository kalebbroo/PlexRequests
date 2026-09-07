using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequests.Downloader.Download;

/// <summary>Bounded bencode handling for Deluge's <c>prefetch_magnet_metadata</c> result. Deluge returns the
/// raw bencoded info dictionary; parsing only that content-addressed blob lets the worker inspect paths and
/// lengths without accepting an untrusted torrent file or downloading payload data.</summary>
internal static class TorrentMetadata
{
    private const int MaxMetadataBytes = 8 * 1024 * 1024;
    private const int MaxDepth = 32;
    private const int MaxNodes = 200_000;
    private const int MaxFiles = 20_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static AcquisitionManifest ParseInfo(byte[] info, string magnet)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.Length is 0 or > MaxMetadataBytes)
            throw new InvalidDataException($"Torrent metadata size {info.Length} is outside the safe limit.");

        var expectedHash = MagnetUtil.InfoHashFromMagnet(magnet)
            ?? throw new InvalidDataException("Magnet has no valid v1 info hash.");
        var actualHash = Convert.ToHexString(SHA1.HashData(info)).ToLowerInvariant();
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Torrent metadata does not match the magnet info hash.");

        var parser = new Parser(info);
        var root = parser.ParseDictionary();
        parser.RequireEnd();

        var files = ParseFiles(root);
        if (files.Count == 0) throw new InvalidDataException("Torrent metadata contains no files.");
        if (files.Count > MaxFiles) throw new InvalidDataException($"Torrent metadata exceeds {MaxFiles} files.");
        return new AcquisitionManifest(files, info.ToArray());
    }

    public static byte[] BuildTorrentFile(byte[] info, string magnet)
    {
        // Parse/verify first, including the SHA-1 boundary. The exact raw info bytes are then embedded so the
        // torrent added to Deluge has the same hash that was preflighted.
        _ = ParseInfo(info, magnet);
        var trackers = ParseTrackers(magnet);
        using var output = new MemoryStream(info.Length + trackers.Sum(x => x.Length) + 128);
        output.WriteByte((byte)'d');
        if (trackers.Count > 0)
        {
            WriteString(output, "announce");
            WriteString(output, trackers[0]);
            WriteString(output, "announce-list");
            output.WriteByte((byte)'l');
            foreach (var tracker in trackers)
            {
                output.WriteByte((byte)'l');
                WriteString(output, tracker);
                output.WriteByte((byte)'e');
            }
            output.WriteByte((byte)'e');
        }
        WriteString(output, "info");
        output.Write(info);
        output.WriteByte((byte)'e');
        return output.ToArray();
    }

    private static List<AcquisitionManifestFile> ParseFiles(BDictionary info)
    {
        if (info.TryGet("files", out var filesValue) && filesValue is BList list)
        {
            var files = new List<AcquisitionManifestFile>(list.Values.Count);
            foreach (var value in list.Values)
            {
                if (value is not BDictionary file)
                    throw new InvalidDataException("Torrent file entry is not a dictionary.");
                var length = Integer(file, "length");
                var path = StringList(file, "path.utf-8") ?? StringList(file, "path")
                    ?? throw new InvalidDataException("Torrent file entry has no path.");
                if (path.Count == 0 || path.Any(string.IsNullOrWhiteSpace))
                    throw new InvalidDataException("Torrent file entry has an empty path component.");
                files.Add(new AcquisitionManifestFile(string.Join('/', path), length));
            }
            return files;
        }

        var name = String(info, "name.utf-8") ?? String(info, "name")
            ?? throw new InvalidDataException("Single-file torrent metadata has no name.");
        return [new AcquisitionManifestFile(name, Integer(info, "length"))];
    }

    private static long Integer(BDictionary dictionary, string key)
    {
        if (!dictionary.TryGet(key, out var value) || value is not BInteger number || number.Value < 0)
            throw new InvalidDataException($"Torrent metadata has no valid {key} value.");
        return number.Value;
    }

    private static string? String(BDictionary dictionary, string key) =>
        dictionary.TryGet(key, out var value) && value is BString text
            ? Decode(text.Value)
            : null;

    private static List<string>? StringList(BDictionary dictionary, string key)
    {
        if (!dictionary.TryGet(key, out var value) || value is not BList list) return null;
        var result = new List<string>(list.Values.Count);
        foreach (var item in list.Values)
        {
            if (item is not BString text)
                throw new InvalidDataException("Torrent path contains a non-string component.");
            result.Add(Decode(text.Value));
        }
        return result;
    }

    private static List<string> ParseTrackers(string magnet)
    {
        var question = magnet.IndexOf('?');
        if (question < 0 || question == magnet.Length - 1) return [];
        var trackers = new List<string>();
        foreach (var part in magnet[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=');
            if (equals <= 0 || !part[..equals].Equals("tr", StringComparison.OrdinalIgnoreCase)) continue;
            var tracker = Uri.UnescapeDataString(part[(equals + 1)..].Replace('+', ' '));
            if (Uri.TryCreate(tracker, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https" or "udp")
                trackers.Add(uri.ToString());
        }
        return trackers.Distinct(StringComparer.Ordinal).Take(64).ToList();
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var prefix = Encoding.ASCII.GetBytes(bytes.Length.ToString(CultureInfo.InvariantCulture) + ":");
        stream.Write(prefix);
        stream.Write(bytes);
    }

    private static string Decode(byte[] value)
    {
        try { return StrictUtf8.GetString(value); }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("Torrent metadata contains invalid UTF-8 text.", ex);
        }
    }

    private abstract record BValue;
    private sealed record BString(byte[] Value) : BValue;
    private sealed record BInteger(long Value) : BValue;
    private sealed record BList(List<BValue> Values) : BValue;
    private sealed record BDictionary(List<KeyValuePair<string, BValue>> Values) : BValue
    {
        public bool TryGet(string key, out BValue? value)
        {
            foreach (var pair in Values)
            {
                if (!string.Equals(pair.Key, key, StringComparison.Ordinal)) continue;
                value = pair.Value;
                return true;
            }
            value = null;
            return false;
        }
    }

    private sealed class Parser(byte[] data)
    {
        private int _position;
        private int _nodes;

        public BDictionary ParseDictionary() => ParseValue(0) as BDictionary
            ?? throw new InvalidDataException("Torrent info metadata is not a dictionary.");

        public void RequireEnd()
        {
            if (_position != data.Length)
                throw new InvalidDataException("Torrent metadata has trailing data.");
        }

        private BValue ParseValue(int depth)
        {
            if (depth > MaxDepth) throw new InvalidDataException("Torrent metadata nesting is too deep.");
            if (++_nodes > MaxNodes) throw new InvalidDataException("Torrent metadata is too complex.");
            if (_position >= data.Length) throw new InvalidDataException("Torrent metadata ended unexpectedly.");
            return data[_position] switch
            {
                (byte)'d' => ParseDictionaryValue(depth + 1),
                (byte)'l' => ParseList(depth + 1),
                (byte)'i' => ParseInteger(),
                >= (byte)'0' and <= (byte)'9' => new BString(ParseBytes()),
                _ => throw new InvalidDataException($"Invalid bencode token at byte {_position}.")
            };
        }

        private BDictionary ParseDictionaryValue(int depth)
        {
            _position++;
            var values = new List<KeyValuePair<string, BValue>>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            while (!AtEndMarker())
            {
                var key = Decode(ParseBytes());
                if (!keys.Add(key)) throw new InvalidDataException($"Torrent metadata repeats dictionary key '{key}'.");
                values.Add(new KeyValuePair<string, BValue>(key, ParseValue(depth)));
            }
            _position++;
            return new BDictionary(values);
        }

        private BList ParseList(int depth)
        {
            _position++;
            var values = new List<BValue>();
            while (!AtEndMarker()) values.Add(ParseValue(depth));
            _position++;
            return new BList(values);
        }

        private BInteger ParseInteger()
        {
            _position++;
            var end = Array.IndexOf(data, (byte)'e', _position);
            if (end < 0) throw new InvalidDataException("Unterminated bencode integer.");
            var text = Encoding.ASCII.GetString(data, _position, end - _position);
            _position = end + 1;
            if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
                throw new InvalidDataException("Invalid bencode integer.");
            return new BInteger(value);
        }

        private byte[] ParseBytes()
        {
            var colon = Array.IndexOf(data, (byte)':', _position);
            if (colon < 0) throw new InvalidDataException("Unterminated bencode string length.");
            var lengthText = Encoding.ASCII.GetString(data, _position, colon - _position);
            if (!int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                || length < 0 || colon + 1L + length > data.Length)
                throw new InvalidDataException("Invalid bencode string length.");
            _position = colon + 1;
            var value = data.AsSpan(_position, length).ToArray();
            _position += length;
            return value;
        }

        private bool AtEndMarker()
        {
            if (_position >= data.Length) throw new InvalidDataException("Unterminated bencode collection.");
            return data[_position] == (byte)'e';
        }
    }
}
