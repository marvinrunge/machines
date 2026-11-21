using Godot;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Game;

// MACHINES_STORY_COMPANION_TEST=<follow|distract|downed> (with MACHINES_STORY=prolog and MACHINES_MAP=prolog, which skips
// the menus): a check of the companions in the running game. Once the chapter has run a second, the trio is set down in
// the lane of maintenance deck A (80, 26) and one scenario plays:
//   follow   - the Locator is put 30 m on (110, 26), looking back; the Grunts come after it and stop 3-9 m off.
//   distract - the Locator orders a distraction 12 m ahead; the nearer Grunt goes round it, honks and flees back.
//   downed   - Ike is shot down (1 HP, flag down:N); the Locator beside it holds Use until Ike stands again.
// Each second the Grunts' places, orders, steps, flags and barks are logged (GD.Print and MACHINES_STORY_COMPANION_TEST_LOG,
// default user://story-companion-test.log), with the world's checksum. MACHINES_STORY_COMPANION_TEST_SHOT=<png> saves the
// window at the scenario's moment (the Grunts arrived, the honk, the Grunt down), and the game quits at the end.
public partial class Game
{
    private CompanionCheck? _companionCheck;

    private void CompanionTestIfAsked()
    {
        if (World.Story is null)
            return;
        if (_companionCheck is null)
        {
            if (System.Environment.GetEnvironmentVariable("MACHINES_STORY_COMPANION_TEST") is not { Length: > 0 } scenario)
                return;
            _companionCheck = new CompanionCheck(scenario.Trim().ToLowerInvariant());
        }
        _companionCheck.Update(this);
    }

    private sealed class CompanionCheck(string scenario)
    {
        private static readonly System.Numerics.Vector2 Spot = new(80, 26);
        private readonly List<string> _log = [];
        private int _start = -1, _lastLog = -1, _barks;
        private bool _shot, _done, _revived;

        private void Note(string line)
        {
            _log.Add(line);
            GD.Print($"companion test: {line}");
        }

        public void Update(Game game)
        {
            var world = game.World;
            if (_done || world.Story is not { } run || run.Seats.Count < 3)
                return;
            if (_start < 0)
            {
                if (world.Tick < GameWorld.TicksPerSecond)
                    return;
                _start = world.Tick;
                Begin(game, run);
                return;
            }
            var seconds = (world.Tick - _start) / (float)GameWorld.TicksPerSecond;
            foreach (var bark in run.Barks.Skip(_barks))
                Note($"bark {bark.Line} by {bark.Speaker} at tick {bark.Tick}");
            _barks = run.Barks.Count;
            if (world.Tick - _lastLog >= GameWorld.TicksPerSecond)
            {
                _lastLog = world.Tick;
                Report(world, run, seconds);
            }
            switch (scenario)
            {
                case "follow":
                    if (seconds >= 1 && seconds < 1.2f && world.HeroOf(run.Locator) is { } locator && locator.Position.X < 100)
                        Place(game, run.Locator, Spot + new System.Numerics.Vector2(30, 0), MathF.PI);
                    if (!_shot && seconds >= 4 && run.Seats.Skip(1).All(s => world.HeroOf(s) is { } g && world.HeroOf(run.Locator) is { } l
                            && System.Numerics.Vector2.Distance(g.Position, l.Position) < 9))
                        Shoot(game, "both Grunts within 9 m of the Locator");
                    if (seconds >= 20)
                        Finish(game, run.Seats.Skip(1).All(s => world.HeroOf(s) is { } g && world.HeroOf(run.Locator) is { } l
                            && System.Numerics.Vector2.Distance(g.Position, l.Position) is >= 3 and <= 9));
                    break;
                case "distract":
                    if (!_shot && run.Crew.Grunts.Values.Any(c => c.Step == CompanionStep.Honking))
                        Shoot(game, $"honking, {run.Crew.Noises.Count} noise(s)");
                    if (seconds >= 16)
                        Finish(game, run.Crew.Noises.Count > 0 && run.Crew.Grunts.Values.Any(c => c.Step == CompanionStep.Fleeing || c.Order == CompanionOrder.Wait));
                    break;
                case "downed":
                    var ike = run.Seats[1];
                    if (!_shot && seconds >= 1 && world.CompanionDown(ike))
                        Shoot(game, "Ike down");
                    if (seconds >= 1.5f && seconds < 1.6f)
                        game.Issue(new StoryUseCommand(Player, world.StoryUseTarget(run.Locator)));
                    if (!_revived && seconds >= 1.6f && !world.CompanionDown(ike))
                    {
                        _revived = true;
                        Note(FormattableString.Invariant($"Ike revived at {seconds:0.0} s with {world.HeroOf(ike)?.HitPoints} HP"));
                        game.Issue(new StoryUseCommand(Player, Held: false));
                    }
                    if (seconds >= 10)
                        Finish(game, _revived);
                    break;
                default:
                    Note($"unknown scenario '{scenario}' (follow, distract, downed)");
                    Finish(game, false);
                    break;
            }
        }

        private void Begin(Game game, StoryRun run)
        {
            var world = game.World;
            Note($"scenario {scenario} at tick {world.Tick}, seats {string.Join(" ", run.Seats.Select(s => s.Value))}, bots {string.Join(" ", run.Bots.Select(s => s.Value))}");
            foreach (var seat in run.Seats.Skip(1))
                world.SetCompanionDormant(seat, false);
            Place(game, run.Locator, Spot, 0);
            Place(game, run.Seats[1], Spot + new System.Numerics.Vector2(-6, 2), 0);
            Place(game, run.Seats[2], Spot + new System.Numerics.Vector2(-6, -2), 0);
            switch (scenario)
            {
                case "distract":
                    game.Issue(new StoryOrderCommand(Player, CompanionOrder.Distract, Spot + new System.Numerics.Vector2(12, 0)));
                    break;
                case "downed":
                    // The Locator stands 3 m from Ike, looking at it.
                    Place(game, run.Locator, Spot + new System.Numerics.Vector2(-3, 2), MathF.PI);
                    if (world.HeroOf(run.Seats[1]) is { } ike)
                        world.Damage(ike, 100000, null);
                    break;
            }
        }

        private static void Place(Game game, PlayerId seat, System.Numerics.Vector2 at, float facing)
        {
            if (game.World.HeroOf(seat) is not { } hero)
                return;
            hero.Position = at;
            hero.Facing = facing;
            hero.Path.Clear();
            hero.PathIndex = 0;
            if (seat == Me)
                game._aimYaw = facing;
            game.SyncViews();
        }

        private void Report(GameWorld world, StoryRun run, float seconds)
        {
            var locator = world.HeroOf(run.Locator);
            var parts = new List<string> { FormattableString.Invariant($"t={seconds:0.0}s"), FormattableString.Invariant($"locator=({locator?.Position.X:0.0},{locator?.Position.Y:0.0})") + (locator is not null && world.DriveInput.TryGetValue(locator.Id, out var input) ? FormattableString.Invariant($" drive {input.Forward:R}/{input.Turn:R}/{input.Strafe:R}") : "") + (locator?.DrivenByPlayer == true ? " at the wheel" : "") };
            foreach (var seat in run.Seats.Skip(1))
            {
                var crew = run.Crew.Of(seat);
                var grunt = world.HeroOf(seat);
                var off = grunt is null || locator is null ? float.NaN : System.Numerics.Vector2.Distance(grunt.Position, locator.Position);
                parts.Add(FormattableString.Invariant($"{CompanionBarks.Speaker(run.Seats.IndexOf(seat))}=({grunt?.Position.X:0.0},{grunt?.Position.Y:0.0}) off {off:0.0} m")
                    + FormattableString.Invariant($" hp {grunt?.HitPoints} order {crew.Order} step {crew.Step} job {grunt?.Order?.GetType().Name}")
                    + (world.CompanionDown(seat) ? " DOWN" : "") + (grunt is not null && world.Stunned(grunt) ? " STUNNED" : "") + $" revive {crew.ReviveTicks}");
            }
            parts.Add($"noises {run.Crew.Noises.Count} flags [{string.Join(",", run.Flags)}] sum {world.Checksum():x16}");
            Note(string.Join(" | ", parts));
        }

        private void Shoot(Game game, string why)
        {
            _shot = true;
            Note($"moment: {why}");
            if (System.Environment.GetEnvironmentVariable("MACHINES_STORY_COMPANION_TEST_SHOT") is not { Length: > 0 } path)
                return;
            // A frame later, so the views have followed the world.
            game.GetTree().CreateTimer(0.1).Timeout += () =>
            {
                game.GetViewport().GetTexture().GetImage().SavePng(path);
                GD.Print($"companion test: picture saved to {path}");
            };
        }

        private void Finish(Game game, bool passed)
        {
            _done = true;
            Note(passed ? $"PASS {scenario}" : $"FAIL {scenario}");
            var path = System.Environment.GetEnvironmentVariable("MACHINES_STORY_COMPANION_TEST_LOG") is { Length: > 0 } asked ? asked : "user://story-companion-test.log";
            var real = path.StartsWith("user://", StringComparison.Ordinal) ? ProjectSettings.GlobalizePath(path) : path;
            System.IO.File.WriteAllLines(real, _log);
            GD.Print($"companion test: {_log.Count} lines written to {real}");
            game.GetTree().CreateTimer(0.5).Timeout += () => game.GetTree().Quit();
        }
    }
}
