using System.Globalization;
using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Story;

namespace Machines.Sim.World;

/// <summary>
/// The escape's hazards on the world's ticks (package E, design-escape §3 and §5; their state is <see cref="StoryHazards"/>):
/// the hold's rolls with the containers sliding on their rails and the rack's plough (moving blockers that never pin a hero),
/// the chocks and the lane lever; the shutter X lowering by itself, held in its frame, slammed after the trio and burst by
/// the Thresher; the gallery's jets on their timetable, the valve and the hand wheel; plates let fall on the pack; the
/// Notgang's collapse with its fire face, and its reopening; the footbridge's drift and its fall; the crane's beam. What
/// they take from the trio passes the budget (<see cref="StoryBudget"/>), what they take from the pack is its share
/// (design-escape §4). Run once a tick by the section's beat <c>escape_tick</c> while the flag <see cref="StoryMarks.Escape"/> is set.
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>The hold's floor between its bulkheads along the rails, millimetres (y 134 to 210).</summary>
    private const int RailSouth = 134_000, RailNorth = 210_000;

    /// <summary>The lanes' rails as the deck lays them out: the prop, its lane, its tag, its width and half length (metres).</summary>
    private static readonly (string Name, int Lane, float Width, float Half)[] RailProps =
    [
        ("container_l1", 1, 8, 6), ("container_l2a", 2, 8, 6), ("container_l2b", 2, 8, 6), ("container_l3", 3, 8, 6),
        ("container_l4", 4, 8, 6), ("rack_l4", 4, 10, 8),
    ];

    /// <summary>The escape's whole tick: the budget's clock, the staggered, the rails, the jets, the shutter, the fire, the plates, the bridge, the crane, the devices, the pack.</summary>
    internal void UpdateEscape(StoryRun run)
    {
        var budget = run.Budget;
        budget.Active = run.Flags.Contains(StoryMarks.Escape);
        budget.Used = true;
        budget.Now = Tick;
        budget.Share = StoryBudget.WindowShare(run.Options.Difficulty);
        var hazards = run.Hazards;
        foreach (var (id, until) in hazards.Staggered.ToList())
        {
            if (Tick >= until || Find<Machine>(id) is not { IsDead: false })
                hazards.Staggered.Remove(id);
            else
                HoldForStory(id);
        }
        UpdateRails(run);
        UpdateJets(run);
        UpdateShutter(run);
        UpdateFireFace(run);
        UpdatePlates(run);
        UpdateBridge(run);
        UpdateCrane(run);
        UpdateEscapeDevices(run);
        UpdatePack(run);
    }

    /// <summary>
    /// A hazard's hit: so much of the target's hit points (armour ignored), through the harm seam (<see cref="StoryPack.Pending"/>),
    /// on the trio through the budget as <paramref name="kind"/>.
    /// </summary>
    internal void HazardHit(StoryRun run, Actor target, float share, StoryHarmKind kind = StoryHarmKind.Hazard)
    {
        if (target.IsDead || share <= 0)
            return;
        run.Pack.Pending = share;
        run.Budget.Kind = kind;
        try
        {
            Damage(target, 1, null);
        }
        finally
        {
            run.Pack.Pending = null;
            run.Budget.Kind = StoryHarmKind.Aimed;
        }
    }

    /// <summary>Stops a machine where it stands for so many ticks (a container's knock, a plate, the fire's flailing).</summary>
    internal void Stagger(StoryRun run, Machine machine, int ticks)
    {
        if (machine.IsDead || ticks <= 0)
            return;
        var until = Tick + ticks;
        if (run.Hazards.Staggered.GetValueOrDefault(machine.Id) < until)
            run.Hazards.Staggered[machine.Id] = until;
        HoldForStory(machine.Id);
    }

    /// <summary>The trio's heroes standing on the deck (not in the pod, not dormant), the Locator first.</summary>
    internal IEnumerable<Machine> EscapeHeroes(StoryRun run) =>
        StoryTrigger.Heroes(this, run, StoryWho.Everyone).Where(h => h.Inside is null);

    /// <summary>Whether a machine stands (with its body) in a zone of the deck.</summary>
    internal static bool Overlaps(DeckZone zone, Vector2 p, float radius) =>
        p.X + radius * 0.5f >= zone.Min.X && p.X - radius * 0.5f < zone.Max.X && p.Y + radius * 0.5f >= zone.Min.Y && p.Y - radius * 0.5f < zone.Max.Y;

    /// <summary>The live machines in a zone of the deck, in id order.</summary>
    private List<Machine> InZone(string zone)
    {
        if (Deck?.Zone(zone) is not { } z)
            return [];
        return Machines.Where(m => !m.IsDead && m.Inside is null && Overlaps(z, m.Position, m.Radius)).OrderBy(m => m.Id).ToList();
    }

    /// <summary>What a hazard does to a member of the pack, by its kind: the share it takes and how long it is staggered.</summary>
    private void PackHazard(StoryRun run, Machine member, (float Share, int Ticks) reaper2, (float Share, int Ticks) reaper3,
        (float Share, int Ticks) heavy, int thresher, string what)
    {
        if (!run.Pack.Members.TryGetValue(member.Id, out var kind))
            return;
        var (share, ticks) = kind switch
        {
            PackKind.Reaper2 => reaper2,
            PackKind.Reaper3 => reaper3,
            PackKind.Heavy => heavy,
            _ => (0f, thresher),
        };
        HazardHit(run, member, share);
        if (!member.IsDead)
            Stagger(run, member, ticks);
        StoryEvents.Cue(this, FormattableString.Invariant($"pack {what} {member.Id} {(member.IsDead ? "destroyed" : "staggered")}"), member.Position);
    }

    // ---- the rails --------------------------------------------------------------------------------------

    /// <summary>
    /// The leaves of the deck doors that stand open from the start and the escape shuts (X, the hold's shutter, and Y, the
    /// Notgang's door into the bay): the scenario has no artefacts for an open door, so they are hung here, open (in the
    /// walls, <see cref="OpenDoor"/> instant), for <see cref="CloseDoor"/> to slide them in. Each is set down as the deck tool
    /// would (door_leaf_8, both halves of a 16 m blast door, tagged as the door) and the view hears it as a placed artefact.
    /// </summary>
    private void HangEscapeDoors()
    {
        foreach (var (letter, a, b, degrees) in EscapeLeaves)
        {
            if (Deck is not { } deck || DoorTag(deck, letter) is not { } tag || DoorLeaves(letter).Count > 0 || _openLeaves.ContainsKey(letter)
                || Story is not { } run || run.Director.Blown.Contains(letter))
                continue;
            var leaves = new[] { PlaceArtefact("door_leaf_8", a, degrees), PlaceArtefact("door_leaf_8", b, degrees) };
            if (leaves.Any(l => l is null))
                continue;
            leaves[0]!.Tag = tag;
            leaves[1]!.Tag = tag + 1;
            OpenDoor(letter, instant: true);
        }
    }

    /// <summary>Where <see cref="HangEscapeDoors"/> hangs each door's two leaves, and how they are turned (deck.mjs's places).</summary>
    private static readonly (char Letter, Vector2 A, Vector2 B, float Degrees)[] EscapeLeaves =
        [('X', new(350, 140), new(350, 148), 90), ('Y', new(516, 122), new(524, 122), 0)];

    /// <summary>
    /// Puts the hold's containers and the rack on their rails (the escape's start): their deck props come off the map, so the
    /// view draws them from the rails and the nav grid no longer counts them; nothing slides until the rolls start.
    /// </summary>
    internal void StartRails(StoryRun run)
    {
        var hazards = run.Hazards;
        if (hazards.Rails.Count > 0 || Deck is not { } deck)
            return;
        HangEscapeDoors();
        foreach (var (name, lane, width, half) in RailProps)
        {
            if (deck.Props.FirstOrDefault(p => p.Name == name) is not { } prop)
                continue;
            var x = (int)MathF.Round(prop.Position.X * 1000);
            var w = (int)(width * 500);
            var h = (int)(half * 1000);
            var centre = Math.Clamp((int)MathF.Round(prop.Position.Y * 1000), RailSouth + h, RailNorth - h);
            hazards.Rails.Add(new StoryRail(name, lane, prop.Id, x - w, x + w, h, centre));
            if (Artefacts.FirstOrDefault(a => a.Tag == prop.Id && !a.IsDead) is { } artefact)
                RemoveArtefact(artefact.Id);
        }
    }

    /// <summary>The rolls start: the first one in a moment (only L1 moves on it: show, then use), one every <see cref="StoryHazards.Cycle"/>.</summary>
    internal void StartRolls(StoryRun run)
    {
        if (run.Hazards.RollsFrom < 0)
            run.Hazards.RollsFrom = Tick;
    }

    /// <summary>How long a roll's telegraph lasts by the difficulty, ticks (Leicht 2.5 s, Normal 2 s, Schwer 1.6 s).</summary>
    private static int RollTelegraph(StoryRun run) => run.Options.Difficulty switch
    {
        StoryDifficulty.Easy => 50,
        StoryDifficulty.Hard => 32,
        _ => 40,
    };

    private void UpdateRails(StoryRun run)
    {
        var hazards = run.Hazards;
        if (hazards.Rails.Count == 0)
            return;
        if (hazards.RollsFrom >= 0)
        {
            var roll = (int)((Tick - hazards.RollsFrom) / StoryHazards.Cycle);
            if (roll != hazards.Roll)
                StartRoll(run, roll);
        }
        LooseRack(run);
        foreach (var rail in hazards.Rails)
        {
            var locked = rail.Lane >= 3 && Tick < hazards.LeverUntil && !rail.IsRack;
            if (rail.State == RailState.Telegraph && Tick >= rail.SlideAt && !locked)
            {
                rail.State = RailState.Slide;
                StoryEvents.Cue(this, $"rail slide {rail.Name}", rail.Middle);
            }
            if (rail.State == RailState.Slide)
            {
                if (locked)
                {
                    (rail.State, rail.Speed) = (RailState.Rest, 0);
                    continue;
                }
                Slide(run, rail);
            }
        }
    }

    /// <summary>A roll begins: the ship lists one way, and each lane's container not chocked, not locked and not already at that end is let go after the telegraph, the lanes one after another.</summary>
    private void StartRoll(StoryRun run, int roll)
    {
        var hazards = run.Hazards;
        hazards.Roll = roll;
        var way = roll % 2 == 0 ? -1 : 1;
        var telegraph = RollTelegraph(run);
        foreach (var lane in hazards.Chocked.Keys.ToList())
        {
            if (--hazards.Chocked[lane] < 0)
                hazards.Chocked.Remove(lane);
        }
        var any = false;
        foreach (var rail in hazards.Rails)
        {
            if (rail.IsRack || rail.State == RailState.Slide || roll == 0 && rail.Lane != 1 || hazards.Chocked.ContainsKey(rail.Lane)
                || rail.Lane >= 3 && Tick < hazards.LeverUntil)
                continue;
            var end = way > 0 ? RailNorth - rail.HalfLength : RailSouth + rail.HalfLength;
            if (rail.Centre == end)
                continue;
            rail.State = RailState.Telegraph;
            rail.Way = way;
            rail.Speed = 0;
            rail.SlideAt = Tick + telegraph + (rail.Lane - 1) * StoryHazards.LaneStagger;
            rail.Struck.Clear();
            any = true;
        }
        if (!any)
            return;
        // The view: the groan from the side that sinks, the horizon leaning, the gyro, each lane's light strip.
        StoryEvents.Cue(this, FormattableString.Invariant($"roll {(way > 0 ? "north" : "south")} {roll} {telegraph}"), new Vector2(430, 170));
        StoryEvents.Cue(this, FormattableString.Invariant($"tilt {(way > 0 ? 9 : -9)} 0 2"));
    }

    /// <summary>
    /// The crush gag (design-escape §3 C2): once the rearmost hero is past L4 and a pack member comes within 14 m of L4's
    /// aisle crossing, the rack's chains snap, and after its telegraph it ploughs down L4; never while a hero stands in L4.
    /// </summary>
    private void LooseRack(StoryRun run)
    {
        var hazards = run.Hazards;
        if (hazards.RackLoose || hazards.Rails.FirstOrDefault(r => r.IsRack) is not { } rack || !run.Pack.Any)
            return;
        var heroes = EscapeHeroes(run).ToList();
        if (heroes.Count == 0 || heroes.Any(h => h.Position.X >= 384 && h.Position.Y >= 120))
            return;
        var crossing = new Vector2((rack.X0 + rack.X1) / 2000f, 170);
        if (!PackAlive(run).Any(m => Vector2.Distance(m.Position, crossing) <= 14))
            return;
        hazards.RackLoose = true;
        rack.State = RailState.Telegraph;
        rack.Way = -1;
        rack.Speed = 0;
        rack.SlideAt = Tick + RollTelegraph(run);
        rack.Struck.Clear();
        StoryEvents.Cue(this, "rack loose", rack.Middle);
    }

    /// <summary>
    /// One tick of a slide: faster up to its top, on to the end of its rail; never into another container of its lane; it
    /// stops short of a hero by <see cref="StoryHazards.PinMargin"/> (a hero it brushes at speed takes its share and is shoved
    /// out of the lane where there is room); a pack member it meets takes the hazard's share, and one that survives stops it.
    /// </summary>
    private void Slide(StoryRun run, StoryRail rail)
    {
        var hazards = run.Hazards;
        var top = rail.IsRack ? StoryHazards.RackSpeed : StoryHazards.TopSpeed;
        var speed = Math.Min(top, Math.Abs(rail.Speed) + (rail.IsRack ? 2 * StoryHazards.Gain : StoryHazards.Gain));
        var way = rail.Way;
        var c = rail.Centre;
        var h = rail.HalfLength;
        var lo = RailSouth + h;
        var hi = RailNorth - h;
        foreach (var other in hazards.Rails)
        {
            if (other == rail || other.Lane != rail.Lane)
                continue;
            if (other.Centre > c)
                hi = Math.Min(hi, other.Centre - other.HalfLength - h);
            else
                lo = Math.Max(lo, other.Centre + other.HalfLength + h);
        }
        var want = Math.Clamp(c + way * speed, lo, hi);
        var halfWidth = (rail.X1 - rail.X0) / 2;
        var middleX = (rail.X0 + rail.X1) / 2;
        var front = c + way * h;
        // The trio: never pinned; stopped short of, brushed at speed.
        foreach (var hero in EscapeHeroes(run).OrderBy(m => m.Id))
        {
            var (hx, hy, r) = ((int)(hero.Position.X * 1000), (int)(hero.Position.Y * 1000), (int)(hero.Radius * 1000));
            if (Math.Abs(hx - middleX) >= halfWidth + r)
                continue;
            var edge = way > 0 ? hy - r : hy + r;
            if ((edge - front) * way < -r)
                continue;
            var limit = edge - way * (StoryHazards.PinMargin + h);
            if ((want - limit) * way > 0)
            {
                var gap = (edge - front) * way;
                if (Math.Abs(rail.Speed) >= StoryHazards.HarmSpeed && gap <= StoryHazards.PinMargin + 500 && rail.Struck.Add(hero.Id))
                {
                    HazardHit(run, hero, StoryHazards.BrushShare);
                    ShoveOut(hero, rail);
                    StoryEvents.Cue(this, $"rail brush {rail.Name} {hero.Id.ToString(CultureInfo.InvariantCulture)}", hero.Position);
                }
                want = (limit - c) * way > 0 ? limit : c;
            }
        }
        // The pack: blind to it, struck by it.
        foreach (var member in PackAlive(run).Concat(Machines.Where(m => !m.IsDead && m.Inside is null && !run.Pack.Members.ContainsKey(m.Id)
                     && CompanionSeat(m) is null)).OrderBy(m => m.Id).ToList())
        {
            var (mx, my, r) = ((int)(member.Position.X * 1000), (int)(member.Position.Y * 1000), (int)(member.Radius * 1000));
            if (Math.Abs(mx - middleX) >= halfWidth + r || member.IsDead)
                continue;
            var edge = way > 0 ? my - r : my + r;
            var reach = way > 0 ? want + h : want - h;
            if ((edge - front) * way < -2 * r || (reach - edge) * way < 0)
                continue;
            if (run.Pack.Members.ContainsKey(member.Id) && rail.Struck.Add(member.Id))
            {
                if (rail.IsRack)
                    PackHazard(run, member, (1, 0), (1, 0), (0.45f, 80), 60, "rack");
                else
                    PackHazard(run, member, (1, 0), (0.70f, 40), (0.45f, 80), 50, "container");
            }
            if (member.IsDead)
                continue;
            var stop = edge - way * h;
            want = (stop - c) * way > 0 ? stop : c;
        }
        rail.Speed = want - c;
        rail.Centre = want;
        if (want == (way > 0 ? hi : lo) && rail.Speed == 0 || want == (way > 0 ? RailNorth - h : RailSouth + h))
        {
            if (Math.Abs(want - c) > 0 || rail.Speed == 0)
                StoryEvents.Cue(this, $"rail end {rail.Name}", rail.Middle);
            (rail.State, rail.Speed, rail.Way) = (RailState.Rest, 0, 0);
        }
    }

    /// <summary>A hero a container brushed is shoved out of its lane, to the nearer side, where there is ground for it.</summary>
    private void ShoveOut(Machine hero, StoryRail rail)
    {
        var mobility = MobilityOf(hero);
        var west = rail.X0 / 1000f - hero.Radius - 0.6f;
        var east = rail.X1 / 1000f + hero.Radius + 0.6f;
        var sides = hero.Position.X - west < east - hero.Position.X ? new[] { west, east } : [east, west];
        foreach (var x in sides)
        {
            var spot = new Vector2(x, hero.Position.Y);
            if (Nav.IsFree(spot, mobility))
            {
                hero.Position = spot;
                return;
            }
        }
    }

    /// <summary>
    /// Whether a sliding container stands where a machine's step from <paramref name="from"/> would take it (GameWorld.Motion):
    /// as with a door's leaf, a machine waits before it; one that touches it already may move off it, not further into it.
    /// </summary>
    private bool RailInWay(Machine machine, Vector2 from, Vector2 to)
    {
        if (Story is not { } run || run.Hazards.Rails.Count == 0)
            return false;
        foreach (var rail in run.Hazards.Rails)
        {
            var near = RailApart(rail, to);
            if (near < machine.Radius && near < RailApart(rail, from))
                return true;
        }
        return false;
    }

    /// <summary>How far a point is from a rail's container (0 inside it), metres.</summary>
    internal static float RailApart(StoryRail rail, Vector2 p)
    {
        var dx = MathF.Max(0, MathF.Abs(p.X - (rail.X0 + rail.X1) / 2000f) - rail.HalfWidth);
        var dy = MathF.Max(0, MathF.Abs(p.Y - rail.Centre / 1000f) - rail.HalfLength / 1000f);
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    // ---- the jets -----------------------------------------------------------------------------------------

    /// <summary>The jets start their timetable (the trio at the shutter); J1's show waits for the gallery's entry.</summary>
    internal void StartJets(StoryRun run)
    {
        if (run.Hazards.JetsFrom < 0)
            run.Hazards.JetsFrom = Tick;
    }

    /// <summary>J1's show at the gallery's entry (design-escape §3 C4): the hush, the warning, the burn.</summary>
    internal void ShowJ1(StoryRun run)
    {
        StartJets(run);
        if (run.Hazards.J1From < 0)
            run.Hazards.J1From = Tick;
    }

    private void UpdateJets(StoryRun run)
    {
        var hazards = run.Hazards;
        if (hazards.JetsFrom < 0 || Deck is not { } deck)
            return;
        // Someone standing on J4's valve keeps it off, and for a while after (the holder crosses last).
        if (deck.Markers.FirstOrDefault(m => m.Name == "valve_j4") is { } valve
            && EscapeHeroes(run).Any(h => Vector2.Distance(h.Position, valve.Position) <= GameWorld.UseReach + h.Radius))
            hazards.J4OffUntil = Math.Max(hazards.J4OffUntil, Tick + StoryHazards.ValveAfter);
        if (Tick % StoryHazards.HarmEvery != 0)
            return;
        for (var jet = 1; jet <= 5; jet++)
        {
            if (!hazards.Burns(jet, Tick))
                continue;
            foreach (var machine in InZone($"jet_j{jet}"))
            {
                if (run.Pack.Members.ContainsKey(machine.Id))
                    PackHazard(run, machine, (0.25f, 60), (0.20f, 60), (0.10f, 0), 0, "fire");
                else if (InTrio(run, machine))
                    HazardHit(run, machine, StoryHazards.JetShare);
            }
        }
    }

    // ---- the shutter X ------------------------------------------------------------------------------------

    /// <summary>The shutter starts lowering by itself (design-escape §3 C3): P_AUTO_SHUT, the klaxon, 10 s down.</summary>
    internal void CloseShutter(StoryRun run)
    {
        if (run.Hazards.Shutter == ShutterState.Open)
            run.Hazards.Shutter = ShutterState.Closing;
    }

    /// <summary>Whether all the trio stand past the shutter (in the gallery or on).</summary>
    private bool PastShutter(StoryRun run) =>
        EscapeHeroes(run).All(h => h.Position.X < 348 && h.Position.Y < 160 || h.Position.Y < 100);

    private void UpdateShutter(StoryRun run)
    {
        var hazards = run.Hazards;
        var frame = Deck?.Zone("x_frame");
        switch (hazards.Shutter)
        {
            case ShutterState.Closing:
            {
                var held = frame is not null && EscapeHeroes(run).Any(h => Overlaps(frame, h.Position, h.Radius));
                var through = PastShutter(run);
                if (through && !held)
                    hazards.ShutterDown += StoryHazards.ShutterLower / StoryHazards.ShutterSlam;
                else if (!(held && hazards.ShutterDown >= StoryHazards.ShutterLower / 2))
                    hazards.ShutterDown++;
                if (hazards.ShutterDown < StoryHazards.ShutterLower)
                    return;
                hazards.ShutterDown = StoryHazards.ShutterLower;
                if (!CloseDoor('X'))
                    return;
                hazards.Shutter = ShutterState.Shut;
                hazards.ShutterShutTicks = 0;
                StoryEvents.Cue(this, through ? "shutter slam" : "shutter shut", new Vector2(350, 144));
                // Nobody held it and someone is still in the hold: the duct beside it and its panel (design-escape §3 C3).
                if (!through)
                    run.Director.Act(this, run, new Then.Hackable(true, "hack_x"));
                return;
            }
            case ShutterState.Shut:
            {
                hazards.ShutterShutTicks++;
                if (!PastShutter(run) && run.Hacks.Done.Contains("hack_x"))
                {
                    // The panel hacked: it rises again and stays up until the trio is through, then slams.
                    if (OpenDoor('X'))
                    {
                        (hazards.Shutter, hazards.ShutterDown) = (ShutterState.Closing, StoryHazards.ShutterLower / 2);
                        StoryEvents.Cue(this, "shutter up", new Vector2(350, 144));
                    }
                    return;
                }
                var x = new Vector2(352, 144);
                if (run.Director.Of(this, Prologue.ThresherRole).Any(t => Vector2.Distance(t.Position, x) <= 16))
                {
                    hazards.ShutterRammed++;
                    if (hazards.ShutterRammed % 20 == 0 && hazards.ShutterRammed < StoryHazards.ShutterRams)
                        StoryEvents.Cue(this, $"shutter ram {hazards.ShutterRammed / 20}", x);
                }
                if (hazards.ShutterRammed >= StoryHazards.ShutterRams || hazards.ShutterShutTicks >= StoryHazards.ShutterGives)
                {
                    if (PastShutter(run) && BlowDoor('X'))
                    {
                        hazards.Shutter = ShutterState.Broken;
                        StoryEvents.Cue(this, "shutter burst", x);
                    }
                }
                return;
            }
        }
    }

    // ---- the collapse, the fire face ------------------------------------------------------------------------

    /// <summary>The ground of a zone as a polygon (its rectangle).</summary>
    private static Vector2[] ZoneArea(DeckZone zone) =>
        [zone.Min, new(zone.Max.X, zone.Min.Y), zone.Max, new(zone.Min.X, zone.Max.Y)];

    /// <summary>
    /// The Notgang caves in at the hull breach (design-escape §3 C1): its cells south of the fire face become wall, a pack
    /// member in them is destroyed; the fire face burns at its edge. Never with a hero in it or beyond it: false then.
    /// </summary>
    internal bool Collapse(StoryRun run)
    {
        var hazards = run.Hazards;
        if (hazards.Collapsed)
            return true;
        if (Deck?.Zone("collapse_notgang") is not { } zone)
            return false;
        if (EscapeHeroes(run).Any(h => Overlaps(zone, h.Position, h.Radius) || h.Position.X >= 508 && h.Position.Y < zone.Min.Y && h.Position.Y >= 118))
            return false;
        foreach (var machine in InZone("collapse_notgang"))
        {
            if (run.Pack.Members.ContainsKey(machine.Id))
                HazardHit(run, machine, 1);
            if (!machine.IsDead)
                machine.Position = new Vector2(520, zone.Max.Y + machine.Radius + 3);
        }
        Nav.SetArea(ZoneArea(zone), present: true);
        hazards.Collapsed = true;
        hazards.FireFace = true;
        StoryEvents.Cue(this, "collapse notgang", (zone.Min + zone.Max) / 2);
        return true;
    }

    /// <summary>The pack blasts its way through the collapse (design-escape §3 C7): its cells free again, the fire out.</summary>
    internal void Reopen(StoryRun run)
    {
        var hazards = run.Hazards;
        if (!hazards.Collapsed || Deck?.Zone("collapse_notgang") is not { } zone)
            return;
        Nav.SetArea(ZoneArea(zone), present: false);
        hazards.Collapsed = false;
        hazards.FireFace = false;
        StoryEvents.Cue(this, "reopen notgang", (zone.Min + zone.Max) / 2);
    }

    private void UpdateFireFace(StoryRun run)
    {
        if (!run.Hazards.FireFace || Tick % StoryHazards.HarmEvery != 0)
            return;
        foreach (var machine in InZone("fire_notgang"))
        {
            if (run.Pack.Members.ContainsKey(machine.Id))
                PackHazard(run, machine, (0.25f, 60), (0.20f, 60), (0.10f, 0), 0, "fire");
            else if (InTrio(run, machine))
                HazardHit(run, machine, StoryHazards.FireShare);
        }
    }

    // ---- plates on the pack ---------------------------------------------------------------------------------

    /// <summary>A plate let fall on a role of the pack (its first live member): it follows it and lands after its warning.</summary>
    internal void FallOn(StoryRun run, string role)
    {
        if (run.Director.Of(this, role).FirstOrDefault() is not { } target)
            return;
        var number = run.Hazards.PlatesFallen++;
        run.Hazards.Plates[number] = (Tick + StoryHazards.PlateWarning, target.Id);
        StoryEvents.Cue(this, FormattableString.Invariant($"plate falling {number} {target.Id}"), target.Position);
    }

    private void UpdatePlates(StoryRun run)
    {
        foreach (var (number, (at, target)) in run.Hazards.Plates.ToList())
        {
            if (Tick < at)
                continue;
            run.Hazards.Plates.Remove(number);
            if (Find<Machine>(target) is not { IsDead: false } member)
                continue;
            StoryEvents.Cue(this, FormattableString.Invariant($"plate landed {number} {member.Position.X:0.#} {member.Position.Y:0.#}"), member.Position);
            PackHazard(run, member, (0.50f, 40), (0.40f, 40), (0.15f, 20), 30, "plate");
        }
    }

    // ---- the bridge ---------------------------------------------------------------------------------------

    /// <summary>The bridge creaks and falls once nobody of the trio is on it (design-escape §3 C5).</summary>
    internal void LetBridgeFall(StoryRun run)
    {
        if (run.Hazards.BridgeFallAt < 0 && !run.Hazards.BridgeFallen)
        {
            run.Hazards.BridgeFallAt = Tick;
            StoryEvents.Cue(this, "bridge creak", new Vector2(388, 92));
        }
    }

    private void UpdateBridge(StoryRun run)
    {
        var hazards = run.Hazards;
        if (Deck?.Zone("R19") is not { } bridge)
            return;
        if (hazards.Drift && !hazards.BridgeFallen)
        {
            foreach (var machine in InZone("R19"))
            {
                var v = InTrio(run, machine)
                    ? CompanionSeat(machine) is null ? StoryHazards.DriftLocator : StoryHazards.DriftGrunt
                    : StoryHazards.DriftPack;
                var step = new Vector2(0, v * Dt);
                if (Nav.IsFree(machine.Position + step + new Vector2(0, machine.Radius), MobilityOf(machine)))
                    machine.Position += step;
            }
        }
        if (hazards.BridgeFallen || hazards.BridgeFallAt < 0 || Tick < hazards.BridgeFallAt + StoryHazards.BridgeCreak)
            return;
        if (EscapeHeroes(run).Any(h => Overlaps(bridge, h.Position, h.Radius)))
            return;
        foreach (var machine in InZone("R19"))
        {
            if (run.Pack.Members.TryGetValue(machine.Id, out var kind) && kind != PackKind.Thresher)
                HazardHit(run, machine, 1);
            else if (!machine.IsDead && Deck.Marker("thresher_edge") is { } edge)
                machine.Position = edge.Position;
        }
        Nav.SetArea(ZoneArea(bridge), present: true);
        hazards.BridgeFallen = true;
        hazards.Drift = false;
        StoryEvents.Cue(this, "bridge fall", (bridge.Min + bridge.Max) / 2);
    }

    // ---- the crane's amber lock ---------------------------------------------------------------------------

    /// <summary>The crane's lock prop's tag (deck PROPS crane_lock).</summary>
    public const int CraneLockTag = 340;

    private void UpdateCrane(StoryRun run)
    {
        var hazards = run.Hazards;
        if (hazards.CraneDown || run.Pack.Members.Count == 0 || Artefacts.Any(a => a.Tag == CraneLockTag && !a.IsDead))
            return;
        // Its beam lands on Y's apron, once no hero stands under it.
        var apron = (Min: new Vector2(500, 100), Max: new Vector2(540, 118));
        bool Under(Machine m) => m.Position.X >= apron.Min.X && m.Position.X < apron.Max.X && m.Position.Y >= apron.Min.Y && m.Position.Y < apron.Max.Y;
        if (EscapeHeroes(run).Any(Under))
            return;
        hazards.CraneDown = true;
        StoryEvents.Cue(this, "crane beam", (apron.Min + apron.Max) / 2);
        foreach (var member in PackAlive(run).Where(Under).ToList())
            PackHazard(run, member, (1, 0), (1, 0), (0.60f, 80), 0, "beam");
    }

    // ---- the devices (Use): the hand wheel, the lever ---------------------------------------------------------

    private void UpdateEscapeDevices(StoryRun run)
    {
        var hazards = run.Hazards;
        foreach (var seat in run.Seats)
        {
            var holding = run.Crew.Holding.TryGetValue(seat.Value, out var what) ? what : null;
            var need = holding switch
            {
                "wheel_j4" => StoryHazards.WheelHold,
                "lever_f" => StoryHazards.LeverHold,
                _ => 0,
            };
            if (need == 0)
            {
                hazards.UseTicks.Remove(seat.Value);
                continue;
            }
            var ticks = hazards.UseTicks[seat.Value] = hazards.UseTicks.GetValueOrDefault(seat.Value) + 1;
            if (ticks < need)
                continue;
            hazards.UseTicks.Remove(seat.Value);
            run.Crew.Holding.Remove(seat.Value);
            if (holding == "wheel_j4")
            {
                hazards.J4OffUntil = Math.Max(hazards.J4OffUntil, Tick + StoryHazards.WheelOff);
                StoryEvents.Cue(this, "wheel j4", Deck?.Marker("wheel_j4").Position ?? default);
            }
            else
            {
                hazards.LeverUntil = Tick + StoryHazards.LeverLock;
                StoryEvents.Cue(this, "lever lanes", Deck?.Marker("lever_f").Position ?? default);
            }
        }
    }
}
