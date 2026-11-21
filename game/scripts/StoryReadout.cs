using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>What a Grunt of the trio is about, for its card.</summary>
public enum GruntDoing
{
    Following,
    Waiting,
    Moving,
    /// <summary>Drawing the hunters off, loud on purpose.</summary>
    Distracting,
    /// <summary>A scrap heap until the Locator puts it together.</summary>
    Down,
    /// <summary>Still switched off in its clamp, before the Locator hacks it awake.</summary>
    Dormant,
    Idle,
}

/// <summary>One of the trio beside the player's own hero: its seat, its name, its health 0 to 1, what it is about, whether a person plays it, and how far off it is.</summary>
public readonly record struct GruntReadout(PlayerId Seat, string Name, float Health, GruntDoing Doing, bool Person, float? Distance);

/// <summary>A machine the motion tracker picks up: where it is, which way it lies from the hero (radians, 0 east, counter-clockwise as the sim's angles) and how far.</summary>
public readonly record struct TrackerContact(Vector2 Position, float Bearing, float Distance);

/// <summary>What the player can do where the hero stands.</summary>
public enum StoryPromptKind
{
    /// <summary>A door, a terminal, a clamp, the core, a pod: the hacking board opens.</summary>
    Hack,
    /// <summary>The weapon on its rack.</summary>
    Take,
    /// <summary>A hatch.</summary>
    Use,
    /// <summary>A gate's switch, held down.</summary>
    Hold,
    /// <summary>A Grunt down beside the hero, put together by holding the key.</summary>
    Revive,
}

/// <summary>A prompt under the sight: what can be done and to what (a marker of the deck, or a Grunt's name), how far off.</summary>
public readonly record struct StoryPrompt(StoryPromptKind Kind, string Target, float Distance);

/// <summary>
/// What the HUD of a story chapter reads off the world at one moment, as plain figures, free of Godot so that a test can hold
/// each of them against the simulation (as <see cref="WiredReadout"/> is for Wired): the chapter and its checkpoint, the
/// player's hero, the objective and the way there through the deck, the signature, the motion tracker's contacts, the alarm
/// and the countdown, the others of the trio, what can be done where the hero stands, and how the chapter ended. What the
/// director sets it reads through <see cref="StoryMarks"/>.
/// </summary>
public sealed record StoryReadout
{
    /// <summary>The Locator's name, which the story gives it: never the name of the person who plays it.</summary>
    public const string LocatorName = "L-7";

    /// <summary>How far the motion tracker reaches, in metres.</summary>
    public const float TrackerRange = 40;

    /// <summary>How near a marker the hero must stand for its prompt, in metres; a downed Grunt is put together from a little further.</summary>
    public const float PromptReach = 3, ReviveReach = 4;

    /// <summary>The Locator's own clamp, which it hacks itself out of first.</summary>
    public const string OwnClamp = "clamp_l7";

    public required string Chapter { get; init; }

    public required int Checkpoint { get; init; }

    public required int Section { get; init; }

    /// <summary>The player's hero's name: <see cref="LocatorName"/> for the Locator's seat, else the Grunt's.</summary>
    public required string HeroName { get; init; }

    /// <summary>Whether the player plays the Locator.</summary>
    public required bool IsLocator { get; init; }

    /// <summary>The hero's health and armour, 0 to 1; 0 while it is down or gone.</summary>
    public required float Health { get; init; }

    public required float Armour { get; init; }

    /// <summary>The objective's line (English: the HUD translates it); null while there is none.</summary>
    public required string? Objective { get; init; }

    /// <summary>Where the objective lies, and how far it is from the hero as the crow flies.</summary>
    public required Vector2? ObjectivePlace { get; init; }

    public required float? ObjectiveDistance { get; init; }

    /// <summary>The waypoints through the deck to the objective, the next first (<see cref="DeckRoute"/>); empty when it lies in sight or there is no way.</summary>
    public required IReadOnlyList<Vector2> Waypoints { get; init; }

    /// <summary>How far the way to the objective is through the deck, in metres; null without one.</summary>
    public required int? WayLength { get; init; }

    /// <summary>How loud and visible the hero is, 0 (still, dark) to 1 (firing).</summary>
    public required float Signature { get; init; }

    /// <summary>A hunter sees the trio.</summary>
    public required bool Spotted { get; init; }

    /// <summary>What moves within <see cref="TrackerRange"/> of the hero, of the ship's machines, nearest first.</summary>
    public required IReadOnlyList<TrackerContact> Contacts { get; init; }

    public required bool Alarm { get; init; }

    /// <summary>The seconds left on the countdown to the crash, and its share left, while it runs.</summary>
    public required float? CountdownLeft { get; init; }

    public required float? CountdownShare { get; init; }

    /// <summary>The others of the trio, in seat order.</summary>
    public required IReadOnlyList<GruntReadout> Grunts { get; init; }

    /// <summary>What the hero can do where it stands, nearest first.</summary>
    public required IReadOnlyList<StoryPrompt> Prompts { get; init; }

    /// <summary>The Locator's HUD boots in its clamp (<see cref="StoryMarks.Boot"/>).</summary>
    public required bool Boot { get; init; }

    /// <summary>How the chapter ended; null while it lasts.</summary>
    public required StoryEnding? Ending { get; init; }

    /// <summary>The figures for a player of a story; null without a story or for a player without a seat in it.</summary>
    public static StoryReadout? Of(GameWorld world, PlayerId player, DeckRoute? route = null)
    {
        if (world.Story is not { } run || !run.Seats.Contains(player))
            return null;
        var hero = world.HeroOf(player);
        var seat = run.Seats.IndexOf(player);
        var objective = StoryMarks.Objective(run);
        var place = StoryMarks.Locate(world.Deck, objective?.Place);
        var waypoints = hero is not null && place is { } goal && route is not null ? route.Chain(hero.Position, goal) : [];
        var countdown = run.Timers.TryGetValue(StoryMarks.Countdown, out var ends) ? MathF.Max(0, ends - world.Time) : (float?)null;
        var from = run.Timers.TryGetValue(StoryMarks.CountdownFrom, out var began) ? began : (float?)null;
        return new StoryReadout
        {
            Chapter = run.Options.Chapter,
            Checkpoint = run.Checkpoint,
            Section = run.Section,
            HeroName = NameOf(world, run, player),
            IsLocator = seat == 0,
            Health = hero is null ? 0 : Share(hero.HitPoints, hero.MaxHitPoints),
            Armour = hero is null ? 0 : Share(hero.Armour, hero.MaxArmour),
            Objective = objective?.Text,
            ObjectivePlace = place,
            ObjectiveDistance = hero is not null && place is { } at ? Vector2.Distance(hero.Position, at) : null,
            Waypoints = waypoints,
            WayLength = hero is not null && waypoints.Count > 0 ? route!.Steps(hero.Position) : null,
            Signature = hero is null ? 0 : SignatureOf(world, player, hero),
            Spotted = run.Flags.Contains(StoryMarks.Spotted),
            Contacts = hero is null ? [] : ContactsOf(world, run, hero),
            Alarm = run.Flags.Contains(StoryMarks.Alarm),
            CountdownLeft = countdown,
            CountdownShare = countdown is { } left && from is { } start && ends > start ? Math.Clamp(left / (ends - start), 0, 1) : null,
            Grunts = run.Seats.Where(s => s != player).Select(s => GruntOf(world, run, s, hero)).ToList(),
            Prompts = hero is null ? [] : PromptsOf(world, run, seat, hero),
            Boot = run.Flags.Contains(StoryMarks.Boot),
            Ending = run.Ending,
        };
    }

    /// <summary>
    /// A seat's name as the HUD shows it: the Locator is always <see cref="LocatorName"/>, a Grunt has the name its seat
    /// carries (the host's name for it while the computer plays it, a person's own while a person does).
    /// </summary>
    public static string NameOf(GameWorld world, StoryRun run, PlayerId seat) =>
        run.Seats.IndexOf(seat) is 0 ? LocatorName : world.Players.Contains(seat) ? world.Players[seat].Name : seat.ToString();

    private static GruntReadout GruntOf(GameWorld world, StoryRun run, PlayerId seat, Machine? from)
    {
        var grunt = world.HeroOf(seat);
        var doing = run.Flags.Contains(StoryMarks.Dormant(seat)) ? GruntDoing.Dormant
            : grunt is null || run.Flags.Contains(StoryMarks.Down(seat)) ? GruntDoing.Down
            : run.Flags.Contains(StoryMarks.Distracting(seat)) ? GruntDoing.Distracting
            : grunt.Order switch
            {
                FollowOrder => GruntDoing.Following,
                StandGroundOrder => GruntDoing.Waiting,
                MoveOrder or ReturnOrder or AttackMoveOrder => GruntDoing.Moving,
                _ => GruntDoing.Idle,
            };
        var person = world.Players.Contains(seat) && world.Players[seat].Kind == PlayerKind.Human;
        return new GruntReadout(seat, NameOf(world, run, seat), grunt is null || doing == GruntDoing.Down ? 0 : Share(grunt.HitPoints, grunt.MaxHitPoints), doing, person,
            grunt is not null && from is not null ? Vector2.Distance(grunt.Position, from.Position) : null);
    }

    /// <summary>
    /// How loud and visible a hero is: still and dark it is all but gone, moving it shows by its speed (half that, creeping),
    /// and just after a shot it is plain. A stand-in until the sim keeps a hero's signature of its own (plan WP-D), which
    /// adds the lamp, the scanner's pulse and the Grunts' wheels.
    /// </summary>
    public static float SignatureOf(GameWorld world, PlayerId player, Machine hero)
    {
        var level = 0.05f;
        if (world.DriveInput.TryGetValue(hero.Id, out var drive))
            level += 0.5f * MathF.Max(MathF.Abs(drive.Forward), MathF.Abs(drive.Strafe)) * (drive.Slow ? 0.5f : 1);
        else if (hero.Path.Count > hero.PathIndex)
            level += 0.5f;
        if (world.Wired?.Heroes.GetValueOrDefault(player) is { } state && world.Time - state.LastShotAt < 3)
            level = 1;
        return Math.Clamp(level, 0, 1);
    }

    /// <summary>The ship's machines moving within the tracker's reach, nearest first: the tracker hears movement, not what stands still.</summary>
    private static List<TrackerContact> ContactsOf(GameWorld world, StoryRun run, Machine hero) =>
        world.Machines
            .Where(m => !m.IsDead && m.Inside is null && m.Race != run.Options.Race && Moving(world, m))
            .Select(m => (Way: m.Position - hero.Position, Machine: m))
            .Where(c => c.Way.Length() <= TrackerRange)
            .OrderBy(c => c.Way.LengthSquared()).ThenBy(c => c.Machine.Id)
            .Select(c => new TrackerContact(c.Machine.Position, MathF.Atan2(c.Way.Y, c.Way.X), c.Way.Length()))
            .ToList();

    private static bool Moving(GameWorld world, Machine machine) =>
        machine.Path.Count > machine.PathIndex
        || world.DriveInput.TryGetValue(machine.Id, out var drive) && (drive.Forward != 0 || drive.Strafe != 0 || drive.Turn != 0);

    /// <summary>What the hero can do where it stands: markers of the deck still to be done within reach, and Grunts down beside it.</summary>
    private static List<StoryPrompt> PromptsOf(GameWorld world, StoryRun run, int seat, Machine hero)
    {
        var prompts = new List<StoryPrompt>();
        if (world.Deck is { } deck)
        {
            foreach (var marker in deck.Markers)
            {
                var distance = Vector2.Distance(marker.Position, hero.Position);
                if (distance > PromptReach || run.Flags.Contains(StoryMarks.Done(marker.Name)) || KindOf(marker, seat) is not { } kind)
                    continue;
                prompts.Add(new StoryPrompt(kind, marker.Name, distance));
            }
        }
        // Only the Locator puts a Grunt together.
        if (seat == 0)
        {
            foreach (var other in run.Seats.Skip(1))
            {
                if (!run.Flags.Contains(StoryMarks.Down(other)) || world.Find<Machine>(world.Wired?.Heroes.GetValueOrDefault(other)?.Hero ?? -1) is not { } wreck)
                    continue;
                var distance = Vector2.Distance(wreck.Position, hero.Position);
                if (distance <= ReviveReach)
                    prompts.Add(new StoryPrompt(StoryPromptKind.Revive, NameOf(world, run, other), distance));
            }
        }
        return prompts.OrderBy(p => p.Distance).ThenBy(p => p.Target, StringComparer.Ordinal).ToList();
    }

    /// <summary>What a marker offers to a seat: hacking is the Locator's, holding a switch anyone's.</summary>
    private static StoryPromptKind? KindOf(DeckMarker marker, int seat)
    {
        bool Tagged(string tag) => marker.Tags.Contains(tag);
        if (seat == 0 && (Tagged("hack") || Tagged("terminal") || marker.Name == OwnClamp))
            return StoryPromptKind.Hack;
        if (seat == 0 && Tagged("pickup"))
            return StoryPromptKind.Take;
        if (seat == 0 && Tagged("hatch"))
            return StoryPromptKind.Use;
        return Tagged("switch") ? StoryPromptKind.Hold : null;
    }

    private static float Share(float part, float whole) => whole <= 0 ? 0 : Math.Clamp(part / whole, 0, 1);
}
