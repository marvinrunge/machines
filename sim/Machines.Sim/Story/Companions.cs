using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Sim.Story;

/// <summary>What the Locator told a Grunt (docs/STORY.md, Koop: "Hier warten", "Folgen", "Ablenken").</summary>
public enum CompanionOrder
{
    /// <summary>Keep with the Locator.</summary>
    Follow,
    /// <summary>Stand where it is until told otherwise.</summary>
    Wait,
    /// <summary>Drive to a point, make a noise there, then go back to where it was told and wait.</summary>
    Distract,
}

/// <summary>A job the story gives a Grunt (its director, or the Locator at the pod's hatch), over the Locator's last order while it lasts.</summary>
public enum CompanionTask
{
    None,
    /// <summary>Carry crates from the pile into the slots before a door, then stand its ground in front of them.</summary>
    Barricade,
    /// <summary>Stand on a gate's switch, which holds the gate shut while anyone of the trio is on it.</summary>
    HoldSwitch,
    /// <summary>Follow the Locator into the escape pod: go to a place inside the pod zone and stay there.</summary>
    Board,
    /// <summary>Drive to a place (a marker, a zone or "x,y") by its own way and wait there: the Grunts down the cable duct while the Locator crawls ahead.</summary>
    Go,
    /// <summary>Stage 7, section 2: sneak to a place by the crossing rules (package S).</summary>
    SneakGo,
    /// <summary>Stage 7, section 2: bang a valve or noise source to draw the Thresher off (package S).</summary>
    Lure,
    /// <summary>Stage 7, the armoury: hold a defence post and fire at what comes (package W).</summary>
    Defend,
    /// <summary>Stage 7: plug a cable into the shut-down Locator and hold it (the Notabschaltung, CompanionBot.Restart).</summary>
    Restart,
    /// <summary>Stage 7, the escape: stand in a frame and hold it (package E).</summary>
    HoldFrame,
    /// <summary>Stage 7, the escape: stand on a valve (package E).</summary>
    Valve,
    /// <summary>Stage 7, the escape: push a chock into a lane (package E).</summary>
    Chock,
    /// <summary>Stage 7, the escape: shoot the crane's lock (package E).</summary>
    Crane,
}

/// <summary>
/// What Use acts on, by priority (plan stufe7 C9): the nearest of the highest kind first. A downed Grunt (the Locator puts it
/// together) › the shut-down Locator (a Grunt plugs its cable in) › a hurt Grunt (mend) › an unarmed Grunt with a Bolter
/// carried (mount) › a wreck (strip) › a valve, noise source, hand wheel or lever › a switch, a rack, a hatch.
/// </summary>
public enum StoryUseKind
{
    Revive,
    Plug,
    Mend,
    Mount,
    Strip,
    Device,
    Switch,
}

/// <summary>Where a Grunt is in a distraction or a barricade (the steps of the job it is on).</summary>
public enum CompanionStep
{
    None,
    /// <summary>Distraction: on the way to the point.</summary>
    Going,
    /// <summary>Distraction: making its noise at the point.</summary>
    Honking,
    /// <summary>Distraction: back to where it was told.</summary>
    Fleeing,
    /// <summary>Barricade: on the way to a crate of the pile.</summary>
    ToPile,
    /// <summary>Barricade: carrying a crate to its slot.</summary>
    ToSlot,
    /// <summary>Barricade: the work done, on the way to stand before the crates.</summary>
    ToStand,
    /// <summary>Any job: at its place, standing.</summary>
    Standing,
    /// <summary>A person's Grunt pushing a crate it drove into (GameWorld.PushCrates).</summary>
    Pushing,
}

/// <summary>A line a Grunt says over the radio (docs/STORY.md, Koop: "kurze Funksprüche, wenn die KI sie steuert"): its id in the line table, who says it, on which tick.</summary>
public sealed record StoryBark(string Line, string Speaker, int Tick, PlayerId Seat);

/// <summary>A noise the trio made on purpose (a Grunt's honk while it distracts): where, how loud (1 is a Grunt's honk), on which tick, by which machine.</summary>
public sealed record StoryNoise(Vector2 Position, float Loudness, int Tick, int Source);

/// <summary>A crate carried by a Grunt: the artefact as it stood in the pile, to be set down again in a slot.</summary>
public sealed record CarriedCrate(int Placement, ArtefactType Type, int SubType, int? Tag, IReadOnlyList<Vector2> Shape, Vector2 From);

/// <summary>One Grunt of the trio as the story keeps it: what it was told, the job it is on and how far, and its clocks.</summary>
public sealed class Companion(PlayerId seat)
{
    public PlayerId Seat { get; } = seat;

    /// <summary>The Locator's last order, which the computer carries out while it plays the seat (and goes back to after a job).</summary>
    public CompanionOrder Order { get; set; } = CompanionOrder.Follow;

    /// <summary>The distraction's point.</summary>
    public Vector2 Point { get; set; }

    /// <summary>Where a distraction goes back to, and where a wait stands: the Grunt's place when it was told.</summary>
    public Vector2 Back { get; set; }

    public CompanionTask Task { get; set; }

    /// <summary>The job's place: a door's letter for a barricade, a switch's or the pod's marker.</summary>
    public string? Place { get; set; }

    public CompanionStep Step { get; set; }

    /// <summary>The marker of the slot it carries a crate to, or stands before once the slots are full.</summary>
    public string? Slot { get; set; }

    /// <summary>The crate of the pile it goes to fetch (an artefact's id), if any.</summary>
    public int? Crate { get; set; }

    /// <summary>The crate it carries, if any.</summary>
    public CarriedCrate? Carrying { get; set; }

    /// <summary>The tick its current step gives up or ends (a honk's end, a trip that takes too long).</summary>
    public int StepUntil { get; set; }

    /// <summary>The tick it may ram again.</summary>
    public int RamReadyAt { get; set; }

    /// <summary>Ticks the Locator has held it together for (revive), and ticks it has lain in the repair station.</summary>
    public int ReviveTicks { get; set; }

    public int RepairTicks { get; set; }

    /// <summary>The tick of its last bark, for the cooldown.</summary>
    public int LastBark { get; set; } = int.MinValue / 2;

    /// <summary>Lines said once at most (by id), and the tick of the last "I don't fit in there".</summary>
    public SortedSet<string> Said { get; } = new(StringComparer.Ordinal);

    internal void MixInto(WiredHash hash)
    {
        hash.Mix(Seat.Value);
        hash.Mix((int)Order);
        hash.Mix(BitConverter.SingleToInt32Bits(Point.X));
        hash.Mix(BitConverter.SingleToInt32Bits(Point.Y));
        hash.Mix(BitConverter.SingleToInt32Bits(Back.X));
        hash.Mix(BitConverter.SingleToInt32Bits(Back.Y));
        hash.Mix((int)Task);
        hash.Mix(Place ?? "");
        hash.Mix((int)Step);
        hash.Mix(Slot ?? "");
        hash.Mix(Crate ?? -1);
        hash.Mix(Carrying?.Placement ?? -1);
        hash.Mix(StepUntil);
        hash.Mix(RamReadyAt);
        hash.Mix(ReviveTicks);
        hash.Mix(RepairTicks);
        hash.Mix(LastBark);
        foreach (var line in Said)
            hash.Mix(line);
    }
}

/// <summary>
/// The companions of a story chapter (plan WP-F): each Grunt's record, who stands stunned by a ram, the noises the trio made
/// and the Grunts' radio barks. Whether a Grunt lies downed, hangs dormant or draws the hunters off is in the run's flags
/// (<see cref="StoryMarks"/>), which the HUD reads. All of it changes on ticks and goes into the checksum.
/// </summary>
public sealed class StoryCrew
{
    /// <summary>How many barks and noises are kept, the newest last; the director turns the barks of each tick into lines.</summary>
    public const int Kept = 32;

    /// <summary>The Grunts by seat id.</summary>
    public SortedDictionary<int, Companion> Grunts { get; } = [];

    /// <summary>Machines a ram knocked over, by id: the tick they may fire again.</summary>
    public SortedDictionary<int, int> Stunned { get; } = [];

    /// <summary>Who of the trio holds Use down, by player id, and on what: "seat:&lt;id&gt;" for a Grunt being put together, else a marker.</summary>
    public SortedDictionary<int, string> Holding { get; } = [];

    /// <summary>Grunts' radio lines, the newest last (up to <see cref="Kept"/>).</summary>
    public List<StoryBark> Barks { get; } = [];

    /// <summary>Noises the trio made on purpose, the newest last (up to <see cref="Kept"/>).</summary>
    public List<StoryNoise> Noises { get; } = [];

    /// <summary>The record of a Grunt's seat, made the first time it is asked for.</summary>
    public Companion Of(PlayerId seat)
    {
        if (!Grunts.TryGetValue(seat.Value, out var companion))
            Grunts[seat.Value] = companion = new Companion(seat);
        return companion;
    }

    internal void Bark(StoryBark bark)
    {
        Barks.Add(bark);
        if (Barks.Count > Kept)
            Barks.RemoveAt(0);
    }

    internal void Noise(StoryNoise noise)
    {
        Noises.Add(noise);
        if (Noises.Count > Kept)
            Noises.RemoveAt(0);
    }

    public long Fingerprint()
    {
        var hash = new WiredHash();
        foreach (var companion in Grunts.Values)
            companion.MixInto(hash);
        foreach (var (id, until) in Stunned)
        {
            hash.Mix(id);
            hash.Mix(until);
        }
        foreach (var (player, what) in Holding)
        {
            hash.Mix(player);
            hash.Mix(what);
        }
        foreach (var bark in Barks)
        {
            hash.Mix(bark.Line);
            hash.Mix(bark.Tick);
        }
        foreach (var noise in Noises)
        {
            hash.Mix(BitConverter.SingleToInt32Bits(noise.Position.X));
            hash.Mix(BitConverter.SingleToInt32Bits(noise.Position.Y));
            hash.Mix(noise.Tick);
        }
        return hash.Value;
    }
}

/// <summary>
/// The Grunts' radio barks (docs/STORY.md, Die beiden Grunts; plan 5.1): which line each says on what, with the English the
/// subtitles show (the line table tools/voice/lines.json has the German and the voices). Ike is the loud one, Bob speaks in
/// status reports. The lines never say a Grunt's name, which the host may change.
/// </summary>
public static class CompanionBarks
{
    /// <summary>What a Grunt barks about.</summary>
    public enum Cue
    {
        Follow,
        Wait,
        Distract,
        Barricade,
        Barricaded,
        Gate,
        Ram,
        Down,
        Up,
        Pod,
        Vent,
        Wake,
    }

    /// <summary>The line of a cue for a seat (1 Ike, 2 Bob), or null where that Grunt says nothing to it.</summary>
    public static string? Line(Cue cue, int seat) => (cue, seat) switch
    {
        (Cue.Follow, 1) => "P_IKE_FOLLOW",
        (Cue.Follow, 2) => "P_BOB_FOLLOW",
        (Cue.Wait, 1) => "P_IKE_WAIT",
        (Cue.Wait, 2) => "P_BOB_HOLD",
        (Cue.Distract, 1) => "P_IKE_DISTRACT",
        (Cue.Distract, 2) => "P_BOB_DISTRACT",
        (Cue.Barricade, 1) => "P_IKE_BARRICADE",
        (Cue.Barricade, 2) => "P_BOB_CRATES",
        (Cue.Barricaded, 1) => "P_IKE_BARRICADED",
        (Cue.Barricaded, 2) => "P_BOB_BARRICADE",
        (Cue.Gate, 1) => "P_IKE_GATE",
        (Cue.Gate, 2) => "P_BOB_GATE",
        (Cue.Ram, 1) => "P_IKE_RAM",
        (Cue.Ram, 2) => "P_BOB_RAM",
        (Cue.Down, 1) => "P_IKE_DOWN",
        (Cue.Down, 2) => "P_BOB_DOWN",
        (Cue.Up, 1) => "P_IKE_UP",
        (Cue.Up, 2) => "P_BOB_UP",
        (Cue.Pod, 1) => "P_IKE_POD",
        (Cue.Pod, 2) => "P_BOB_POD",
        (Cue.Vent, 1) => "P_IKE_VENT",
        (Cue.Wake, 1) => "P_IKE_WAKE",
        (Cue.Wake, 2) => "P_BOB_WAKE",
        _ => null,
    };

    /// <summary>Who speaks a seat's lines in the line table.</summary>
    public static string Speaker(int seat) => seat == 1 ? "ike" : "bob";

    /// <summary>The English of each bark line (the msgid; de.po has the German), for the director's subtitles.</summary>
    public static readonly IReadOnlyDictionary<string, string> English = new SortedDictionary<string, string>(StringComparer.Ordinal)
    {
        ["P_IKE_FOLLOW"] = "Right behind you.",
        ["P_BOB_FOLLOW"] = "Following.",
        ["P_IKE_WAIT"] = "Wait. Great. I'm amazing at waiting.",
        ["P_BOB_HOLD"] = "Holding position.",
        ["P_IKE_DISTRACT"] = "Hey! Over here, you tin spider!",
        ["P_BOB_DISTRACT"] = "Drawing them off.",
        ["P_IKE_BARRICADE"] = "Crates against the door. Then me against the crates.",
        ["P_BOB_CRATES"] = "Moving crates.",
        ["P_IKE_BARRICADED"] = "Crates are in. Nobody gets past me.",
        ["P_BOB_BARRICADE"] = "Door secured.",
        ["P_IKE_GATE"] = "I've got the gate. Go, go!",
        ["P_BOB_GATE"] = "Gate held. Hurry.",
        ["P_IKE_RAM"] = "Out of the way!",
        ["P_BOB_RAM"] = "Ramming.",
        ["P_IKE_DOWN"] = "I'm … just … gonna … lie here …",
        ["P_BOB_DOWN"] = "Damage critical. Shutting d-",
        ["P_IKE_UP"] = "Back. Did I miss anything?",
        ["P_BOB_UP"] = "Systems online. Thanks.",
        ["P_IKE_POD"] = "In, in, in!",
        ["P_BOB_POD"] = "Aboard.",
        ["P_IKE_VENT"] = "I don't fit in there. We'll take the long way. Open up for us!",
        ["P_IKE_WAKE"] = "Ike? Ike. I like it. Who are we knocking over?",
        ["P_BOB_WAKE"] = "Confirmed. Designation: Bob.",
    };
}
