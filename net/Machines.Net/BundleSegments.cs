using System.IO.Compression;
using System.Text.Json;

namespace Machines.Net;

/// <summary>
/// A sealed run of the relay's bundle log (P2 design §2.10): the bundles from <paramref name="From"/> to <paramref name="To"/>
/// that hold a turn or a coming or going, in order; every other tick of the run had nothing.
/// </summary>
public sealed record BundleSegment(long From, long To, IReadOnlyList<Bundle> Bundles);

/// <summary>
/// The relay's log as a rebuild downloads it (P2 design §2.10, §5.3): a run of bundles sealed as gzipped JSON of a
/// <see cref="BundleSegment"/>, split into <see cref="LogPart"/>s small enough for one frame. The relay seals each run once,
/// and every rebuild is sent the same parts; a client joins them back and decodes a segment only when its world gets there.
/// </summary>
public static class BundleSegments
{
    /// <summary>The bundles from <paramref name="from"/> to <paramref name="to"/>, those with nothing left out, as gzipped JSON.</summary>
    public static byte[] Encode(long from, long to, IEnumerable<Bundle> bundles)
    {
        var kept = bundles.Where(b => b.Tick >= from && b.Tick <= to && !b.Empty).ToList();
        var json = JsonSerializer.SerializeToUtf8Bytes(new BundleSegment(from, to, kept), NetJson.Options);
        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(json);
        return packed.ToArray();
    }

    /// <summary>A segment back from what <see cref="Encode"/> made.</summary>
    /// <exception cref="InvalidDataException">The data is no gzip.</exception>
    /// <exception cref="JsonException">The data is no segment.</exception>
    public static BundleSegment Decode(byte[] data)
    {
        using var gzip = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        return JsonSerializer.Deserialize<BundleSegment>(gzip, NetJson.Options) ?? throw new JsonException("empty segment");
    }

    /// <summary>A sealed segment cut into pieces of at most <paramref name="partBytes"/> bytes, in order; one piece at least.</summary>
    public static IReadOnlyList<LogPart> Split(long from, long to, byte[] data, int partBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(partBytes, 1);
        var count = Math.Max(1, (data.Length + partBytes - 1) / partBytes);
        var parts = new List<LogPart>(count);
        for (var i = 0; i < count; i++)
        {
            var start = i * partBytes;
            parts.Add(new LogPart(from, to, i, count, data[start..Math.Min(data.Length, start + partBytes)]));
        }
        return parts;
    }

    /// <summary>The bundles from <paramref name="from"/> to <paramref name="to"/> sealed and cut into pieces: <see cref="Encode"/>, then <see cref="Split"/>.</summary>
    public static IReadOnlyList<LogPart> Seal(long from, long to, IEnumerable<Bundle> bundles, int partBytes) =>
        Split(from, to, Encode(from, to, bundles), partBytes);

    /// <summary>The pieces of one segment put back together, in their order.</summary>
    /// <exception cref="InvalidDataException">They are not every piece of one segment, in order.</exception>
    public static byte[] Join(IReadOnlyList<LogPart> parts)
    {
        if (parts.Count == 0 || parts.Count != parts[0].Parts)
            throw new InvalidDataException("not every piece of the segment");
        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i].Part != i || parts[i].From != parts[0].From || parts[i].To != parts[0].To)
                throw new InvalidDataException($"piece {i} of the segment from {parts[0].From} is not in its place");
        }
        var data = new byte[parts.Sum(p => p.Data.Length)];
        var at = 0;
        foreach (var part in parts)
        {
            part.Data.CopyTo(data, at);
            at += part.Data.Length;
        }
        return data;
    }
}
