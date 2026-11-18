using Machines.Import.Model;
using Machines.Net;
using Machines.Sim.World;

namespace Machines.Tests.Net;

/// <summary>The relay's log sealed for a rebuild (P2 design §2.10, §5.1): encoded, split, joined and decoded.</summary>
public class BundleSegmentTests
{
    private static readonly PlayerId Red = PlayerId.Of(Race.Red), Blue = PlayerId.Of(Race.Blue);

    /// <summary>Ticks <paramref name="from"/> to <paramref name="to"/> as the relay closes them: a drive every seventh tick, a leave and a return.</summary>
    private static List<Bundle> Log(long from, long to) =>
    [
        .. Enumerable.Range((int)from, (int)(to - from + 1)).Select(t => new Bundle(t,
            t % 7 == 0 ? [new Turn(t, Red, [new DriveCommand(Race.Red, 40 + t, true, Forward: 1, Turn: -1)], Asked: t - 1)] : [],
            t == from + 3 ? [new PlayerLeft(Blue, t)] : t == from + 30 ? [new PlayerReturned(Blue, t)] : null, 4)),
    ];

    private static void Same(Bundle expected, Bundle actual)
    {
        Assert.Equal((expected.Tick, expected.Delay), (actual.Tick, actual.Delay));
        Assert.Equal(expected.Turns.Select(t => (t.Tick, t.Player, t.Asked)), actual.Turns.Select(t => (t.Tick, t.Player, t.Asked)));
        Assert.Equal(expected.Turns.SelectMany(t => t.Commands), actual.Turns.SelectMany(t => t.Commands));
        Assert.Equal(expected.Presence ?? [], actual.Presence ?? []);
    }

    [Fact]
    public void A_segment_keeps_only_its_bundles_with_something_in_them_and_decodes_to_them()
    {
        var log = Log(0, 99);

        var segment = BundleSegments.Decode(BundleSegments.Encode(10, 59, log));

        Assert.Equal((10L, 59L), (segment.From, segment.To));
        var kept = log.Where(b => b.Tick is >= 10 and <= 59 && !b.Empty).ToList();
        Assert.Equal([14L, 21, 28, 30, 35, 42, 49, 56], kept.Select(b => b.Tick));
        Assert.Equal(kept.Select(b => b.Tick), segment.Bundles.Select(b => b.Tick));
        foreach (var (expected, actual) in kept.Zip(segment.Bundles))
            Same(expected, actual);
        // The leave at 3 and the return at 30 are each in the segment of their own tick.
        Assert.Contains(BundleSegments.Decode(BundleSegments.Encode(0, 9, log)).Bundles, b => b.Presence is [PlayerLeft { FromTick: 3 }]);
        Assert.Contains(segment.Bundles, b => b.Presence is [PlayerReturned { FromTick: 30 }]);
        Assert.Empty(BundleSegments.Decode(BundleSegments.Encode(100, 199, log)).Bundles);
    }

    [Theory]
    [InlineData(64)]
    [InlineData(200)]
    [InlineData(1 << 20)]
    public void A_segment_is_split_into_pieces_no_larger_than_asked_and_joined_back(int partBytes)
    {
        var data = BundleSegments.Encode(0, 599, Log(0, 599));

        var parts = BundleSegments.Split(0, 599, data, partBytes);

        Assert.Equal((data.Length + partBytes - 1) / partBytes, parts.Count);
        Assert.All(parts, p => Assert.InRange(p.Data.Length, 1, partBytes));
        Assert.Equal(Enumerable.Range(0, parts.Count), parts.Select(p => p.Part));
        Assert.All(parts, p => Assert.Equal((0L, 599L, parts.Count), (p.From, p.To, p.Parts)));
        Assert.Equal(data, BundleSegments.Join(parts));
        // Through the wire and back, each piece as the relay sends it.
        var sent = parts.Select(p => (LogPart)NetJson.Decode(NetJson.Encode(p))).ToList();
        Assert.Equal(data, BundleSegments.Join(sent));
        Assert.Equal(BundleSegments.Decode(data).Bundles.Count, BundleSegments.Decode(BundleSegments.Join(sent)).Bundles.Count);
    }

    [Fact]
    public void A_segment_of_nothing_is_one_piece_and_sealing_is_encoding_and_splitting()
    {
        var empty = BundleSegments.Seal(0, 599, [], 64 * 1024);
        Assert.Single(empty);
        Assert.Empty(BundleSegments.Decode(BundleSegments.Join(empty)).Bundles);
        Assert.Single(BundleSegments.Split(0, 0, [], 64));

        var log = Log(600, 1199);
        var sealedParts = BundleSegments.Seal(600, 1199, log, 100);
        Assert.Equal(BundleSegments.Split(600, 1199, BundleSegments.Encode(600, 1199, log), 100).Select(p => p.Data.Length), sealedParts.Select(p => p.Data.Length));
        Assert.Throws<ArgumentOutOfRangeException>(() => BundleSegments.Split(0, 1, [1, 2], 0));
    }

    [Fact]
    public void Pieces_missing_out_of_order_or_of_another_segment_are_refused()
    {
        var parts = BundleSegments.Seal(0, 599, Log(0, 599), 64);
        var other = BundleSegments.Seal(600, 1199, Log(600, 1199), 64);
        Assert.True(parts.Count > 3);

        Assert.Throws<InvalidDataException>(() => BundleSegments.Join([]));
        Assert.Throws<InvalidDataException>(() => BundleSegments.Join([.. parts.Take(parts.Count - 1)]));
        Assert.Throws<InvalidDataException>(() => BundleSegments.Join([parts[1], parts[0], .. parts.Skip(2)]));
        Assert.Throws<InvalidDataException>(() => BundleSegments.Join([parts[0], other[1], .. parts.Skip(2)]));
    }

    [Fact]
    public void A_bundle_or_a_piece_of_the_log_is_encoded_once_however_many_get_it()
    {
        var bundle = Log(0, 20)[14];
        var part = BundleSegments.Seal(0, 20, Log(0, 20), 64)[0];
        var turn = bundle.Turns.Single();

        Assert.Same(NetJson.Encode(bundle), NetJson.Encode(bundle));
        Assert.Same(NetJson.Encode(part), NetJson.Encode(part));
        Assert.NotSame(NetJson.Encode(turn), NetJson.Encode(turn));
        // An equal bundle is another message: encoded on its own, to the same bytes.
        var twin = bundle with { };
        Assert.NotSame(NetJson.Encode(bundle), NetJson.Encode(twin));
        Assert.Equal(NetJson.Encode(bundle), NetJson.Encode(twin));
    }
}
