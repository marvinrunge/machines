using Machines.Sim.Story;

namespace Machines.Tests.Story;

/// <summary>
/// The hacking minigame's boards (plan WP-E, docs/STORY.md Hacken): every board made is solvable within its turns, wardens
/// and the order of targets are kept, the firewall's pulse can be sent into a dead end, and a seed always makes the same
/// board.
/// </summary>
public class HackPuzzleTests
{
    private static readonly HackKind[] Kinds = Enum.GetValues<HackKind>();

    private static IEnumerable<(HackKind Kind, int Stage, HackSpec Spec)> Specs() =>
        Kinds.SelectMany(kind => HackSpec.Boards(kind).Select((spec, stage) => (kind, stage, spec)));

    /// <summary>Plays the board's own solution, a turn at a time, until it is won (it may be won before the last).</summary>
    private static List<HackTurnResult> Solve(HackPuzzle puzzle)
    {
        var turns = new List<HackTurnResult>();
        foreach (var (cell, clockwise) in puzzle.SolutionTurns().ToList())
        {
            turns.Add(puzzle.Turn(cell, clockwise));
            if (puzzle.Complete)
                break;
        }
        return turns;
    }

    [Fact]
    public void The_boards_have_the_sizes_the_story_gives_them()
    {
        Assert.Equal([3], HackSpec.Boards(HackKind.Door).Select(b => b.Size));
        Assert.Equal([3], HackSpec.Boards(HackKind.Clamp).Select(b => b.Size));
        Assert.Equal([(4, 1)], HackSpec.Boards(HackKind.Grunt).Select(b => (b.Size, b.Wardens)));
        Assert.Equal([(5, 3)], HackSpec.Boards(HackKind.Tower).Select(b => (b.Size, b.Targets)));
        Assert.Equal([(5, true)], HackSpec.Boards(HackKind.Pod).Select(b => (b.Size, b.Firewall)));
        var core = HackSpec.Boards(HackKind.Core);
        Assert.Equal([4, 5, 5], core.Select(b => b.Size));
        Assert.Equal(3, core[1].Targets);
        Assert.Equal(0.9f, core[2].HoldAt);
    }

    [Fact]
    public void Every_board_made_is_solvable_within_its_turns()
    {
        foreach (var (kind, stage, spec) in Specs())
        {
            for (var seed = 0; seed < 150; seed++)
            {
                var puzzle = HackPuzzle.Generate(spec, seed * 7919 + stage);
                var label = $"{kind} board {stage} seed {seed}";
                Assert.False(puzzle.Complete, label);
                Assert.InRange(puzzle.Distance, 1, spec.MostTurns);
                var turns = Solve(puzzle);

                Assert.True(turns.Count <= spec.MostTurns, label);
                Assert.True(puzzle.Complete, label);
                Assert.True(turns[^1].Complete, label);
                Assert.All(turns, turn => Assert.True(turn.Turned, label));
                // The solution walks no warden and reaches no target before its time, so it costs a turn a turn.
                Assert.All(turns, turn => Assert.False(turn.Warden || turn.WrongOrder, label));
                Assert.Equal(turns.Count, puzzle.Trace);
                Assert.True(puzzle.Trace < spec.Trace, label);
                Assert.Equal(spec.Targets, puzzle.Claimed);
                Assert.Equal(1f, puzzle.Progress);
            }
        }
    }

    [Fact]
    public void A_board_is_dealt_with_chips_on_its_edges_and_wardens_inside()
    {
        foreach (var (kind, stage, spec) in Specs())
        {
            var puzzle = HackPuzzle.Generate(spec, 1234 + stage);
            var (px, _) = puzzle.At(puzzle.Probe.Cell);
            Assert.Equal((0, HackPuzzle.W), (px, puzzle.Probe.Side));
            Assert.Equal(spec.Targets, puzzle.Targets.Count);
            Assert.Equal(spec.Targets, puzzle.Targets.Select(t => t.Cell).Distinct().Count());
            Assert.All(puzzle.Targets, t => Assert.Equal(-1, puzzle.Next(t.Cell, t.Side)));
            Assert.Equal(spec.Wardens, puzzle.Wardens.Count());
            Assert.All(puzzle.Wardens, w => Assert.False(puzzle.IsTile(w)));
            Assert.Equal(spec.Firewall, puzzle.Firewall is not null);
            // As dealt it is not won and touches neither a warden nor a target.
            var network = puzzle.Network();
            Assert.False(puzzle.TouchesWarden(network), $"{kind} {stage}");
            Assert.DoesNotContain(puzzle.Targets, t => puzzle.Touches(network, t));
        }
    }

    [Fact]
    public void The_same_seed_makes_the_same_board_and_another_seed_another()
    {
        foreach (var (_, _, spec) in Specs())
        {
            var (a, b) = (HackPuzzle.Generate(spec, 42), HackPuzzle.Generate(spec, 42));
            Assert.Equal(a.Fingerprint(), b.Fingerprint());
            Assert.Equal(Enumerable.Range(0, a.Cells).Select(a.Mask), Enumerable.Range(0, b.Cells).Select(b.Mask));
            Assert.Equal(a.Probe, b.Probe);
            Assert.Equal(a.Targets, b.Targets);
            Assert.Equal(a.Firewall, b.Firewall);
            Assert.Equal(a.Wardens, b.Wardens);
            var boards = Enumerable.Range(0, 20).Select(seed => HackPuzzle.Generate(spec, seed).Fingerprint()).Distinct().Count();
            Assert.True(boards >= 15, $"{spec}: only {boards} boards of 20 seeds");
        }
    }

    [Fact]
    public void Touching_a_warden_costs_trace_back_and_keeps_the_board_from_being_won()
    {
        var checkedOne = false;
        for (var seed = 0; seed < 200 && !checkedOne; seed++)
        {
            // A tile of the probe's network beside the warden, turned until it opens onto it.
            var fresh = HackPuzzle.Generate(HackSpec.Boards(HackKind.Grunt)[0], seed);
            var warden = fresh.Wardens.Single();
            var network = fresh.Network();
            var beside = Enumerable.Range(0, fresh.Cells).FirstOrDefault(c => network[c] && fresh.IsTile(c)
                && new[] { HackPuzzle.N, HackPuzzle.E, HackPuzzle.S, HackPuzzle.W }.Any(s => fresh.Next(c, s) == warden), -1);
            if (beside < 0)
                continue;
            for (var i = 0; i < 4; i++)
            {
                var before = fresh.Trace;
                var result = fresh.Turn(beside, true);
                if (!result.Warden)
                    continue;
                Assert.Equal(before + 1 + HackPuzzle.WardenCost, fresh.Trace);
                Assert.False(result.Complete);
                checkedOne = true;
                break;
            }
        }
        Assert.True(checkedOne, "no board had a probe tile that could be turned onto its warden");
    }

    [Fact]
    public void Targets_count_only_in_their_order()
    {
        var spec = HackSpec.Boards(HackKind.Tower)[0];
        for (var seed = 0; seed < 60; seed++)
        {
            var puzzle = HackPuzzle.Generate(spec, seed);
            var claims = new List<int>();
            foreach (var turn in puzzle.SolutionTurns().ToList())
            {
                puzzle.Turn(turn.Cell, turn.Clockwise);
                claims.Add(puzzle.Claimed);
            }
            // Claims only ever grow, one target after the other, to all three.
            Assert.Equal(claims.OrderBy(c => c), claims);
            Assert.Equal(3, puzzle.Claimed);
        }
    }

    [Fact]
    public void A_full_trace_back_raises_the_alarm()
    {
        var spec = HackSpec.Boards(HackKind.Door)[0];
        var puzzle = HackPuzzle.Generate(spec, 5);
        // Turning one tile off the path round and round never wins the board, and each turn traces a little more.
        var idle = Enumerable.Range(0, puzzle.Cells).First(c => puzzle.IsTile(c) && !puzzle.OnPath(c) || puzzle.IsTile(c) && puzzle.Base(c) == 15);
        HackTurnResult last = default;
        var turns = 0;
        while (!last.Overflow && turns < 100)
        {
            last = puzzle.Turn(idle, true);
            turns++;
        }
        Assert.True(last.Overflow);
        Assert.True(puzzle.Trace >= spec.Trace);
        Assert.False(puzzle.Complete);
    }

    [Fact]
    public void Turns_are_refused_where_there_is_no_tile()
    {
        var puzzle = HackPuzzle.Generate(HackSpec.Boards(HackKind.Grunt)[0], 9);
        Assert.NotNull(puzzle.Turn(-1, true).Refused);
        Assert.NotNull(puzzle.Turn(puzzle.Cells, true).Refused);
        Assert.NotNull(puzzle.Turn(puzzle.Wardens.Single(), true).Refused);
        Assert.Equal(0, puzzle.Trace);
    }

    [Fact]
    public void A_turn_and_its_turn_back_leave_the_tile_as_it_was()
    {
        var puzzle = HackPuzzle.Generate(HackSpec.Boards(HackKind.Pod)[0], 77);
        var cell = Enumerable.Range(0, puzzle.Cells).First(puzzle.IsTile);
        var mask = puzzle.Mask(cell);
        puzzle.Turn(cell, true);
        Assert.Equal(HackPuzzle.Rotate(mask, 1), puzzle.Mask(cell));
        puzzle.Turn(cell, false);
        Assert.Equal(mask, puzzle.Mask(cell));
        Assert.Equal(2, puzzle.TurnsMade);
    }

    [Fact]
    public void The_firewall_runs_its_pulses_and_a_solved_board_sends_them_into_a_dead_end()
    {
        var spec = HackSpec.Boards(HackKind.Pod)[0];
        var moved = 0;
        for (var seed = 0; seed < 80; seed++)
        {
            // As dealt the first pulse dies before the probe (a fair start).
            var dealt = HackPuzzle.Generate(spec, seed);
            var steps = new List<HackPulse>();
            for (var i = 0; i < HackPuzzle.FirstPulse + dealt.Cells + 1; i++)
            {
                steps.Add(dealt.StepFirewall());
                if (steps[^1] == HackPulse.Died)
                    break;
            }
            Assert.DoesNotContain(HackPulse.Reached, steps);
            moved += steps.Count(s => s == HackPulse.Moved);

            // Laid as its solution the board sends every pulse into a dead end; once won, the firewall stops.
            Assert.True(dealt.DeadEnds(), $"seed {seed}");
            var solved = HackPuzzle.Generate(spec, seed);
            foreach (var turn in solved.SolutionTurns().ToList())
                Assert.True(solved.Turn(turn.Cell, turn.Clockwise).Turned);
            Assert.True(solved.Complete);
            Assert.Equal(HackPulse.None, solved.StepFirewall());
        }
        // Pulses do come out and run: the firewall is a threat on these boards.
        Assert.True(moved > 40, $"pulses moved only {moved} tiles over 80 boards");
    }

    [Fact]
    public void A_pulse_takes_the_way_toward_the_probe_and_dies_where_the_trace_ends()
    {
        var spec = HackSpec.Boards(HackKind.Pod)[0];
        for (var seed = 0; seed < 40; seed++)
        {
            var puzzle = HackPuzzle.Generate(spec, seed);
            var probe = puzzle.At(puzzle.Probe.Cell);
            var last = -1;
            for (var i = 0; i < 60; i++)
            {
                var pulse = puzzle.StepFirewall();
                if (pulse == HackPulse.Moved)
                {
                    // It only ever moves into a tile joined to the one it left.
                    Assert.True(last >= 0);
                    var side = new[] { HackPuzzle.N, HackPuzzle.E, HackPuzzle.S, HackPuzzle.W }.Single(s => puzzle.Next(last, s) == puzzle.PulseCell);
                    Assert.NotEqual(0, puzzle.Mask(last) & side);
                    Assert.NotEqual(0, puzzle.Mask(puzzle.PulseCell) & HackPuzzle.Opposite(side));
                }
                Assert.NotEqual(HackPulse.Reached, pulse);
                last = puzzle.PulseCell;
                Assert.True(puzzle.PulseCell < 0 || puzzle.IsTile(puzzle.PulseCell));
            }
            Assert.True(probe.X == 0);
        }
    }

    [Fact]
    public void The_tile_the_pulse_is_in_cannot_be_turned()
    {
        var spec = HackSpec.Boards(HackKind.Pod)[0];
        for (var seed = 0; seed < 80; seed++)
        {
            var puzzle = HackPuzzle.Generate(spec, seed);
            for (var i = 0; i < 10 && puzzle.PulseCell < 0; i++)
                puzzle.StepFirewall();
            if (puzzle.PulseCell < 0)
                continue;
            Assert.Equal("the firewall is in that tile", puzzle.Turn(puzzle.PulseCell, true).Refused);
            return;
        }
        Assert.Fail("no pulse came out on 80 boards");
    }
}
