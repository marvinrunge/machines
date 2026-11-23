using System.Numerics;
using Machines.Sim.Survival;

namespace Machines.Sim.Story;

/// <summary>Where a sliding container or the rack is in its slide (StoryHazards.Rail).</summary>
public enum RailState
{
    /// <summary>At rest at an end of its rail (or where a chock or the lever holds it).</summary>
    Rest,

    /// <summary>Its chains snapped: it is about to slide (the telegraph: the groan, the light strip running its way).</summary>
    Telegraph,

    /// <summary>Sliding along its rail.</summary>
    Slide,
}

/// <summary>
/// One container (or the rack) on its rail across the hold (design-escape §3 C2, §5.3): a lane north to south, its centre in
/// millimetres along the rail, its speed in millimetres a tick. All in whole numbers, so two peers agree to the millimetre.
/// </summary>
public sealed class StoryRail(string name, int lane, int tag, int x0, int x1, int halfLength, int centre)
{
    /// <summary>The prop it was on the deck (container_l1, rack_l4 ...).</summary>
    public string Name { get; } = name;

    /// <summary>Its lane, 1 to 4 (L1 the east one).</summary>
    public int Lane { get; } = lane;

    /// <summary>The deck prop's artefact tag, taken off the map when the hold's rails start (the view draws the rail instead).</summary>
    public int Tag { get; } = tag;

    /// <summary>Its sides across the lane, millimetres (x).</summary>
    public int X0 { get; } = x0;

    public int X1 { get; } = x1;

    /// <summary>Half its length along the rail, millimetres.</summary>
    public int HalfLength { get; } = halfLength;

    /// <summary>Its centre along the rail (y), millimetres.</summary>
    public int Centre { get; set; } = centre;

    /// <summary>Its speed along the rail, millimetres a tick (signed: + north).</summary>
    public int Speed { get; set; }

    /// <summary>Which way it goes this slide: +1 north, -1 south, 0 none.</summary>
    public int Way { get; set; }

    public RailState State { get; set; }

    /// <summary>The tick its slide starts (after its telegraph).</summary>
    public long SlideAt { get; set; }

    /// <summary>The machines it already struck this slide (each once a slide).</summary>
    public SortedSet<int> Struck { get; } = [];

    public bool IsRack => Name.StartsWith("rack", StringComparison.Ordinal);

    /// <summary>Its middle, metres.</summary>
    public Vector2 Middle => new((X0 + X1) / 2000f, Centre / 1000f);

    /// <summary>Its half width across the lane, metres.</summary>
    public float HalfWidth => (X1 - X0) / 2000f;

    internal void Mix(WiredHash hash)
    {
        hash.Mix(Name);
        hash.Mix(Centre);
        hash.Mix(Speed);
        hash.Mix(Way);
        hash.Mix((int)State);
        hash.Mix(SlideAt);
        foreach (var id in Struck)
            hash.Mix(id);
    }
}

/// <summary>The shutter X's state (design-escape §3 C3): open, lowering, held in its frame, shut, broken.</summary>
public enum ShutterState
{
    Open,
    Closing,
    Shut,
    Broken,
}

/// <summary>
/// The escape's hazards (package E, design-escape §5): the hold's rolls and the containers on their rails, the rack, the
/// chocks and the lane lever; the shutter X; the gallery's jets with the valve and the hand wheel; the plates that fall on
/// the pack; the Notgang's collapse and its fire face; the footbridge's drift and fall; the crane's amber lock. Every number
/// is in ticks or millimetres. Everything changes on ticks and goes into <see cref="Fingerprint"/> once it keeps anything,
/// so a run that never reaches the escape keeps its checksum.
/// </summary>
public sealed class StoryHazards
{
    // ---- numbers (design-escape §3, §5) -------------------------------------------------------------------

    /// <summary>A roll's cycle, ticks (10 s): telegraph, then each lane's container slides to the end its way goes.</summary>
    public const int Cycle = 200;

    /// <summary>The delay of each lane's slide after the telegraph, ticks: L1 first, L4 last, so one lane is hot while another is clear.</summary>
    public const int LaneStagger = 20;

    /// <summary>A container's top speed and its gain a tick, millimetres a tick (8 m/s, 4 m/s²); the rack's (10 m/s).</summary>
    public const int TopSpeed = 400, Gain = 10, RackSpeed = 500;

    /// <summary>A container stops this far short of a hero (never pins one), millimetres; it harms one it brushes from this speed on.</summary>
    public const int PinMargin = 1500, HarmSpeed = 100;

    /// <summary>The share a container (or the rack) takes from a hero it brushes; a falling plate; a jet a half second; the Notgang's fire face a half second.</summary>
    public const float BrushShare = 0.15f, JetShare = 0.10f, FireShare = 0.10f;

    /// <summary>A jet's cycle (J2–J5): flicker, fire, off, ticks; and the offset between neighbouring jets.</summary>
    public const int JetFlicker = 20, JetFire = 40, JetOff = 50, JetOffset = 36;

    /// <summary>J1's show at the gallery's entry: the warning (hiss, glow, the gauge's sparks), then the burn, ticks.</summary>
    public const int J1Warning = 50, J1Burn = 80;

    /// <summary>J4 stays off this long after the valve's holder steps off; the hand wheel turns it off this long, ticks.</summary>
    public const int ValveAfter = 80, WheelOff = 240;

    /// <summary>The lever locks lanes 3 and 4 this long, ticks; a chock stops its lane for this many cycles.</summary>
    public const int LeverLock = 160, ChockCycles = 2;

    /// <summary>How long the Use of the hand wheel and the lever are held, ticks (C9: wheel 2 s, lever 1 s).</summary>
    public const int WheelHold = 40, LeverHold = 20;

    /// <summary>The shutter lowers in this many ticks (10 s), slams in this many once the trio is through (2 s); it stops at half while held.</summary>
    public const int ShutterLower = 200, ShutterSlam = 40;

    /// <summary>The Thresher rams the shut shutter this long (three rams) before it bursts; without him it gives after this long, ticks.</summary>
    public const int ShutterRams = 60, ShutterGives = 500;

    /// <summary>A falling plate's warning before it lands, ticks (1.5 s).</summary>
    public const int PlateWarning = 30;

    /// <summary>The bridge's creak before it falls, ticks (2.5 s); the collapse's creak (2 s).</summary>
    public const int BridgeCreak = 50, CollapseCreak = 40;

    /// <summary>The drift toward the north railing on the bridge, metres a second: the Locator, a Grunt, the pack.</summary>
    public const float DriftLocator = 0.4f, DriftGrunt = 0.8f, DriftPack = 1.0f;

    // ---- state --------------------------------------------------------------------------------------------

    /// <summary>The hold's rails, in the deck's order (L1 east first); empty until the escape starts them.</summary>
    public List<StoryRail> Rails { get; } = [];

    /// <summary>The tick the rolls started (-1: none); the rolls come every <see cref="Cycle"/> from it.</summary>
    public long RollsFrom { get; set; } = -1;

    /// <summary>The roll under way: its number (0 the first, only L1 moves) and which way it goes (+1 north, -1 south).</summary>
    public int Roll { get; set; } = -1;

    /// <summary>Lanes chocked, with the cycles they stay stopped.</summary>
    public SortedDictionary<int, int> Chocked { get; } = [];

    /// <summary>Until which tick the lever locks lanes 3 and 4.</summary>
    public long LeverUntil { get; set; } = -1;

    /// <summary>Whether the rack was let go (it ploughs down L4 once, the crush gag).</summary>
    public bool RackLoose { get; set; }

    /// <summary>The tick the jets started (-1: none); their timetable counts from it.</summary>
    public long JetsFrom { get; set; } = -1;

    /// <summary>The tick J1's show started (-1: none).</summary>
    public long J1From { get; set; } = -1;

    /// <summary>J4 off until this tick (the valve's holder, the hand wheel).</summary>
    public long J4OffUntil { get; set; } = -1;

    /// <summary>The shutter X: its state, how far down it is (0 open, 1 shut, in 1/<see cref="ShutterLower"/> steps), and the ticks the Thresher rammed it / it stood shut.</summary>
    public ShutterState Shutter { get; set; }

    public int ShutterDown { get; set; }

    public int ShutterRammed { get; set; }

    public int ShutterShutTicks { get; set; }

    /// <summary>The Notgang's collapse: whether its cells are wall now, and whether the fire face burns.</summary>
    public bool Collapsed { get; set; }

    public bool FireFace { get; set; }

    /// <summary>Whether the bridge drifts the ones on it toward the north railing, and whether it has fallen.</summary>
    public bool Drift { get; set; }

    public bool BridgeFallen { get; set; }

    /// <summary>The tick the bridge's creak started (-1: none).</summary>
    public long BridgeFallAt { get; set; } = -1;

    /// <summary>Plates on their way down onto a pack member: the tick they land and whom they follow.</summary>
    public SortedDictionary<int, (long At, int Target)> Plates { get; } = [];

    /// <summary>The plates let fall so far (their numbers).</summary>
    public int PlatesFallen { get; set; }

    /// <summary>Machines staggered (stopped where they stand) until a tick: by a container, a plate, the fire.</summary>
    public SortedDictionary<int, long> Staggered { get; } = [];

    /// <summary>How long each seat has held its Use on a device of this section (the wheel, the lever), ticks.</summary>
    public SortedDictionary<int, int> UseTicks { get; } = [];

    /// <summary>Whether the crane's beam fell (its amber lock shot).</summary>
    public bool CraneDown { get; set; }

    /// <summary>The hazards' last half-second harm tick, so harm a half second lands on the same ticks on every peer.</summary>
    public const int HarmEvery = 10;

    /// <summary>Whether it keeps anything (the run's checksum mixes it in only then).</summary>
    public bool Any => Rails.Count > 0 || JetsFrom >= 0 || Shutter != ShutterState.Open || Collapsed || Drift || BridgeFallen
        || Plates.Count > 0 || PlatesFallen > 0 || Staggered.Count > 0 || CraneDown || J1From >= 0;

    /// <summary>A fingerprint of what this part keeps, for the run's checksum.</summary>
    public long Fingerprint()
    {
        var hash = new WiredHash();
        if (!Any)
            return hash.Value;
        foreach (var rail in Rails)
            rail.Mix(hash);
        hash.Mix(RollsFrom);
        hash.Mix(Roll);
        foreach (var (lane, cycles) in Chocked)
        {
            hash.Mix(lane);
            hash.Mix(cycles);
        }
        hash.Mix(LeverUntil);
        hash.Mix(RackLoose ? 1 : 0);
        hash.Mix(JetsFrom);
        hash.Mix(J1From);
        hash.Mix(J4OffUntil);
        hash.Mix((int)Shutter);
        hash.Mix(ShutterDown);
        hash.Mix(ShutterRammed);
        hash.Mix(ShutterShutTicks);
        hash.Mix((Collapsed ? 1 : 0) | (FireFace ? 2 : 0) | (Drift ? 4 : 0) | (BridgeFallen ? 8 : 0) | (CraneDown ? 16 : 0));
        hash.Mix(BridgeFallAt);
        foreach (var (number, (at, target)) in Plates)
        {
            hash.Mix(number);
            hash.Mix(at);
            hash.Mix(target);
        }
        hash.Mix(PlatesFallen);
        foreach (var (id, until) in Staggered)
        {
            hash.Mix(id);
            hash.Mix(until);
        }
        foreach (var (seat, ticks) in UseTicks)
        {
            hash.Mix(seat);
            hash.Mix(ticks);
        }
        return hash.Value;
    }

    /// <summary>
    /// Whether a jet burns at a tick (J1 to J5, 1-based): J4 not while the valve or the wheel keeps it off, J1 through its show
    /// and then on its cycle. A pure function of the ticks, which the Grunts' bots and the driver read ahead too.
    /// </summary>
    public bool Burns(int jet, long tick) => Phase(jet, tick) == JetPhase.Fire;

    /// <summary>Whether a jet warns (flickers) at a tick: the moment to wait.</summary>
    public bool Warns(int jet, long tick) => Phase(jet, tick) == JetPhase.Flicker;

    public enum JetPhase
    {
        Off,
        Flicker,
        Fire,
    }

    /// <summary>The phase of a jet at a tick.</summary>
    public JetPhase Phase(int jet, long tick)
    {
        if (JetsFrom < 0 || tick < JetsFrom)
            return JetPhase.Off;
        if (jet == 4 && tick < J4OffUntil)
            return JetPhase.Off;
        if (jet == 1)
        {
            if (J1From < 0 || tick < J1From)
                return JetPhase.Off;
            var show = tick - J1From;
            if (show < J1Warning)
                return JetPhase.Flicker;
            if (show < J1Warning + J1Burn)
                return JetPhase.Fire;
        }
        var period = JetFlicker + JetFire + JetOff;
        var t = (tick - JetsFrom - (jet - 1) * JetOffset) % period;
        if (t < 0)
            t += period;
        return t < JetFlicker ? JetPhase.Flicker : t < JetFlicker + JetFire ? JetPhase.Fire : JetPhase.Off;
    }
}
