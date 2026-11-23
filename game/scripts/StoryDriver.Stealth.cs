using Godot;
using Machines.Sim.Story;
using Machines.Sim.World;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

// Stage 7's driver legs for package S (section 2, the Thresher) (plan stufe7 C23, design-stealth §12): the Locator sneaks
// through the section as a player would. It reads him each frame (where he is, which way his beam points, his state, his
// suspicion of L-7) and goes by the design's go rules: to the niche and still through the window; off to the Graben once he
// left a1 eastbound past x 170; the Bahnsprung once he stands at the east end; along the Graben into V1a, creeping; the ducts
// to R7 and hack_s as the common legs go; out of V1c, down the crawl way to the Nordgang once he is at b1 or westbound
// beyond b2; at wait_c the lure with the distract key and the view on valve_o; hack_h while he searches at the pipe's far
// end. Everywhere in the section it stands still (creeping) while his beam could find it (within 42 m, inside 35° of his
// beam, his eye clear, or nearer than 16 m, or his suspicion of it rising), and creeps whenever he is within 35 m. A fallen
// Grunt is put together once he is more than 40 m off and not facing it. It prints
//   story drive: thresher round R point P at S s (lap L, D s since the last)  and  thresher lap: round R S s (plan P)
//   story drive: window: <leg> he at D m, <state>   (each time it goes after a hide)
//   story drive: section 2: S s, wary W, sure U, hits H, downs D, revives R, holds K, checkpoints <n at s, ...>
// and fails the run when he is sure of L-7 (spotted, or drawing on it) without a mode asking for it.
// MACHINES_STORY_DRIVE_STEALTH=caught: on the Bahnsprung the Grunts are told to wait on the lane once, so one goes down; the
// driver puts it together again and says how long that took. =die: once the window is over L-7 switches its lamp on and
// stands in his cone until it is lost; after the restart it says how far he is and whether he finds it again within 10 s.
public partial class StoryDriver
{
    private static readonly string StealthMode = System.Environment.GetEnvironmentVariable("MACHINES_STORY_DRIVE_STEALTH")?.Trim().ToLowerInvariant() ?? "";

    /// <summary>He could see L-7 standing: within this many metres, inside this many degrees of his beam, his eye clear; or nearer than <see cref="CloseRise"/>.</summary>
    private const float SeenWithin = 42, SeenAngle = 35, CloseRise = 16;

    /// <summary>Within this many metres of him L-7 creeps.</summary>
    private const float CreepWithin = 35;

    /// <summary>The lap of each round by the design (design-stealth §3.6), seconds.</summary>
    private static readonly Dictionary<string, float> RoundPlan = new() { ["a"] = 100, ["ring"] = 150, ["b"] = 95 };

    private bool _stealthOn, _creeping, _holding, _stealthDone, _caughtTold, _diedOnce, _lureTold, _drawSeen;
    private double _stealthFrom, _holdFrom, _lureAt = -100, _restartAt = -1, _caughtAt = -1, _downAt = -1;
    private int _wary, _sure, _hits, _downs, _revives, _holds, _newsTick = -1, _lastCheckpoint = -1, _pointIndex = -1, _lap;
    private string _window = "", _lastWord = "";
    private float _pointAt, _lapFrom = -1;
    private readonly List<string> _checkpoints = [];
    private readonly HashSet<int> _downSeen = [];

    /// <summary>One look of the driver at this package's section; true when it drove this frame (the common legs wait).</summary>
    private bool DriveStealth(Game game)
    {
        var world = game.World;
        if (world.Story is not { } story || Game.Me != story.Locator || world.HeroOf(Game.Me) is not { } hero)
            return false;
        var him = StoryStalker.Of(world);
        if (him is null || !story.Flags.Contains(StoryMarks.Stealth))
        {
            EndStealth(world, story);
            return false;
        }
        // The common lamp check first (it leaves the lamp on; this section switches it off again).
        if (_lampChecked is null)
            return false;
        if (!_stealthOn)
        {
            (_stealthOn, _stealthFrom, _stealthDone) = (true, world.Time, false);
            Tell(FormattableString.Invariant($"stealth: section 2 driven from checkpoint {story.Checkpoint}, mode {(StealthMode.Length > 0 ? StealthMode : "clean")}"));
            AfterRestart(world, story, hero, him);
        }
        Watch(world, story, hero, him);
        var director = story.Director;
        var desk = story.Hacks;
        var dying = StealthMode == "die" && !_diedOnce && director.Has("window_off");
        // The lamp: off for the whole section (but for the mode that walks into his light).
        if (world.StoryLampOn(hero.Id) != dying && Time.GetTicksMsec() / 1000.0 - _lastUse > 1)
        {
            _lastUse = Time.GetTicksMsec() / 1000.0;
            _ = Tap(KeyMap.Of(KeyMap.Act.Light));
        }
        if (dying)
            return Die(game, hero, him);

        // H is open: the rush to the antechamber, no creeping (the common leg goes to R8).
        if (desk.Done.Contains("hack_h"))
        {
            Creep(false);
            return false;
        }
        var distance = NVector2.Distance(hero.Position, him.Position);
        // A fallen Grunt: put together once he is far and not facing it.
        if (Fallen(game, story) is { } fallen)
        {
            if (NVector2.Distance(him.Position, fallen.Position) > 40 && !Facing(world, him, fallen.Position, SeenAngle) && !Exposed(world, him, hero))
            {
                Creep(distance < CreepWithin);
                Revive(game, hero, fallen);
                return true;
            }
            if (_reviving)
                Hold(KeyMap.Act.Use, _reviving = false);
            return Hide(game, hero, him, "a fallen Grunt");
        }
        if (_reviving)
        {
            Hold(KeyMap.Act.Use, _reviving = false);
            _revives++;
            if (_downAt >= 0)
            {
                Tell(FormattableString.Invariant($"stealth: the Grunt is up again {world.Time - _downAt:0.0} s after it went down"));
                _downAt = -1;
            }
        }
        // A board up: played on as the common legs do, even while he looks (it stands still anyway).
        if (desk.Session("hack_s") is { Open: true } || desk.Session("hack_h") is { Open: true })
            return false;
        if (Exposed(world, him, hero))
            return Hide(game, hero, him, "his beam");
        Creep(distance < CreepWithin);
        GoOn();
        return Legs(game, story, hero, him);
    }

    /// <summary>The section's own legs, by the go rules; false where the common legs take over (the ducts, the hacks).</summary>
    private bool Legs(Game game, StoryRun story, Machine hero, Machine him)
    {
        var world = game.World;
        var director = story.Director;
        var deck = world.Deck!;
        NVector2 Marker(string name) => StoryMarks.Locate(deck, name) ?? hero.Position;
        bool In(params string[] zones) => zones.Any(name => deck.Zones.FirstOrDefault(z => z.Name == name) is { } zone && zone.Contains(hero.Position));
        if (!story.Hacks.Done.Contains("hack_s"))
        {
            // Deck A: the niche, the Graben, the Bahnsprung, V1a. In the ducts and R7 the common leg goes to hack_s.
            if (In("V1am", "V1aw", "V1a", "V1b", "R5", "R7") || hero.Position.Y > 82 && hero.Position.X > 150)
                return false;
            var west = hero.Position.X < 146;
            if (west)
            {
                if (!director.Has("window_off") || !WentEast(him, 170))
                {
                    if (Go(game, hero, "the niche", Marker("niche_l7"), 1.5f))
                        Wait(game, hero, director.Has("window_off") ? "him past x 170 eastbound" : "the window");
                    return true;
                }
                Window("niche → x_g0", hero, him);
                Go(game, hero, "to the Graben (x_g0)", Marker("x_g0"), 1.5f);
                return true;
            }
            if (hero.Position.X < 178)
            {
                // The Bahnsprung: once he stands at the east end (a3/a4) or is eastbound past x 225.
                if (him.Position.X < 225)
                {
                    if (Go(game, hero, "x_g0", Marker("x_g0"), 1.5f))
                        Wait(game, hero, "him at the east end (Bahnsprung)");
                    return true;
                }
                Window("Bahnsprung x_g0 → x_g2", hero, him);
                if (StealthMode == "caught" && !_caughtTold)
                {
                    _caughtTold = true;
                    _caughtAt = world.Time;
                    _ = Tap(KeyMap.Of(KeyMap.Act.GruntWait));
                    Tell("stealth: caught mode: the Grunts told to wait on the lane");
                }
                Go(game, hero, "the Bahnsprung (x_g2)", Marker("x_g2"), 1.5f);
                return true;
            }
            // Along the Graben into V1a's mouth, creeping; still while he dwells at a2.
            Creep(true);
            Window("x_g2 → V1a", hero, him);
            Go(game, hero, "V1a's mouth", new NVector2(198, 86), 1.2f);
            return true;
        }
        if (!director.Has("guard") && !In("nordgang"))
        {
            // Deck B: from V1c's top down the crawl way to the Nordgang once he is at b1 or westbound beyond b2.
            if (In("R7", "V1b", "V1c") && !SafeOnDeckB(him))
            {
                if (Go(game, hero, "V1c's top", Marker("cp1c_l7"), 1.5f))
                    Wait(game, hero, "him at b1 or westbound beyond b2");
                return true;
            }
            Window("V1c → crawl way → Nordgang", hero, him);
            Go(game, hero, "the Nordgang (wait_c)", Marker("wait_c"), 1.5f);
            return true;
        }
        if (!director.Has("guard"))
        {
            if (Go(game, hero, "wait_c", Marker("wait_c"), 1.5f))
                Wait(game, hero, "the guard");
            return true;
        }
        // The guard at H: the lure, then the hack while he searches at the pipe's far end.
        var searching = him.Order is SearchOrder && NVector2.Distance(him.Position, Marker("hack_h")) > 50;
        if (searching)
        {
            Window("wait_c → console", hero, him);
            return false;
        }
        if (!Go(game, hero, "wait_c", Marker("wait_c"), 1.5f))
            return true;
        Wait(game, hero, "him drawn off by a valve");
        var valve = story.Stealth.Bangs.ContainsKey("valve_o") && !story.Stealth.Spent.Contains("valve_w") ? "valve_w" : "valve_o";
        LookAt(game, ActorView.World(Marker(valve)) + Vector3.Up * 0.5f);
        var now = world.Time;
        if (director.Has("offer") && now - _lureAt > 15 && him.Order is not SearchOrder && !story.Stealth.Spent.Contains(valve))
        {
            _lureAt = now;
            _ = Tap(KeyMap.Of(KeyMap.Act.GruntDistract));
            Tell(FormattableString.Invariant($"stealth: the lure: distract key with the view on {valve}, he at {NVector2.Distance(hero.Position, him.Position):0} m"));
            _lureTold = true;
        }
        return true;
    }

    // Whether he has left a1 eastbound past x (the niche's go rule).
    private static bool WentEast(Machine him, float x) => him.Position.X > x;

    // Deck B's go rule: he is at b1 (the west end) or westbound beyond b2.
    private static bool SafeOnDeckB(Machine him) =>
        him.Position.X < 160 || him.Position.X < 192 && him.Order is PatrolOrder { Points: var points, Next: var next } && next < points.Count && points[next].X < him.Position.X;

    /// <summary>Whether he could see L-7 now: in his beam within reach, his eye clear; up close; or growing suspicious of it.</summary>
    private static bool Exposed(GameWorld world, Machine him, Machine hero)
    {
        var distance = NVector2.Distance(hero.Position, him.Position);
        if (world.StorySuspicionOf(hero.Id) >= 0.1f)
            return true;
        if (distance < CloseRise)
            return true;
        return distance <= SeenWithin && Facing(world, him, hero.Position, SeenAngle) && world.StoryEyeClear(him, hero);
    }

    private static bool Facing(GameWorld world, Machine him, NVector2 at, float degrees)
    {
        var way = at - him.Position;
        if (way.LengthSquared() < 0.01f)
            return true;
        var off = MathF.IEEERemainder(MathF.Atan2(way.Y, way.X) - world.GunHeading(him), MathF.Tau);
        return MathF.Abs(off) <= degrees * MathF.PI / 180;
    }

    /// <summary>Stands still, creeping, while he could see it; the leg's clock waits meanwhile.</summary>
    private bool Hide(Game game, Machine hero, Machine him, string why)
    {
        Creep(true);
        Stop(hero);
        Fire(game, hero, 0, false);
        if (!_holding)
        {
            (_holding, _holdFrom) = (true, Time.GetTicksMsec() / 1000.0);
            _holds++;
            _window = why;
        }
        return true;
    }

    // Going again after a hide: the leg's clock runs on from where it stopped.
    private void GoOn()
    {
        if (!_holding)
            return;
        _holding = false;
        _legSince += Time.GetTicksMsec() / 1000.0 - _holdFrom;
    }

    private void Creep(bool on)
    {
        // Held again each frame: the common loop lets every key go in a cutscene.
        _creeping = on;
        Hold(KeyMap.Act.Creep, on);
    }

    // A window taken: said once a leg.
    private void Window(string leg, Machine hero, Machine him)
    {
        if (_window == leg)
            return;
        _window = leg;
        Tell(FormattableString.Invariant($"window: {leg} he at {NVector2.Distance(hero.Position, him.Position):0} m, {_lastWord}"));
    }

    /// <summary>The die mode: the lamp on, into his cone, standing until L-7 is lost.</summary>
    private bool Die(Game game, Machine hero, Machine him)
    {
        Creep(false);
        if (Go(game, hero, "into his light", new NVector2(128, 74), 1.5f))
        {
            Stop(hero);
            Look(game, Heading(hero.Position, him.Position), 0.05f);
        }
        return true;
    }

    // After a restart from a checkpoint in the die mode: how far he is, and whether he finds L-7 again within 10 s.
    private void AfterRestart(GameWorld world, StoryRun story, Machine hero, Machine him)
    {
        if (StealthMode != "die" || !_diedOnce || _restartAt >= 0)
            return;
        _restartAt = world.Time;
        Tell(FormattableString.Invariant($"stealth: restarted at checkpoint {story.Checkpoint}, he {NVector2.Distance(hero.Position, him.Position):0} m from L-7"));
    }

    /// <summary>Each frame: his round's points and laps, his state's word, the sight's news, the downs, the checkpoints.</summary>
    private void Watch(GameWorld world, StoryRun story, Machine hero, Machine him)
    {
        if (StoryStalker.Look(world) is { } look)
            _lastWord = StoryStalker.Word(look.State);
        if (story.Checkpoint != _lastCheckpoint)
        {
            if (_lastCheckpoint >= 0)
                _checkpoints.Add(FormattableString.Invariant($"{story.Checkpoint} at {world.Time - _stealthFrom:0} s"));
            _lastCheckpoint = story.Checkpoint;
        }
        // His round: each point as he reaches it, each lap against the plan.
        if (story.Stealth.RoundOf.TryGetValue(him.Id, out var round) && StoryStealth.Rounds.TryGetValue(round, out var points))
        {
            for (var i = 0; i < points.Count; i++)
            {
                if (i == _pointIndex || StoryMarks.Locate(world.Deck, points[i]) is not { } point || NVector2.Distance(point, him.Position) > 2)
                    continue;
                Tell(FormattableString.Invariant($"thresher round {round} point {i + 1} ({points[i]}) at {world.Time - _stealthFrom:0.0} s (lap {_lap}, {world.Time - _pointAt:0.0} s since the last)"));
                (_pointIndex, _pointAt) = (i, world.Time);
                if (i == 0)
                {
                    if (_lapFrom >= 0 && RoundPlan.TryGetValue(round, out var plan))
                    {
                        var lap = world.Time - _lapFrom;
                        Tell(FormattableString.Invariant($"thresher lap: round {round} {lap:0.0} s (plan {plan:0} s{(MathF.Abs(lap - plan) > 5 ? ", OFF PLAN" : "")})"));
                    }
                    (_lapFrom, _lap) = (world.Time, _lap + 1);
                }
            }
        }
        // The sight's news of this tick, once.
        if (world.Tick != _newsTick)
        {
            _newsTick = world.Tick;
            foreach (var news in story.Sight.News.Where(n => n.Seeker == him.Id))
            {
                if (news.Kind == StorySightKind.Suspicious)
                    _wary++;
                if (news.Kind == StorySightKind.Spotted && news.Hero == hero.Id)
                {
                    _sure++;
                    Sure(world, story, hero, "spotted");
                }
            }
            foreach (var e in world.Events.Where(e => e.Kind == GameEventKind.Hit && e.Other == him.Id && story.Seats.Any(s => world.HeroOf(s)?.Id == e.Actor)))
                _hits++;
        }
        foreach (var seat in story.Seats.Skip(1))
        {
            var down = world.CompanionDown(seat);
            if (down && _downSeen.Add(seat.Value))
            {
                _downs++;
                _downAt = world.Time;
                Tell(FormattableString.Invariant($"stealth: the Grunt of seat {seat.Value} is down{(_caughtAt >= 0 ? $", {world.Time - _caughtAt:0.0} s after it was told to wait" : "")}"));
                if (_caughtAt >= 0)
                {
                    _caughtAt = -1;
                    _ = Tap(KeyMap.Of(KeyMap.Act.GruntFollow));
                }
            }
            else if (!down)
                _downSeen.Remove(seat.Value);
        }
        if (_caughtAt >= 0 && world.Time - _caughtAt > 60)
        {
            Tell("stealth: caught mode: no Grunt went down within 60 s of the wait");
            _caughtAt = -1;
            _ = Tap(KeyMap.Of(KeyMap.Act.GruntFollow));
        }
        if (_restartAt >= 0 && world.Time - _restartAt <= 10 && story.Sight.News.Any(n => n.Kind == StorySightKind.Spotted && n.Hero == hero.Id))
        {
            Tell("stealth: FOUND AGAIN within 10 s of the restart");
            _restartAt = double.MaxValue;
        }
        // Drawing on L-7 counts as sure as well.
        var drawing = StoryStalker.Look(world) is { State: StalkerState.Draw, Quarry: var quarry } && quarry == hero.Id;
        if (drawing && !_drawSeen)
            Sure(world, story, hero, "drawing on it");
        _drawSeen = drawing;
        if (StealthMode == "die" && !_diedOnce && hero.HitPoints <= 0)
            _diedOnce = true;
    }

    // He is sure of L-7: the run fails, unless a mode asked for it.
    private void Sure(GameWorld world, StoryRun story, Machine hero, string how)
    {
        if (StealthMode == "die")
        {
            if (!_diedOnce)
                Tell(FormattableString.Invariant($"stealth: die mode: he is sure of L-7 ({how}) at {world.Time - _stealthFrom:0.0} s"));
            _diedOnce = true;
            return;
        }
        if (StealthMode == "caught")
        {
            Tell(FormattableString.Invariant($"stealth: he is sure of L-7 ({how}) at {world.Time - _stealthFrom:0.0} s (caught mode: counted)"));
            return;
        }
        throw new InvalidOperationException(FormattableString.Invariant(
            $"stealth: L-7 spotted ({how}) at {hero.Position.X:0.0},{hero.Position.Y:0.0}, he at {NVector2.Distance(hero.Position, world.Find<Machine>(StoryStalker.Of(world)?.Id ?? 0)?.Position ?? hero.Position):0} m, window '{_window}', checkpoint {story.Checkpoint}"));
    }

    // The section is over (H shut behind the trio), or left: its line, once.
    private void EndStealth(GameWorld world, StoryRun story)
    {
        if (!_stealthOn)
            return;
        _stealthOn = false;
        if (_creeping)
            Creep(false);
        if (_stealthDone)
            return;
        _stealthDone = true;
        Tell(FormattableString.Invariant($"section 2: {world.Time - _stealthFrom:0} s, wary {_wary}, sure {_sure}, hits {_hits}, downs {_downs}, revives {_revives}, holds {_holds}, checkpoints {(_checkpoints.Count > 0 ? string.Join(", ", _checkpoints) : "none")}{(_lureTold ? ", lured" : "")}"));
    }
}
