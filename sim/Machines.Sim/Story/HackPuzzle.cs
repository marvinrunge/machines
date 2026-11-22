namespace Machines.Sim.Story;

/// <summary>What a hack is aimed at, which sets its boards (docs/STORY.md, Hacken).</summary>
public enum HackKind
{
    /// <summary>A door or a switch: one small board, the way in.</summary>
    Door,

    /// <summary>The maintenance clamp the Locator hangs in: as small as a door.</summary>
    Clamp,

    /// <summary>A Grunt or another small machine: a warden chip the trace must not touch.</summary>
    Grunt,

    /// <summary>A tower or a satellite: several target chips, reached in their order.</summary>
    Tower,

    /// <summary>A pod: a firewall chip sends red pulses over the traces toward the probe.</summary>
    Pod,

    /// <summary>The ship's main computer: three boards in a row, the last held at 90 % until the story breaks it off.</summary>
    Core,
}

/// <summary>
/// One board of a hack: its size, how many target chips (more than one are reached in order), warden chips, whether a
/// firewall sends pulses, how many turns its solution lies away at most and at least, and how many turns the trace-back
/// takes before it raises the alarm. A board with <see cref="HoldAt"/> holds once it is solved that far: the target locks it
/// (<see cref="HackSession.Held"/>), and no turn goes in that would win it (the core's last board, held until the story
/// breaks the hack off).
/// </summary>
public sealed record HackSpec(int Size, int Targets, int Wardens, bool Firewall, int MostTurns, int LeastTurns, int Trace, float HoldAt = 0)
{
    /// <summary>The boards of a kind of target, in the order they are played.</summary>
    public static IReadOnlyList<HackSpec> Boards(HackKind kind) => kind switch
    {
        HackKind.Door or HackKind.Clamp => [new(3, 1, 0, false, 5, 3, 12)],
        HackKind.Grunt => [new(4, 1, 1, false, 8, 4, 18)],
        HackKind.Tower => [new(5, 3, 0, false, 12, 7, 26)],
        HackKind.Pod => [new(5, 1, 0, true, 10, 6, 24)],
        HackKind.Core => [new(4, 1, 1, false, 8, 4, 18), new(5, 3, 0, false, 12, 7, 26), new(5, 1, 1, false, 12, 7, 26, HoldAt: 0.9f)],
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

/// <summary>A chip on the board's edge, by the cell it touches and that cell's side toward it (<see cref="HackPuzzle.N"/> …).</summary>
public readonly record struct HackPort(int Cell, int Side);

/// <summary>What a turn did.</summary>
public readonly record struct HackTurnResult(bool Turned, int Claimed, bool Warden, bool WrongOrder, bool Complete, bool Overflow, string? Refused = null)
{
    public static HackTurnResult No(string why) => new(false, 0, false, false, false, false, why);
}

/// <summary>What a step of the firewall did.</summary>
public enum HackPulse
{
    /// <summary>No pulse on the board, or one resting before the next.</summary>
    None,

    /// <summary>A pulse came out of the firewall chip onto the board.</summary>
    Spawned,

    /// <summary>The pulse ran on by a tile.</summary>
    Moved,

    /// <summary>The pulse ran into a dead end and went out.</summary>
    Died,

    /// <summary>The pulse reached the probe's traces: the hack is traced.</summary>
    Reached,
}

/// <summary>
/// The hacking minigame, pure (docs/STORY.md, Hacken): an N by N board of trace tiles (straight, curve, T, cross, empty),
/// each with its open sides as four bits (N, E, S, W) and a quarter turn. The Locator's probe sits on the west edge; a
/// path of tiles from it to the target chip(s) on the other edges wins the board, once every target is reached in its
/// order and no warden chip is touched by a trace of the probe's. Each turn raises the trace-back; touching a warden or
/// reaching a target out of order raises it more; full, the hack is traced. A firewall chip sends a red pulse over the
/// connected tiles toward the probe, one tile a step; it takes the way nearest the probe at a junction, so the T tiles are
/// the switches that send it into a dead end. Boards are made from a seed: a random path, the rest filled, then the path's
/// tiles turned so that its solution lies within the spec's turns. Free of Godot and of the world, so tests and the
/// client's screenshot can make the same board; the world keeps it in its <see cref="HackSession"/>.
/// </summary>
public sealed class HackPuzzle
{
    public const int N = 1, E = 2, S = 4, W = 8;

    /// <summary>Trace-back for touching a warden, for reaching a target out of its order, and for walking away.</summary>
    public const int WardenCost = 3, WrongOrderCost = 2, AbortCost = 2;

    /// <summary>Firewall steps between a pulse going out and the next, and before the first.</summary>
    public const int PulseRest = 2, FirstPulse = 3;

    private readonly byte[] _base, _turns;
    private readonly bool[] _path;
    private readonly bool[] _warden;
    private ulong _visited;

    private HackPuzzle(HackSpec spec, int seed, byte[] tiles, bool[] path, bool[] warden, HackPort probe, IReadOnlyList<HackPort> targets, HackPort? firewall)
    {
        Spec = spec;
        Seed = seed;
        _base = tiles;
        _turns = new byte[tiles.Length];
        _path = path;
        _warden = warden;
        Probe = probe;
        Targets = targets;
        Firewall = firewall;
        PulseWait = FirstPulse;
    }

    public HackSpec Spec { get; }

    /// <summary>The seed it was made from.</summary>
    public int Seed { get; }

    public int Size => Spec.Size;

    public int Cells => _base.Length;

    /// <summary>The probe's chip: the Locator's contact.</summary>
    public HackPort Probe { get; }

    /// <summary>The target chips, in the order they must be reached.</summary>
    public IReadOnlyList<HackPort> Targets { get; }

    /// <summary>The firewall's chip, on a board that has one.</summary>
    public HackPort? Firewall { get; }

    /// <summary>The cells the warden chips sit on: no tile there.</summary>
    public IEnumerable<int> Wardens => Enumerable.Range(0, Cells).Where(c => _warden[c]);

    /// <summary>How many targets are reached (they stay reached).</summary>
    public int Claimed { get; private set; }

    /// <summary>The trace-back so far, in turns; the alarm at <see cref="HackSpec.Trace"/>.</summary>
    public int Trace { get; private set; }

    /// <summary>How many turns were made.</summary>
    public int TurnsMade { get; private set; }

    /// <summary>All targets reached in order with no warden touched.</summary>
    public bool Complete { get; private set; }

    /// <summary>The cell the firewall's pulse is in, or -1.</summary>
    public int PulseCell { get; private set; } = -1;

    /// <summary>The side of <see cref="PulseCell"/> the pulse came in by.</summary>
    public int PulseFrom { get; private set; }

    /// <summary>Firewall steps before the next pulse comes out.</summary>
    public int PulseWait { get; private set; }

    public float TraceShare => Math.Clamp((float)Trace / Spec.Trace, 0, 1);

    public static int Opposite(int side) => side switch { N => S, E => W, S => N, _ => E };

    /// <summary>A mask turned a quarter clockwise <paramref name="quarters"/> times.</summary>
    public static int Rotate(int mask, int quarters)
    {
        quarters &= 3;
        for (var i = 0; i < quarters; i++)
            mask = ((mask << 1) | (mask >> 3)) & 15;
        return mask;
    }

    public (int X, int Y) At(int cell) => (cell % Size, cell / Size);

    public int CellOf(int x, int y) => y * Size + x;

    /// <summary>The neighbouring cell on a side, or -1 off the board.</summary>
    public int Next(int cell, int side)
    {
        var (x, y) = At(cell);
        (x, y) = side switch { N => (x, y - 1), E => (x + 1, y), S => (x, y + 1), _ => (x - 1, y) };
        return x < 0 || y < 0 || x >= Size || y >= Size ? -1 : CellOf(x, y);
    }

    public bool IsWarden(int cell) => _warden[cell];

    /// <summary>A cell with a tile that turns (not a warden, not empty).</summary>
    public bool IsTile(int cell) => cell >= 0 && cell < Cells && !_warden[cell] && _base[cell] != 0;

    /// <summary>The tile's shape as it was made, unturned.</summary>
    public int Base(int cell) => _base[cell];

    /// <summary>How many quarter turns the tile has from its shape (0 to 3).</summary>
    public int Rotation(int cell) => _turns[cell];

    /// <summary>The open sides of the tile as it lies now.</summary>
    public int Mask(int cell) => _warden[cell] ? 0 : Rotate(_base[cell], _turns[cell]);

    /// <summary>Whether the cell lies on the path the board was made with.</summary>
    public bool OnPath(int cell) => _path[cell];

    /// <summary>Whether a tile lies as the solution has it (any turn giving the same open sides).</summary>
    public bool Solved(int cell) => !_path[cell] || Mask(cell) == _base[cell];

    /// <summary>The turns a tile lies away from its solution, the shorter way round.</summary>
    private int Away(int cell)
    {
        if (!_path[cell])
            return 0;
        var best = 4;
        for (var q = 0; q < 4; q++)
            if (Rotate(_base[cell], (_turns[cell] + q) & 3) == _base[cell])
                best = Math.Min(best, Math.Min(q, 4 - q));
        return best == 4 ? 0 : best;
    }

    /// <summary>Turns from here to the board's own solution: a bound on the fewest that win it.</summary>
    public int Distance => Enumerable.Range(0, Cells).Sum(Away);

    /// <summary>How far the board is solved: its targets reached, and of the rest the share of the path's tiles lying right.</summary>
    public float Progress
    {
        get
        {
            if (Complete)
                return 1;
            var path = Enumerable.Range(0, Cells).Where(c => _path[c]).ToList();
            var right = path.Count(Solved);
            return Math.Min(0.99f, (float)right / Math.Max(1, path.Count));
        }
    }

    /// <summary>The path the board was made with, from the probe's cell to the last target's.</summary>
    public IReadOnlyList<int> Route()
    {
        var route = new List<int>();
        var (cell, from) = (Probe.Cell, Probe.Side);
        while (cell >= 0 && _path[cell] && !route.Contains(cell))
        {
            route.Add(cell);
            var next = -1;
            foreach (var side in (int[])[N, E, S, W])
            {
                if ((_base[cell] & side) == 0 || side == from)
                    continue;
                var n = Next(cell, side);
                if (n < 0 || !_path[n] || route.Contains(n))
                    continue;
                (next, from) = (n, Opposite(side));
                break;
            }
            cell = next;
        }
        return route;
    }

    /// <summary>
    /// The turns that solve the board from where it lies, the shorter way round each tile, from the probe outward, so the
    /// targets come in their order.
    /// </summary>
    public IEnumerable<(int Cell, bool Clockwise)> SolutionTurns()
    {
        foreach (var cell in Route())
        {
            var away = Away(cell);
            if (away == 0)
                continue;
            var clockwise = Rotate(_base[cell], (_turns[cell] + away) & 3) == _base[cell];
            for (var i = 0; i < away; i++)
                yield return (cell, clockwise);
        }
    }

    /// <summary>The cells joined to the probe by open sides that meet.</summary>
    public bool[] Network()
    {
        var joined = new bool[Cells];
        if ((Mask(Probe.Cell) & Probe.Side) == 0)
            return joined;
        var queue = new Queue<int>();
        joined[Probe.Cell] = true;
        queue.Enqueue(Probe.Cell);
        while (queue.Count > 0)
        {
            var cell = queue.Dequeue();
            var mask = Mask(cell);
            foreach (var side in (int[])[N, E, S, W])
            {
                if ((mask & side) == 0)
                    continue;
                var next = Next(cell, side);
                if (next < 0 || joined[next] || _warden[next] || (Mask(next) & Opposite(side)) == 0)
                    continue;
                joined[next] = true;
                queue.Enqueue(next);
            }
        }
        return joined;
    }

    /// <summary>Whether a trace of the probe's opens onto a warden chip.</summary>
    public bool TouchesWarden(bool[] network)
    {
        for (var cell = 0; cell < Cells; cell++)
        {
            if (!network[cell])
                continue;
            var mask = Mask(cell);
            foreach (var side in (int[])[N, E, S, W])
                if ((mask & side) != 0 && Next(cell, side) is var next and >= 0 && _warden[next])
                    return true;
        }
        return false;
    }

    /// <summary>Whether a trace of the probe's opens onto a target chip.</summary>
    public bool Touches(bool[] network, HackPort target) => network[target.Cell] && (Mask(target.Cell) & target.Side) != 0;

    /// <summary>
    /// Turns a tile a quarter, clockwise or back, and sees what the probe's traces now reach: targets in their order are
    /// claimed, a warden touched or a target out of order costs trace-back. Refused for what is no tile, the tile the
    /// firewall's pulse is in, and a board already won.
    /// </summary>
    public HackTurnResult Turn(int cell, bool clockwise)
    {
        if (Complete)
            return HackTurnResult.No("the board is won");
        if (cell < 0 || cell >= Cells || !IsTile(cell))
            return HackTurnResult.No("no tile to turn there");
        if (cell == PulseCell)
            return HackTurnResult.No("the firewall is in that tile");
        _turns[cell] = (byte)((_turns[cell] + (clockwise ? 1 : 3)) & 3);
        TurnsMade++;
        Trace++;
        var network = Network();
        var claimed = 0;
        while (Claimed < Targets.Count && Touches(network, Targets[Claimed]))
        {
            Claimed++;
            claimed++;
        }
        var wrong = false;
        for (var i = Claimed + 1; i < Targets.Count; i++)
            wrong |= Touches(network, Targets[i]);
        var warden = TouchesWarden(network);
        if (warden)
            Trace += WardenCost;
        if (wrong)
            Trace += WrongOrderCost;
        Complete = Claimed == Targets.Count && !warden;
        // A trace joined to the firewall's pulse lets it straight in.
        if (!Complete && PulseCell >= 0 && network[PulseCell])
            Trace = Math.Max(Trace, Spec.Trace);
        return new HackTurnResult(true, claimed, warden, wrong, Complete, !Complete && Trace >= Spec.Trace);
    }

    /// <summary>Adds trace-back (walking away from the board); true when it is full.</summary>
    public bool AddTrace(int turns)
    {
        Trace += turns;
        return Trace >= Spec.Trace;
    }

    /// <summary>
    /// A step of the firewall (one every 1.2 s on the world's clock): a pulse comes out of its chip after a rest, then
    /// runs a tile a step along open sides that meet, never back nor twice through a tile, at a junction the way that
    /// leads nearest the probe (north, east, south, west on a tie). It goes out in a dead end; in a tile of the probe's
    /// traces it has traced the hack.
    /// </summary>
    public HackPulse StepFirewall()
    {
        if (Firewall is not { } wall || Complete)
            return HackPulse.None;
        if (PulseCell < 0)
        {
            if (PulseWait > 0)
            {
                PulseWait--;
                return HackPulse.None;
            }
            if (_warden[wall.Cell] || (Mask(wall.Cell) & wall.Side) == 0)
            {
                PulseWait = PulseRest;
                return HackPulse.Died;
            }
            (PulseCell, PulseFrom, _visited) = (wall.Cell, wall.Side, 1UL << wall.Cell);
            return Network()[PulseCell] ? Out(HackPulse.Reached) : HackPulse.Spawned;
        }
        var mask = Mask(PulseCell);
        var (px, py) = At(Probe.Cell);
        var best = -1;
        var bestSide = 0;
        var bestReach = int.MaxValue;
        foreach (var side in (int[])[N, E, S, W])
        {
            if ((mask & side) == 0 || side == PulseFrom)
                continue;
            var next = Next(PulseCell, side);
            if (next < 0)
            {
                // Off the board: the probe's own edge is the probe.
                if (PulseCell == Probe.Cell && side == Probe.Side)
                    return Out(HackPulse.Reached);
                continue;
            }
            if (_warden[next] || (_visited & (1UL << next)) != 0 || (Mask(next) & Opposite(side)) == 0)
                continue;
            var (nx, ny) = At(next);
            var reach = Math.Abs(nx - px) + Math.Abs(ny - py);
            if (reach < bestReach)
                (best, bestSide, bestReach) = (next, side, reach);
        }
        if (best < 0)
            return Out(HackPulse.Died);
        (PulseCell, PulseFrom) = (best, Opposite(bestSide));
        _visited |= 1UL << best;
        return Network()[best] ? Out(HackPulse.Reached) : HackPulse.Moved;
    }

    private HackPulse Out(HackPulse how)
    {
        (PulseCell, PulseFrom, _visited, PulseWait) = (-1, 0, 0, PulseRest);
        return how;
    }

    /// <summary>A fingerprint of the board as it lies, for the world's checksum.</summary>
    public long Fingerprint()
    {
        long hash = Seed;
        foreach (var turn in _turns)
            hash = hash * 31 + turn;
        hash = hash * 31 + Claimed;
        hash = hash * 31 + Trace;
        hash = hash * 31 + TurnsMade;
        hash = hash * 31 + (Complete ? 1 : 0);
        hash = hash * 31 + PulseCell;
        hash = hash * 31 + PulseFrom;
        hash = hash * 31 + PulseWait;
        hash = hash * 31 + (long)_visited;
        return hash;
    }

    // ---- making a board ----------------------------------------------------------------------------

    /// <summary>A board to the spec from the seed: the same seed, the same board.</summary>
    public static HackPuzzle Generate(HackSpec spec, int seed)
    {
        if (spec.Size is < 3 or > 8)
            throw new ArgumentOutOfRangeException(nameof(spec), "boards are 3 to 8 tiles a side");
        var random = new Random(seed);
        for (var attempt = 0; attempt < 2000; attempt++)
        {
            if (TryGenerate(spec, seed, random) is { } puzzle)
                return puzzle;
        }
        throw new InvalidOperationException($"no board for {spec} from seed {seed}");
    }

    private static HackPuzzle? TryGenerate(HackSpec spec, int seed, Random random)
    {
        var n = spec.Size;
        var cells = n * n;
        int Cell(int x, int y) => y * n + x;

        var probe = new HackPort(Cell(0, random.Next(n)), W);
        var targets = new List<HackPort>();
        if (spec.Targets == 1)
            targets.Add(new HackPort(Cell(n - 1, random.Next(n)), E));
        else
        {
            // Along the east, north and south edges, never on the probe's column, each on a cell of its own.
            var edge = new List<HackPort>();
            for (var i = 0; i < n; i++)
                edge.Add(new HackPort(Cell(n - 1, i), E));
            for (var x = 1; x < n - 1; x++)
            {
                edge.Add(new HackPort(Cell(x, 0), N));
                edge.Add(new HackPort(Cell(x, n - 1), S));
            }
            while (targets.Count < spec.Targets)
            {
                var pick = edge[random.Next(edge.Count)];
                if (targets.All(t => t.Cell != pick.Cell))
                    targets.Add(pick);
            }
        }
        HackPort? firewall = null;
        if (spec.Firewall)
        {
            var x = 1 + random.Next(n - 2);
            var north = random.Next(2) == 0;
            var wall = new HackPort(Cell(x, north ? 0 : n - 1), north ? N : S);
            if (wall.Cell == probe.Cell || targets.Any(t => t.Cell == wall.Cell))
                return null;
            firewall = wall;
        }
        var warden = new bool[cells];
        for (var (placed, tries) = (0, 0); placed < spec.Wardens; tries++)
        {
            if (tries > 200)
                return null;
            var cell = random.Next(cells);
            var (x, y) = (cell % n, cell / n);
            // Not on a chip's cell nor beside the probe, and away from the edges where it would wall off a target.
            if (x == 0 || x == n - 1 || y == 0 || y == n - 1 || warden[cell] || cell == probe.Cell + 1)
                continue;
            warden[cell] = true;
            placed++;
        }

        var path = FindPath(n, probe.Cell, targets.Select(t => t.Cell).ToList(), warden, firewall?.Cell ?? -1, random,
            n + (n >= 4 ? 2 : 1));
        if (path is null)
            return null;
        var onPath = new bool[cells];
        var tiles = new byte[cells];
        for (var i = 0; i < path.Count; i++)
        {
            var cell = path[i];
            onPath[cell] = true;
            var mask = i == 0 ? probe.Side : SideTo(n, cell, path[i - 1]);
            if (i + 1 < path.Count)
                mask |= SideTo(n, cell, path[i + 1]);
            foreach (var target in targets.Where(t => t.Cell == cell))
                mask |= target.Side;
            tiles[cell] = (byte)mask;
        }
        // The rest: straights, curves and a few T pieces and gaps.
        for (var cell = 0; cell < cells; cell++)
        {
            if (onPath[cell] || warden[cell])
                continue;
            var roll = random.Next(10);
            tiles[cell] = (byte)(roll < 1 ? 0 : roll < 4 ? N | S : roll < 8 ? N | E : N | E | S);
        }
        if (firewall is { } fw && tiles[fw.Cell] == 0)
            tiles[fw.Cell] = N | S;

        for (var shuffle = 0; shuffle < 20; shuffle++)
        {
            var puzzle = new HackPuzzle(spec, seed, (byte[])tiles.Clone(), onPath, warden, probe, targets, firewall);
            for (var cell = 0; cell < cells; cell++)
                puzzle._turns[cell] = (byte)(puzzle.IsTile(cell) ? random.Next(4) : 0);
            puzzle.Fit(random);
            // The firewall's own tile opens toward it, so its pulses come onto the board from the start.
            if (firewall is { } gate)
            {
                var open = Enumerable.Range(0, 4).Where(q => (Rotate(tiles[gate.Cell], q) & gate.Side) != 0).ToList();
                puzzle._turns[gate.Cell] = (byte)open[random.Next(open.Count)];
            }
            if (puzzle.Fair())
                return puzzle;
        }
        return null;
    }

    /// <summary>Turns path tiles toward or away from the solution until it lies between the spec's least and most turns.</summary>
    private void Fit(Random random)
    {
        var path = Enumerable.Range(0, Cells).Where(c => _path[c]).ToList();
        // Nearer a turn at a time: a tile half round goes to a quarter off, a quarter off to solved.
        while (Distance > Spec.MostTurns)
        {
            var off = path.Where(c => Away(c) > 0).ToList();
            var cell = off[random.Next(off.Count)];
            _turns[cell] = (byte)(Away(cell) == 2 ? (SolvedTurn(cell) + (random.Next(2) == 0 ? 1 : 3)) & 3 : SolvedTurn(cell));
        }
        for (var guard = 0; Distance < Spec.LeastTurns && guard < 200; guard++)
        {
            var room = path.Where(c => Away(c) < MostAway(c)).ToList();
            if (room.Count == 0)
                break;
            var cell = room[random.Next(room.Count)];
            _turns[cell] = (byte)((_turns[cell] + (random.Next(2) == 0 ? 1 : 3)) & 3);
        }
    }

    /// <summary>The turn at which a path tile lies solved.</summary>
    private int SolvedTurn(int cell)
    {
        for (var q = 0; q < 4; q++)
            if (Rotate(_base[cell], q) == _base[cell])
                return q;
        return 0;
    }

    /// <summary>The furthest a tile can lie from solved: two for curves and T pieces, one for straights, none for a cross.</summary>
    private int MostAway(int cell) => _base[cell] switch
    {
        15 => 0,
        N | S or E | W => 1,
        _ => 2,
    };

    /// <summary>
    /// A board as dealt must not be won nor touch a warden or a target; its solution, played from the probe outward, must
    /// touch no warden and reach the targets in their order; and the firewall's first pulse must die before the probe, as
    /// must every pulse once the board lies solved.
    /// </summary>
    private bool Fair()
    {
        if (Distance < 1 || Distance > Spec.MostTurns)
            return false;
        var network = Network();
        if (TouchesWarden(network) || Targets.Any(t => Touches(network, t)))
            return false;
        var trial = Copy();
        foreach (var (cell, clockwise) in trial.SolutionTurns().ToList())
        {
            var turn = trial.Turn(cell, clockwise);
            if (turn.Warden || turn.WrongOrder)
                return false;
        }
        if (!trial.Complete)
            return false;
        if (Firewall is null)
            return true;
        // A pulse let out on the dealt board at once must die before the probe.
        var dealt = Copy();
        dealt.PulseWait = 0;
        return dealt.RunsDry() && DeadEnds();
    }

    /// <summary>
    /// Whether the board, laid as its solution, sends every pulse of its firewall into a dead end: the solution's tiles
    /// open only along the path, so no pulse can get onto it. True for a board without a firewall.
    /// </summary>
    public bool DeadEnds()
    {
        if (Firewall is null)
            return true;
        var solved = Copy();
        for (var cell = 0; cell < Cells; cell++)
            if (_path[cell])
                solved._turns[cell] = (byte)solved.SolvedTurn(cell);
        solved.PulseWait = 0;
        for (var pulse = 0; pulse < 3; pulse++)
        {
            if (!solved.RunsDry())
                return false;
            solved.PulseWait = 0;
        }
        return true;
    }

    /// <summary>Steps the firewall until its pulse dies; false when it reaches the probe first.</summary>
    private bool RunsDry()
    {
        for (var step = 0; step < Cells + PulseRest + FirstPulse + 2; step++)
        {
            switch (StepFirewall())
            {
                case HackPulse.Reached:
                    return false;
                case HackPulse.Died:
                    return true;
            }
        }
        return true;
    }

    /// <summary>The board as it lies, on its own (its tiles' turns, claims and trace-back, the firewall's pulse).</summary>
    public HackPuzzle Copy()
    {
        var copy = new HackPuzzle(Spec, Seed, _base, _path, _warden, Probe, Targets, Firewall)
        {
            Claimed = Claimed,
            Trace = Trace,
            TurnsMade = TurnsMade,
            Complete = Complete,
            PulseCell = PulseCell,
            PulseFrom = PulseFrom,
            PulseWait = PulseWait,
            _visited = _visited,
        };
        Array.Copy(_turns, copy._turns, _turns.Length);
        return copy;
    }

    private static int SideTo(int n, int from, int to) =>
        to == from - n ? N : to == from + 1 ? E : to == from + n ? S : W;

    /// <summary>
    /// A self-avoiding random walk from the probe's cell through the target cells in order, around the wardens and the
    /// firewall's cell, at least <paramref name="least"/> cells long; null when none was found in time.
    /// </summary>
    private static List<int>? FindPath(int n, int start, List<int> stops, bool[] warden, int firewall, Random random, int least)
    {
        var path = new List<int> { start };
        var on = new bool[n * n];
        on[start] = true;
        var budget = 4000;

        bool Walk(int cell, int stop)
        {
            if (--budget < 0)
                return false;
            while (stop < stops.Count && cell == stops[stop])
                stop++;
            if (stop == stops.Count)
                return path.Count >= least || stops.Count > 1;
            var sides = new List<int> { N, E, S, W };
            for (var i = sides.Count - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (sides[i], sides[j]) = (sides[j], sides[i]);
            }
            foreach (var side in sides)
            {
                var (x, y) = (cell % n, cell / n);
                (x, y) = side switch { N => (x, y - 1), E => (x + 1, y), S => (x, y + 1), _ => (x - 1, y) };
                if (x < 0 || y < 0 || x >= n || y >= n)
                    continue;
                var next = y * n + x;
                // Not through a warden, the firewall's cell, the path itself, or a later target before its turn.
                if (on[next] || warden[next] || next == firewall || stops.Skip(stop + 1).Contains(next))
                    continue;
                path.Add(next);
                on[next] = true;
                if (Walk(next, stop))
                    return true;
                path.RemoveAt(path.Count - 1);
                on[next] = false;
            }
            return false;
        }

        return Walk(start, 0) ? path : null;
    }
}
