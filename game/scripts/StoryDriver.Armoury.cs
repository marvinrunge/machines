using Godot;
using Machines.Sim.Story;
using Machines.Sim.World;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

// Stage 7's driver legs for package W (section 6, the armoury) (plan stufe7 C23, §4 W): from the lockdown to the last marked
// wreck stripped it plays the batches as a player would. In a batch it fights the nearest foe through the real triggers and
// puts a fallen Grunt together; when Old Iron revs at it, or runs down a line it stands on, it sprints sideways out of it. In
// a pause it strips a Bolter off a wreck, mounts it on the Grunt that has none and mends the hurt ones, each by the use key
// held; after the room unseals it strips the marked wrecks. It prints
//   story drive: wave N start tick T seconds S / wave N clear tick T seconds S
//   story drive: pause N: revived R mended M stripped S mounted K
//   story drive: boss charges C hits H crashes X fight S
// and a batch over 150 s fails the run. Pictures: the lockdown, the batch bar (pause 2), the boss's telegraph, the strip
// prompt, the Bolters mounted on Ike and Bob. MACHINES_STORY_DRIVE_STOP=<checkpoint> ends the run once it is reached.
public partial class StoryDriver
{
    /// <summary>A batch that runs longer than this fails the run (the plan's rule).</summary>
    private const double LongestBatch = 150;

    private static readonly int? StopAt = int.TryParse(System.Environment.GetEnvironmentVariable("MACHINES_STORY_DRIVE_STOP"), out var stop) ? stop : null;

    private int _armouryWave;
    private bool _armouryFighting, _armouryBossTold;
    private float? _armouryBossIn;
    private (int Revived, int Mended, int Stripped, int Mounted) _armouryPause;
    private int _armouryRevived;
    private string? _armouryHold;
    private readonly HashSet<string> _armouryShots = [];
    private readonly Queue<(string Name, string? Camera)> _armouryPictures = new();

    /// <summary>One look of the driver at this package's section; true when it drove this frame (the common legs wait).</summary>
    private bool DriveArmoury(Game game)
    {
        var world = game.World;
        if (world.Story is not { } story)
            return false;
        if (StopAt is { } stop && story.Checkpoint >= stop)
        {
            Tell(FormattableString.Invariant($"checkpoint {story.Checkpoint} reached at tick {world.Tick}: stopping as asked"));
            GetTree().Quit();
            return true;
        }
        var director = story.Director;
        var waves = story.Waves;
        if (story.Section != 6 || !director.Has("lockdown") || Game.Me != story.Locator || world.HeroOf(Game.Me) is not { } hero)
            return false;
        Batches(world, waves);
        var marked = waves.Wrecks.FirstOrDefault(w => w.Value.Marked is not null && GameWorld.StripYield(story, w.Value) is not null);
        if (director.Has("cleared") && marked.Value is null)
        {
            LetGoOfArmoury();
            return false;
        }
        if (!_armouryShots.Contains("lockdown") && world.Time >= director.Fired["lockdown"] + 1.5f)
            Shoot("lockdown");

        // A hold under way: kept until the world lets it go.
        if (_armouryHold is not null)
        {
            if (story.Crew.Holding.ContainsKey(story.Locator.Value) && !Charged(world, story, hero))
            {
                Stop(hero);
                return true;
            }
            LetGoOfArmoury();
        }
        if (DodgeCharge(game, story, hero))
            return true;
        // A fallen Grunt is put together once no foe stands within 20 m of it (in a batch the foes come first, as a player's would).
        if (Fallen(game, story) is { } fallen && !waves.Foes.Keys.Any(id => world.Find<Machine>(id) is { IsDead: false } foe
            && NVector2.Distance(foe.Position, fallen.Position) < 20))
        {
            Revive(game, hero, fallen);
            return true;
        }
        if (_reviving)
        {
            Hold(KeyMap.Act.Use, _reviving = false);
            _armouryRevived++;
        }
        if (waves.Fighting)
        {
            var foe = waves.Foes.Keys.Select(id => world.Find<Machine>(id)).OfType<Machine>().Where(m => !m.IsDead)
                .OrderBy(m => NVector2.DistanceSquared(m.Position, hero.Position)).ThenBy(m => m.Id).FirstOrDefault();
            if (foe is null)
            {
                Fire(game, hero, 0, false);
                Wait(game, hero, "the batch's next unit");
                return true;
            }
            Overdue("batch " + waves.Wave, double.MaxValue);
            if (foe.Id == waves.Boss && waves.Phase == StoryChargePhase.Telegraph && !_armouryShots.Contains("boss-telegraph"))
                Shoot("boss-telegraph");
            Attack(game, hero, foe);
            Sprint(game, hero, Forward);
            return true;
        }
        Fire(game, hero, 0, false);
        Sprint(game, hero, false);
        if (waves.Wave == 3 && waves.Bar(world.Time) >= 0.5f && !_armouryShots.Contains("batch-bar"))
            Shoot("batch-bar");
        if (waves.Mounted.Count >= 2)
        {
            Shoot("mounted-ike", "seat1/5/2.2");
            Shoot("mounted-bob", "seat2/5/2.2");
        }
        return Workbench(game, story, hero);
    }

    /// <summary>The pause's work, one hold at a time: mount, strip, mend; false when there is nothing to do.</summary>
    private bool Workbench(Game game, StoryRun story, Machine hero)
    {
        var world = game.World;
        var waves = story.Waves;
        var unarmed = story.Seats.Skip(1).Select(s => world.HeroOf(s)).FirstOrDefault(g => g is { Weapons.Count: 0 } && !world.CompanionDown(story.Seats.First(s => world.HeroOf(s)?.Id == g.Id)));
        if (waves.Carried is not null && unarmed is not null)
            return HoldAt(game, story, hero, GameWorld.MountTarget + story.Seats.First(s => world.HeroOf(s)?.Id == unarmed.Id).Value, unarmed.Position, "mount");
        foreach (var (id, wreck) in waves.Wrecks)
        {
            if (GameWorld.StripYield(story, wreck) is not { } yields || yields == StoryWaves.Bolter && unarmed is null)
                continue;
            return HoldAt(game, story, hero, StoryMarks.WreckTarget + id.ToString(System.Globalization.CultureInfo.InvariantCulture), wreck.Position, "strip");
        }
        if (story.Flags.Contains(StoryMarks.ArmourySealed))
        {
            foreach (var seat in story.Seats.Skip(1))
            {
                if (world.HeroOf(seat) is { } grunt && !world.CompanionDown(seat) && grunt.HitPoints < grunt.MaxHitPoints * 0.85f)
                    return HoldAt(game, story, hero, GameWorld.MendTarget + seat.Value, grunt.Position, "mend");
            }
        }
        // Nothing to do: it waits in the room's middle for the bar.
        var middle = new NVector2(458, 242);
        if (NVector2.Distance(hero.Position, middle) > 3)
            Go(game, hero, "the armoury's middle", middle, 2.5f);
        else
            Stop(hero);
        return true;
    }

    /// <summary>Goes up to a target, then holds the use key once the world offers that target; true while it drives.</summary>
    private bool HoldAt(Game game, StoryRun story, Machine hero, string target, NVector2 at, string what)
    {
        var offered = game.World.StoryUseTarget(Game.Me);
        var distance = NVector2.Distance(hero.Position, at);
        var close = distance <= StoryWaves.Reach - 1.2f;
        if (!close || offered is null || offered != target && distance > 1.6f)
        {
            // Offered something else close by (a Grunt beside the wreck): nearer still, then whatever is offered is held.
            Go(game, hero, what + " " + target, at, close && offered != target ? 1.2f : StoryWaves.Reach - 1.5f);
            return true;
        }
        Stop(hero);
        Look(game, Heading(hero.Position, at), -0.15f);
        if (what == "strip" && !_armouryShots.Contains("strip-prompt"))
        {
            Shoot("strip-prompt");
            return true;
        }
        // Close up and offered something else of the workbench (a Grunt beside the wreck): that is held instead.
        _armouryHold = offered;
        Hold(KeyMap.Act.Use, true);
        return true;
    }

    private void LetGoOfArmoury()
    {
        if (_armouryHold is null)
            return;
        _armouryHold = null;
        Hold(KeyMap.Act.Use, false);
    }

    /// <summary>Whether the boss is about to run the Locator down (its rev or run aimed at it, or the Locator in its line).</summary>
    private static bool Charged(GameWorld world, StoryRun story, Machine hero)
    {
        var waves = story.Waves;
        if (waves.Boss == 0 || waves.Phase is not (StoryChargePhase.Telegraph or StoryChargePhase.Run) || world.Find<Machine>(waves.Boss) is not { IsDead: false } boss)
            return false;
        var rel = hero.Position - boss.Position;
        var ahead = NVector2.Dot(rel, waves.RunWay);
        var across = rel.X * waves.RunWay.Y - rel.Y * waves.RunWay.X;
        return waves.RunTarget == hero.Id || ahead >= 0 && ahead <= GameWorld.ChargeLength + boss.Radius && MathF.Abs(across) <= boss.Radius + hero.Radius + 1;
    }

    /// <summary>Out of the charge's line: a sprint sideways to free ground, as a player told by the rev would.</summary>
    private bool DodgeCharge(Game game, StoryRun story, Machine hero)
    {
        var world = game.World;
        if (!Charged(world, story, hero))
            return false;
        var waves = story.Waves;
        var boss = world.Find<Machine>(waves.Boss)!;
        var rel = hero.Position - boss.Position;
        var across = rel.X * waves.RunWay.Y - rel.Y * waves.RunWay.X;
        var side = new NVector2(waves.RunWay.Y, -waves.RunWay.X) * (across >= 0 ? 1 : -1);
        var mobility = GameWorld.MobilityOf(hero);
        var to = hero.Position + side * 7;
        if (!world.Nav.IsFree(to, mobility))
            to = hero.Position - side * 7;
        Fire(game, hero, 0, false);
        Go(game, hero, "out of the charge", to, 1);
        Sprint(game, hero, true);
        return true;
    }

    /// <summary>The batches' starts and clears, the pauses' work and the boss's fight told; a batch over 150 s fails the run.</summary>
    private void Batches(GameWorld world, StoryWaves waves)
    {
        var work = (_armouryRevived, waves.Mends, waves.Stripped, waves.Mounted.Count);
        if (waves.Fighting && !_armouryFighting)
        {
            if (waves.Wave > 1)
                Tell($"pause {waves.Wave - 1}: revived {work.Item1 - _armouryPause.Revived} mended {work.Mends - _armouryPause.Mended} stripped {work.Stripped - _armouryPause.Stripped} mounted {work.Item4 - _armouryPause.Mounted}");
            Tell(FormattableString.Invariant($"wave {waves.Wave} start tick {world.Tick} seconds {world.Time:0.0}"));
        }
        if (!waves.Fighting && _armouryFighting)
        {
            Tell(FormattableString.Invariant($"wave {_armouryWave} clear tick {world.Tick} seconds {world.Time - waves.WaveStart:0.0}"));
            _armouryPause = work;
        }
        if (waves.Fighting && world.Time - waves.WaveStart > LongestBatch)
            throw new InvalidOperationException(FormattableString.Invariant($"batch {waves.Wave} has run {world.Time - waves.WaveStart:0} s, longer than {LongestBatch} s"));
        if (waves.Boss != 0 && _armouryBossIn is null)
            _armouryBossIn = world.Time;
        if (_armouryBossIn is { } bossIn && !_armouryBossTold && world.Find<Machine>(waves.Boss) is not { IsDead: false })
        {
            _armouryBossTold = true;
            Tell(FormattableString.Invariant($"boss charges {waves.Charges} hits {waves.ChargeHits} crashes {waves.Crashes} fight {world.Time - bossIn:0.0} s"));
        }
        (_armouryWave, _armouryFighting) = (waves.Wave, waves.Fighting);
    }

    /// <summary>A picture of the armoury's, once, taken on the driver's next frame (from a seat's camera if one is given).</summary>
    private void Shoot(string name, string? camera = null)
    {
        if (_armouryShots.Add(name))
            _armouryPictures.Enqueue((name, camera));
    }

    /// <summary>The armoury's next picture due, its camera set for <see cref="BeatPicture"/>; null when none.</summary>
    private string? ArmouryShot()
    {
        if (!_armouryPictures.TryDequeue(out var shot))
            return null;
        _shotCamera = shot.Camera;
        return "armoury-" + shot.Name;
    }
}
