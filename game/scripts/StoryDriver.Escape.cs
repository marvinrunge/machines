using Machines.Sim.Story;
using Machines.Sim.World;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

// Stage 7's driver legs for package E (section 7, the escape) (plan stufe7 C23, §4 E): from the escape's start (checkpoint
// 12, or 13, 14) it runs the Locator through the Notgang to door P (waiting for the breach to blow it), along the hold's
// aisle with a wait before each lane while its container telegraphs or slides (and until the drive has cooled enough to
// sprint across), to the west landing and through the shutter X (the hack of its panel if it came down), down the gallery
// along the free side of each jet and before J4 until it stays dark, over the footbridge into the bay. There the common legs
// take over (the pod's hack, its hatch, everyone in). It puts a fallen Grunt together when no hunter stands near it. Every
// beat of the section it prints as it fires, with the seconds left: "story drive: escape: <beat> at T-<s>"; the launch the
// same, and the total.
// MACHINES_STORY_DRIVE_SHUTDOWN=1 (or 2) has it stand still in the hold's west landing, facing the pack, until the
// Notabschaltung has come and gone that often; after 40 s without one it shoots its own Locator down (a fallback, counted).
public partial class StoryDriver
{
    /// <summary>How many Notabschaltungen the escape's driver waits for before the gallery (MACHINES_STORY_DRIVE_SHUTDOWN).</summary>
    private static readonly int ShutdownsWanted =
        int.TryParse(System.Environment.GetEnvironmentVariable("MACHINES_STORY_DRIVE_SHUTDOWN"), out var wanted) ? Math.Clamp(wanted, 0, 3) : 0;

    /// <summary>How long it stands waiting for a shutdown before it shoots its own Locator down, seconds.</summary>
    private const double ShutdownPatience = 40;

    /// <summary>The gallery's way along the jets' free sides (J1 east, J2 west, J3 east of the way, J4 across: waited for, J5 west).</summary>
    private static readonly NVector2[] GalleryWay = [new(336, 141), new(336, 133), new(345, 133), new(345, 121), new(335, 121), new(335, 110), new(345, 99), new(345, 92)];

    /// <summary>The gallery's step before J4, where it waits until the jet stays dark long enough.</summary>
    private const int BeforeJ4 = 5;

    private Task? _escapeStep;
    private readonly HashSet<string> _escapeBeats = [];
    private int _galleryStep;
    private double _shutdownWaitFrom = -1;
    private bool _escapeLaunchTold;

    /// <summary>One look of the driver at this package's section; true when it drove this frame (the common legs wait).</summary>
    private bool DriveEscape(Game game)
    {
        var world = game.World;
        if (world.Story is not { } story)
            return false;
        EscapeBeats(world, story);
        if (story.Section != 7 || !story.Flags.Contains(StoryMarks.Escape) || Game.Me != story.Locator || world.HeroOf(Game.Me) is not { } hero)
            return false;
        // A step of the common helpers that awaits (a hack's key, the use key) runs to its end first.
        if (_escapeStep is { IsCompleted: false })
            return true;
        var deck = world.Deck!;
        var at = hero.Position;
        bool In(string zone) => deck.Zone(zone) is { } z && z.Contains(at);
        // The bay: the common legs (the pod's hack, its hatch, everyone aboard).
        if (In("R16"))
        {
            LetGoOfEscape();
            return false;
        }
        // A fallen Grunt is put together when no hunter stands within 20 m of it.
        if (Fallen(game, story) is { } fallen && !world.PackAlive(story).Any(m => NVector2.Distance(m.Position, fallen.Position) < 20))
        {
            Revive(game, hero, fallen);
            return true;
        }
        LetGoOfEscape();
        Fire(game, hero, 0, false);
        if (In("R18") || at.X < 349 && at.Y < 152)
            return Gallery(game, story, hero);
        _galleryStep = 0;
        if (In("R19") || at.Y < 104 && at.X >= 348)
        {
            Go(game, hero, "the footbridge", new NVector2(440, 92), 2);
            return true;
        }
        if (In("R17") || at.X < 512 && at.Y > 130 && at.Y < 214)
            return Aisle(game, story, hero);
        // The Notgang: down to door P, waiting there until the breach has blown it.
        if (!story.Director.Has("breach") || world.DoorClosed('P'))
        {
            if (Go(game, hero, "door P", new NVector2(520, 168), 2))
                Look(game, Heading(at, new NVector2(504, 163)), -0.05f);
            return true;
        }
        Go(game, hero, "into the hold", new NVector2(500, 168), 2);
        return true;
    }

    /// <summary>The hold: along the aisle (y 168), before each lane a wait while it is hot; at the west landing the shutter.</summary>
    private bool Aisle(Game game, StoryRun story, Machine hero)
    {
        var world = game.World;
        var hazards = story.Hazards;
        var at = hero.Position;
        if (at.X < 384)
        {
            // The west landing: the Notabschaltung asked for, then through X (its panel hacked if it came down on the trio).
            if (ShutdownsWanted > 0 && story.Reserve.Restarts < ShutdownsWanted)
            {
                StandForShutdown(game, story, hero);
                return true;
            }
            if (hazards.Shutter == ShutterState.Shut && story.Hacks.Find("hack_x", world.Deck) is not null)
            {
                _escapeStep = Hack(game, story, hero, "hack_x");
                return true;
            }
            Go(game, hero, "through X", at.Y > 150 ? new NVector2(356, 146) : new NVector2(340, 141), 1.5f);
            return true;
        }
        var lanes = world.Deck!.Zones.Where(z => z.Name.StartsWith("lane_", StringComparison.Ordinal)).OrderByDescending(z => z.Min.X).ToList();
        foreach (var lane in lanes)
        {
            if (at.X <= lane.Min.X - 1)
                continue;
            var number = int.Parse(lane.Name["lane_".Length..], System.Globalization.CultureInfo.InvariantCulture);
            var beyond = new NVector2(lane.Min.X - 3.5f, 168);
            if (at.X > lane.Max.X + 1.5f && (Hot(hazards, number) || Hot(world, hero)))
            {
                // Before the lane: up to its edge, then waiting for it to cool (and the drive to have breath for the sprint).
                if (Go(game, hero, $"before lane {number}", new NVector2(lane.Max.X + 3.5f, 168), 1.2f))
                    Look(game, Heading(at, beyond), -0.05f);
                Overdue($"before lane {number}", 90);
                return true;
            }
            Go(game, hero, $"across lane {number}", beyond, 1.5f);
            return true;
        }
        Go(game, hero, "the west landing", new NVector2(376, 168), 2);
        return true;
    }

    /// <summary>Whether a lane is hot: its container (or the rack) telegraphing, or sliding.</summary>
    private static bool Hot(StoryHazards hazards, int lane) =>
        hazards.Rails.Any(r => r.Lane == lane && r.State is RailState.Telegraph or RailState.Slide);

    /// <summary>Whether the drive is too hot to sprint across a lane yet.</summary>
    private static bool Hot(GameWorld world, Machine hero) =>
        world.StorySprintFree && world.StoryDriveOf(hero.Id) is { } drive && drive.Heat > 0.6f;

    /// <summary>The gallery, step by step along the jets' free sides; before J4 until it stays dark for two seconds.</summary>
    private bool Gallery(Game game, StoryRun story, Machine hero)
    {
        var world = game.World;
        var at = hero.Position;
        // On from the nearest step not behind it (a restart at checkpoint 13 starts at its head).
        while (_galleryStep < GalleryWay.Length - 1 && GalleryWay[_galleryStep].Y > at.Y + 2)
            _galleryStep++;
        var step = GalleryWay[_galleryStep];
        if (_galleryStep == BeforeJ4 + 1 && !Dark(story.Hazards, 4, world.Tick, 40) && at.Y > 106)
        {
            if (Go(game, hero, "before J4", GalleryWay[BeforeJ4], 1.2f))
                Look(game, Heading(at, step), -0.05f);
            Overdue("before J4", 90);
            return true;
        }
        if (Go(game, hero, $"gallery {_galleryStep}", step, 1.2f))
        {
            if (_galleryStep < GalleryWay.Length - 1)
                _galleryStep++;
            else
                Go(game, hero, "the footbridge", new NVector2(360, 92), 2);
        }
        return true;
    }

    /// <summary>Whether a jet stays dark (neither warning nor burning) for the next <paramref name="ticks"/>.</summary>
    private static bool Dark(StoryHazards hazards, int jet, long tick, int ticks)
    {
        for (var t = tick; t <= tick + ticks; t += 2)
            if (hazards.Phase(jet, t) != StoryHazards.JetPhase.Off)
                return false;
        return true;
    }

    /// <summary>Stands in the west landing facing the pack until the Notabschaltung comes; after a while it brings it on itself.</summary>
    private void StandForShutdown(Game game, StoryRun story, Machine hero)
    {
        var world = game.World;
        if (!Go(game, hero, "waiting for the shutdown", new NVector2(378, 172), 2))
        {
            _shutdownWaitFrom = -1;
            return;
        }
        Look(game, Heading(hero.Position, new NVector2(470, 170)), -0.05f);
        var now = Godot.Time.GetTicksMsec() / 1000.0;
        if (_shutdownWaitFrom < 0)
        {
            _shutdownWaitFrom = now;
            Tell($"escape: standing for shutdown {story.Reserve.Restarts + 1} of {ShutdownsWanted} at T-{SecondsLeft(world, story):0}");
        }
        if (now - _shutdownWaitFrom < ShutdownPatience || game.Online || !story.Flags.Contains(StoryMarks.Reserve))
            return;
        _shutdownWaitFrom = -1;
        _fallbacks++;
        world.Damage(hero, 100000, story.Options.Ship);
        Tell($"escape: no shutdown came in {ShutdownPatience:0} s: shot the Locator down at tick {world.Tick} (fallback)");
    }

    private void LetGoOfEscape()
    {
        if (!_reviving)
            return;
        _reviving = false;
        Hold(KeyMap.Act.Use, false);
    }

    private static float SecondsLeft(GameWorld world, StoryRun story) =>
        story.Timers.TryGetValue(StoryMarks.Countdown, out var ends) ? MathF.Max(0, ends - world.Time) : 0;

    /// <summary>The beats of section 7 the driver prints as they fire (Prologue.Section7's, without the repeating ones).</summary>
    private static readonly HashSet<string> EscapeBeatNames =
    [
        "escape", "lift_bulge", "lift_burst", "heavies_out", "thresher_out", "slalom", "breach_warn", "breach", "hold_entry", "shut_x",
        "galerie", "j1", "t180", "valve_job", "thresher_fire", "bridge", "bridge_edge", "bridge_fall", "bucht", "pack_round", "pod1",
        "pod1_drop", "t120", "t90", "t60", "t30", "t10", "crash", "pod_denied", "blast_collapse", "at_y", "door_held", "y_press",
        "pod_hacked", "hatch", "y_gives", "pod2", "commander", "launch",
    ];

    // The countdown's seconds left as last seen (the launch ends the countdown in the same tick).
    private float _escapeLeft;

    /// <summary>Each beat of the section as it fires, with the countdown's seconds left; the launch with the run's total.</summary>
    private void EscapeBeats(GameWorld world, StoryRun story)
    {
        if (!story.Flags.Contains(StoryMarks.Escape) && !story.Director.Has("launch"))
            return;
        foreach (var (beat, firedAt) in story.Director.Fired.OrderBy(f => f.Value))
        {
            if (!EscapeBeatNames.Contains(beat) || !_escapeBeats.Add(beat))
                continue;
            var left = story.Timers.TryGetValue(StoryMarks.Countdown, out var ends) ? MathF.Max(0, ends - firedAt) : _escapeLeft;
            Tell(FormattableString.Invariant($"escape: {beat} at {firedAt:0.0} s, T-{left:0}"));
        }
        if (story.Timers.ContainsKey(StoryMarks.Countdown))
            _escapeLeft = SecondsLeft(world, story);
        if (!_escapeLaunchTold && story.Director.Fired.TryGetValue("launch", out var launched) && story.Director.Fired.TryGetValue("escape", out var began))
        {
            _escapeLaunchTold = true;
            Tell(FormattableString.Invariant($"escape: launch at T-{_escapeLeft:0}, {launched - began:0.0} s after the escape's start, restarts {story.Reserve.Restarts}, fallbacks {_fallbacks}"));
        }
    }
}
