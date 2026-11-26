using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Tests.Story;

/// <summary>The escape run's test worlds and moves (package E): a chapter from a checkpoint, heroes set down, a run along the route.</summary>
internal static class EscapeWorlds
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    public static GameWorld Chapter(int checkpoint, int seed = 3, StoryDifficulty difficulty = StoryDifficulty.Normal) =>
        MatchSetup.Create(DataLoader.Value, StoryOptions.Prologue, StoryOptions.Prologue,
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed,
            new MatchOptions { Story = new StoryOptions { Checkpoint = checkpoint, Difficulty = difficulty }, FogOfWar = false });

    public static Machine Hero(GameWorld world, int seat) => world.HeroOf(world.Story!.Seats[seat])!;

    public static IEnumerable<Machine> Heroes(GameWorld world) => Enumerable.Range(0, 3).Select(s => Hero(world, s));

    /// <summary>Steps until done or the seconds are up, gathering the events; whether it was done.</summary>
    public static bool RunUntil(GameWorld world, Func<bool> done, float seconds, List<GameEvent>? events = null, Action? each = null)
    {
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond && !done(); i++)
        {
            each?.Invoke();
            world.Step();
            events?.AddRange(world.Events);
        }
        return done();
    }

    public static void Run(GameWorld world, float seconds, List<GameEvent>? events = null, Action? each = null) =>
        RunUntil(world, () => false, seconds, events, each);

    /// <summary>A hero set down on free ground for it nearest a point.</summary>
    public static void SetDown(GameWorld world, Machine hero, Vector2 at)
    {
        var mobility = GameWorld.MobilityOf(hero);
        for (var r = 0f; r <= 6; r += 0.5f)
        {
            for (var a = 0; a < 16; a++)
            {
                var spot = at + r * new Vector2(MathF.Cos(a * MathF.PI / 8), MathF.Sin(a * MathF.PI / 8));
                if (world.Nav.IsFree(spot, mobility))
                {
                    hero.Position = spot;
                    return;
                }
            }
        }
        hero.Position = at;
    }

    public static IEnumerable<string> Cues(IEnumerable<GameEvent> events) =>
        events.Where(e => e.Kind == GameEventKind.StoryCue).Select(e => e.Detail!);

    /// <summary>
    /// What a player does on the way that carrying the trio skips (play test 2026-11-25): the Kühlschlange's crate walls shot
    /// through, and door P's panel hacked once the breach has jammed it.
    /// </summary>
    public static void ClearTheWay(GameWorld world)
    {
        foreach (var wall in world.Artefacts.Where(a => a.Tag is GameWorld.Barricade1Tag or GameWorld.Barricade2Tag && !a.IsDead))
            wall.HitPoints = 0;
        if (world.Story is { } run && run.Director.Has("breach"))
            run.Hacks.Done.Add("hack_p");
    }

    /// <summary>
    /// A crate wall still standing ahead in the Kühlschlange, or null: across the lane north of <paramref name="at"/> within
    /// 80 m in its own lane (the route runs both walls' lanes northwards, 56 and 72 m from their feet; the next lane is 20 m
    /// over, behind the maze's wall).
    /// </summary>
    public static Artefact? WallAhead(GameWorld world, Vector2 at) =>
        world.Artefacts.Where(a => a.Tag is GameWorld.Barricade1Tag or GameWorld.Barricade2Tag && !a.IsDead
                && a.Position.Y < at.Y + 3 && at.Y - a.Position.Y < 80 && MathF.Abs(a.Position.X - at.X) < 8)
            .OrderBy(a => Vector2.Distance(a.Position, at)).FirstOrDefault();

    /// <summary>A hack played by its solution, the Locator set down at its place first; whether it was done.</summary>
    public static bool HackBySolution(GameWorld world, Machine locator, string target)
    {
        var run = world.Story!;
        SetDown(world, locator, run.Hacks.Find(target, world.Deck)!.Value.Position);
        world.Issue(new StoryHackOpenCommand(Race.Red, target));
        world.Step();
        var session = run.Hacks.Session(target);
        for (var turns = 0; session is not null && turns < 400 && !run.Hacks.Done.Contains(target) && !run.Over; turns++)
        {
            if (!session.Open)
                world.Issue(new StoryHackOpenCommand(Race.Red, target));
            else if (session.Puzzle!.SolutionTurns().Take(1).ToList() is [var turn])
                world.Issue(new StoryHackTurnCommand(Race.Red, target, turn.Cell, turn.Clockwise));
            world.Step();
        }
        return run.Hacks.Done.Contains(target);
    }

    /// <summary>
    /// The trio carried along the escape's route at a runner's pace from checkpoint 12 (the Locator first, the Grunts 3 m and
    /// 6 m behind), each tick, as far as <paramref name="until"/> metres; <paramref name="each"/> sees every tick.
    /// </summary>
    public static void Carry(GameWorld world, float until, float speed = 6, Action<float>? each = null, float wait = 8)
    {
        var along = 0f;
        for (var i = 0; along < until && i < 4000; i++)
        {
            ClearTheWay(world);
            if (i > wait * GameWorld.TicksPerSecond)
                along += speed * GameWorld.Dt;
            for (var seat = 0; seat < 3; seat++)
                Hero(world, seat).Position = StoryPack.At(MathF.Max(0, along - 3 * seat));
            world.Step();
            each?.Invoke(along);
        }
    }
}
