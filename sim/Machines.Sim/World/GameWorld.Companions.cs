using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Ai;
using Machines.Sim.Combat;
using Machines.Sim.Story;
using Machines.Sim.Survival;

namespace Machines.Sim.World;

/// <summary>
/// The story's companions, Ike and Bob (docs/STORY.md, Die beiden Grunts and Koop; plan WP-F). Unarmed, they help with their
/// bodies: they follow the Locator, wait, distract, barricade doors with crates and with themselves, hold gate switches, ram
/// enemies so that the Locator can hit them, and follow it into the pod. A Grunt is never destroyed: it lies downed at one
/// hit point as a scrap heap until the Locator holds it together or a repair station brings it back, the same actor all the
/// while, so seats, names and orders stay. Before the Locator hacks it awake it hangs dormant in its clamp. The computer
/// plays the Grunts of <see cref="StoryRun.Bots"/> by <see cref="CompanionBot"/>; a person who takes a seat over plays the
/// same hero on. Nothing here runs outside a story.
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>How near the Locator must be to put a downed Grunt together, and how long it holds Use for it.</summary>
    public const float ReviveReach = 4, ReviveSeconds = 4;

    /// <summary>How long a downed Grunt lies in the repair station before it is back.</summary>
    public const float RepairReviveSeconds = 3;

    /// <summary>The share of its hit points a Grunt comes back with.</summary>
    public const float ReviveShare = 0.5f;

    /// <summary>How near a hero must be to use a switch, a rack or a hatch.</summary>
    public const float UseReach = 3.5f;

    /// <summary>How far beyond touching a ram reaches, how long a Grunt waits between rams, and how long its victim holds fire.</summary>
    public const float RamReach = 1.5f, RamCooldown = 3, StunSeconds = 1.5f;

    /// <summary>
    /// What a Grunt's ram takes off: a knock, not a kill (an Old Iron's ram, <see cref="WiredBalance.RamDamage"/>, would end a
    /// Grunt 1 outright). "Sie rammen die Gegner um, damit er treffen kann": the stun is what counts.
    /// </summary>
    public const int StoryRamDamage = 6;

    /// <summary>How far a noise of loudness 1 carries to the ship's machines (stub of the hearing of plan WP-D).</summary>
    public const float HearingReach = 30;

    /// <summary>The deck's zone of the repair station, and the pod's and its bay's.</summary>
    public const string RepairZone = "repair", PodZone = "pod_zone", PodBay = "R16";

    /// <summary>The seat of the Grunt whose hero <paramref name="machine"/> is (Ike's or Bob's), or null.</summary>
    public PlayerId? CompanionSeat(Machine machine)
    {
        if (Story is not { } run || Wired is not { } heroes)
            return null;
        for (var i = 1; i < run.Seats.Count; i++)
        {
            if (heroes.Heroes.GetValueOrDefault(run.Seats[i])?.Hero == machine.Id)
                return run.Seats[i];
        }
        return null;
    }

    /// <summary>Whether a Grunt lies downed, a scrap heap until the Locator or a repair station brings it back.</summary>
    public bool CompanionDown(PlayerId seat) => Story is { } run && run.Flags.Contains(StoryMarks.Down(seat));

    /// <summary>Whether a Grunt still hangs switched off in its clamp, before the Locator hacks it awake.</summary>
    public bool CompanionDormant(PlayerId seat) => Story is { } run && run.Flags.Contains(StoryMarks.Dormant(seat));

    /// <summary>Whether an actor is a Grunt of the story lying downed or dormant: nobody fires at it, nothing hurts it, it does nothing.</summary>
    public bool StoryInert(Actor actor) =>
        Story is not null && actor is Machine machine && CompanionSeat(machine) is { } seat && (CompanionDown(seat) || CompanionDormant(seat));

    /// <summary>Whether a Grunt of the story lies downed, so that it stands in nobody's way.</summary>
    private bool StoryLies(Machine machine) => Story is not null && CompanionSeat(machine) is { } seat && CompanionDown(seat);

    /// <summary>Whether a ram knocked a machine over a moment ago: it holds its fire.</summary>
    public bool Stunned(Machine machine) => Story?.Crew.Stunned.TryGetValue(machine.Id, out var until) == true && Tick < until;

    /// <summary>
    /// Whether a hero of the story is held where it stands: a Grunt downed or dormant, or the Locator holding a Grunt
    /// together. Its driver may look about but neither drive nor fire, as while it hacks.
    /// </summary>
    private bool StoryHolds(int machine)
    {
        if (Story is not { } run || Find<Machine>(machine) is not { } hero)
            return false;
        if (StoryInert(hero))
            return true;
        return HeroOf(run.Locator)?.Id == machine && run.Crew.Holding.TryGetValue(run.Locator.Value, out var what) && what.StartsWith(SeatTarget, StringComparison.Ordinal);
    }

    private const string SeatTarget = "seat:";

    // ---- the director's hand ------------------------------------------------------------------------

    /// <summary>
    /// Hangs a Grunt switched off in its clamp (<paramref name="dormant"/>), or wakes it: it does nothing, is no target and
    /// takes no hurt until then. Hacking its terminal wakes it by itself (<see cref="WakeTerminals"/>).
    /// </summary>
    public void SetCompanionDormant(PlayerId seat, bool dormant)
    {
        if (Story is not { } run || run.Seats.IndexOf(seat) < 1)
            return;
        if (dormant)
        {
            if (run.Flags.Add(StoryMarks.Dormant(seat)) && HeroOf(seat) is { } grunt)
            {
                EndJob(run, run.Crew.Of(seat));
                StartAfresh(grunt);
                HoldStill(grunt.Id);
            }
            return;
        }
        if (!run.Flags.Remove(StoryMarks.Dormant(seat)))
            return;
        var companion = run.Crew.Of(seat);
        companion.Order = CompanionOrder.Follow;
        CompanionBark(run, seat, CompanionBarks.Cue.Wake, urgent: true, once: true);
    }

    /// <summary>
    /// Gives a Grunt a job over the Locator's last order: barricade a door (<paramref name="place"/> its letter), hold a
    /// switch (its marker), or board the pod (the pod's hatch marker, or null). <see cref="CompanionTask.None"/> ends the job.
    /// The computer carries it out while it plays the seat; the Locator's next order ends it. False for no Grunt's seat.
    /// </summary>
    public bool AssignCompanion(PlayerId seat, CompanionTask task, string? place = null)
    {
        if (Story is not { } run || run.Seats.IndexOf(seat) < 1)
            return false;
        var companion = run.Crew.Of(seat);
        EndJob(run, companion);
        companion.Task = task;
        companion.Place = place;
        if (task == CompanionTask.Barricade)
            CompanionBark(run, seat, CompanionBarks.Cue.Barricade, urgent: true);
        return true;
    }

    /// <summary>
    /// Whether a gate's switch is held: anyone of the trio standing on it (in its zone, <c>&lt;marker&gt;_zone</c>, or within
    /// reach of the marker where the deck has no zone), or holding Use at it, and neither downed nor dormant.
    /// </summary>
    public bool SwitchHeld(string marker)
    {
        if (Story is not { } run || Deck is not { } deck || deck.Markers.FirstOrDefault(m => m.Name == marker) is not { } at)
            return false;
        var zone = deck.Zone(marker + "_zone");
        foreach (var seat in run.Seats)
        {
            if (HeroOf(seat) is not { } hero || StoryInert(hero))
                continue;
            if (zone?.Contains(hero.Position) ?? Vector2.Distance(hero.Position, at.Position) <= UseReach)
                return true;
            if (run.Crew.Holding.TryGetValue(seat.Value, out var held) && held == marker && Vector2.Distance(hero.Position, at.Position) <= UseReach)
                return true;
        }
        return false;
    }

    /// <summary>Whether both Grunts are in the pod for the launch: standing in the pod's zone, or lying downed in its bay.</summary>
    public bool CompanionsAboard()
    {
        if (Story is not { } run || Deck is not { } deck || deck.Zone(PodZone) is not { } pod)
            return false;
        var bay = deck.Zone(PodBay);
        foreach (var seat in run.Seats.Skip(1))
        {
            if (HeroOf(seat) is not { } grunt)
                return false;
            if (!pod.Contains(grunt.Position) && !(CompanionDown(seat) && bay?.Contains(grunt.Position) == true))
                return false;
        }
        return true;
    }

    /// <summary>
    /// The ship's machines that hear a noise of the trio, in id order: within <see cref="HearingReach"/> times its loudness,
    /// half that through a wall, as the sight hears (<see cref="StoryPerceives"/>; the sight takes the noise up as the maker's
    /// signature, <see cref="StorySight.Noise"/>). A machine switched off (Blind) hears nothing.
    /// </summary>
    public IReadOnlyList<Machine> HeardBy(StoryNoise noise)
    {
        if (Story is not { } run)
            return [];
        var reach = HearingReach * noise.Loudness;
        var maker = Find<Machine>(noise.Source);
        return Machines.Where(m => !m.IsDead && m.Race == run.Options.Ship && m.Inside is null && StorySeekerOf(m.Id) is not { Blind: true }
                && Vector2.Distance(m.Position, noise.Position) <= (maker is null || ClearShot(m, maker) ? reach : reach / 2))
            .OrderBy(m => m.Id).ToList();
    }

    // ---- orders -------------------------------------------------------------------------------------

    internal string? OrderCompanions(StoryOrderCommand command)
    {
        var run = Story!;
        if ((command.Issuer ?? run.Locator) != run.Locator)
            return "only the Locator gives the Grunts orders";
        List<PlayerId> seats;
        if (command.Seat is { } one)
        {
            if (run.Seats.IndexOf(one) < 1)
                return "no such Grunt";
            if (!StoryBotPlays(one))
                return "a person plays that Grunt";
            seats = [one];
        }
        else
        {
            seats = run.Seats.Skip(1).Where(s => StoryBotPlays(s) && HeroOf(s) is not null && !CompanionDown(s) && !CompanionDormant(s)).ToList();
            // A distraction is one Grunt's noise: the one nearer the point goes (the first seat on a tie), the other stays
            // with its order. Two in one lane would only block each other and the Locator.
            if (command.Order == CompanionOrder.Distract && seats.Count > 1)
                seats = [seats.OrderBy(s => Vector2.DistanceSquared(HeroOf(s)!.Position, command.Point)).ThenBy(s => run.Seats.IndexOf(s)).First()];
        }
        var taken = new List<PlayerId>();
        foreach (var seat in seats)
        {
            if (HeroOf(seat) is not { } grunt || CompanionDown(seat) || CompanionDormant(seat))
                continue;
            var companion = run.Crew.Of(seat);
            EndJob(run, companion);
            companion.Order = command.Order;
            companion.Point = command.Point;
            companion.Back = grunt.Position;
            CarryOut(run, companion, grunt);
            taken.Add(seat);
        }
        if (taken.Count == 0)
            return "no Grunt takes orders now";
        // One of them answers, not both at once.
        var cue = command.Order switch
        {
            CompanionOrder.Wait => CompanionBarks.Cue.Wait,
            CompanionOrder.Distract => CompanionBarks.Cue.Distract,
            _ => CompanionBarks.Cue.Follow,
        };
        CompanionBark(run, taken[Tick % taken.Count], cue, urgent: true);
        return null;
    }

    /// <summary>Sets a Grunt about its order at once: after the Locator, standing, or off to the distraction's point.</summary>
    internal void CarryOut(StoryRun run, Companion companion, Machine grunt)
    {
        switch (companion.Order)
        {
            case CompanionOrder.Follow:
                if (HeroOf(run.Locator) is { } locator)
                {
                    if (grunt.Order is not FollowOrder follow || follow.Target != locator.Id)
                        Send(grunt, new FollowOrder(locator.Id), locator.Position);
                }
                else if (grunt.Order is not StandGroundOrder)
                    Send(grunt, new StandGroundOrder(), null);
                break;
            case CompanionOrder.Wait:
                if (grunt.Order is not StandGroundOrder)
                    Send(grunt, new StandGroundOrder(), null);
                break;
            case CompanionOrder.Distract:
                companion.Step = CompanionStep.Going;
                companion.StepUntil = Tick + 30 * TicksPerSecond;
                run.Flags.Add(StoryMarks.Distracting(companion.Seat));
                Send(grunt, new MoveOrder(companion.Point), companion.Point);
                break;
        }
    }

    /// <summary>Ends a Grunt's job or distraction: a crate it carries goes back on the pile, and it draws nobody off any more.</summary>
    private void EndJob(StoryRun run, Companion companion)
    {
        if (companion.Carrying is { } crate)
            SetCrate(crate, crate.From);
        companion.Carrying = null;
        companion.Crate = null;
        companion.Slot = null;
        companion.Task = CompanionTask.None;
        companion.Place = null;
        companion.Step = CompanionStep.None;
        run.Flags.Remove(StoryMarks.Distracting(companion.Seat));
    }

    // ---- what the computer's Grunts do (CompanionBot) -----------------------------------------------------

    /// <summary>Sends a Grunt somewhere, unless it is on its way there already.</summary>
    internal void CompanionMove(Machine grunt, Vector2 point)
    {
        if (grunt.Order is MoveOrder move && Vector2.DistanceSquared(move.Target, point) < 0.25f)
            return;
        Send(grunt, new MoveOrder(point), point);
    }

    /// <summary>
    /// Whether a Grunt's way over the grid gets it within <paramref name="within"/> metres of a point: a crate of the pile
    /// behind the barricade it built itself is out of its reach (before, the way handed it the crate across the crates).
    /// </summary>
    internal bool CompanionReaches(Machine grunt, Vector2 point, float within)
    {
        var path = Nav.FindPath(grunt.Position, point, MobilityOf(grunt));
        return Vector2.Distance(path.Count > 0 ? path[^1] : grunt.Position, point) <= within;
    }

    /// <summary>A Grunt stands its ground where it is.</summary>
    internal void CompanionStand(Machine grunt)
    {
        if (grunt.Order is not StandGroundOrder)
            Send(grunt, new StandGroundOrder(), null);
    }

    /// <summary>Whether a Grunt has got where it was sent, or as near as its way goes: it has no order left to drive.</summary>
    internal static bool Arrived(Machine grunt) => grunt.Order is not MoveOrder;

    /// <summary>A Grunt makes its noise: the signature of the trio jumps where it stands, and the ship's machines in reach may hear it.</summary>
    internal void Honk(StoryRun run, Machine grunt)
    {
        var noise = new StoryNoise(grunt.Position, 1, Tick, grunt.Id);
        // The sight hears it: it adds to the Grunt's signature for a while (GameWorld.UpdateSignatures, StorySight.Noise).
        run.Crew.Noise(noise);
    }

    /// <summary>
    /// The enemy a Grunt goes for: the nearest machine of the ship within <paramref name="reach"/> of it that fires at one of
    /// the trio (its aim is on them), not knocked over already; ties to the lower id.
    /// </summary>
    internal Machine? CompanionFoe(StoryRun run, Machine grunt, float reach)
    {
        var trio = run.Seats.Select(s => HeroOf(s)?.Id ?? -1).ToHashSet();
        Machine? best = null;
        var bestDistance = float.MaxValue;
        foreach (var machine in Machines)
        {
            if (machine.IsDead || machine.Inside is not null || !AreEnemies(grunt.Race, machine.Race) || Stunned(machine)
                || !_machineAims.TryGetValue(machine.Id, out var aim) || !trio.Contains(aim))
                continue;
            var distance = Vector2.Distance(machine.Position, grunt.Position);
            if (distance > reach || distance > bestDistance || distance == bestDistance && machine.Id > best!.Id)
                continue;
            (best, bestDistance) = (machine, distance);
        }
        return best;
    }

    /// <summary>How far a Grunt's ram reaches to <paramref name="victim"/>, centre to centre.</summary>
    internal static float RamDistance(Machine grunt, Machine victim) => grunt.Radius + victim.Radius + RamReach;

    // ---- the ram ------------------------------------------------------------------------------------------

    internal string? Ram(StoryRamCommand command)
    {
        var run = Story!;
        if (command.Issuer is { } issuer && command.Seat is { } named && named != issuer)
            return "not your Grunt";
        if ((command.Issuer ?? command.Seat) is not { } seat)
            return "name the Grunt that rams";
        return RamWith(run, seat, command.Target);
    }

    /// <summary>
    /// A Grunt rams: it lunges at its victim (<paramref name="target"/>, or the nearest enemy machine within reach, more in
    /// front of it than behind), which takes the ram's damage as a hit of the Grunt's, is thrown on along the way and holds
    /// its fire for <see cref="StunSeconds"/>; anchored tracks hold, though.
    /// </summary>
    /// <returns>null when it rammed, otherwise why not.</returns>
    internal string? RamWith(StoryRun run, PlayerId seat, int? target)
    {
        if (run.Seats.IndexOf(seat) < 1)
            return "only a Grunt rams";
        if (HeroOf(seat) is not { } grunt || CompanionDown(seat) || CompanionDormant(seat))
            return "the Grunt is down";
        var companion = run.Crew.Of(seat);
        if (Tick < companion.RamReadyAt)
            return "that is not ready yet";
        var victim = target is { } id ? Find<Machine>(id) : RamVictim(grunt);
        if (victim is not { IsDead: false, Inside: null } || !AreEnemies(grunt.Race, victim.Race) || victim.Stats.Locomotion == Locomotion.Glider
            || Vector2.Distance(grunt.Position, victim.Position) > RamDistance(grunt, victim))
            return "nothing to ram there";
        var way = victim.Position - grunt.Position;
        var distance = way.Length();
        way = distance > 1e-4f ? way / distance : new Vector2(DetMath.Cos(grunt.Facing), DetMath.Sin(grunt.Facing));
        grunt.Facing = DetMath.Atan2(way.Y, way.X);
        var lunge = distance - grunt.Radius - victim.Radius - 0.1f;
        if (lunge > 0)
            Shove(grunt, way * lunge);
        companion.RamReadyAt = Tick + (int)(RamCooldown * TicksPerSecond);
        Raise(new GameEvent(GameEventKind.WiredRammed, victim.Id, victim.Position, grunt.Id));
        Damage(victim, StoryRamDamage, grunt.Race, grunt, RamWeapon);
        if (!victim.IsDead)
        {
            if (!Rooted(victim))
                Shove(victim, way * WiredBalance.RamPush);
            run.Crew.Stunned[victim.Id] = Tick + (int)(StunSeconds * TicksPerSecond);
            _machineAims.Remove(victim.Id);
        }
        CompanionBark(run, seat, CompanionBarks.Cue.Ram);
        return null;
    }

    /// <summary>The enemy machine nearest a Grunt within its ram's reach and not behind it; ties to the lower id.</summary>
    private Machine? RamVictim(Machine grunt)
    {
        var facing = new Vector2(DetMath.Cos(grunt.Facing), DetMath.Sin(grunt.Facing));
        return Machines
            .Where(m => !m.IsDead && m.Inside is null && AreEnemies(grunt.Race, m.Race) && m.Stats.Locomotion != Locomotion.Glider
                && Vector2.Distance(m.Position, grunt.Position) <= RamDistance(grunt, m)
                && Vector2.Dot(m.Position - grunt.Position, facing) >= 0)
            .OrderBy(m => Vector2.DistanceSquared(m.Position, grunt.Position)).ThenBy(m => m.Id)
            .FirstOrDefault();
    }

    // ---- using ------------------------------------------------------------------------------------------

    internal string? Use(StoryUseCommand command)
    {
        var run = Story!;
        var player = command.Issuer ?? run.Locator;
        var seat = run.Seats.IndexOf(player);
        if (seat < 0)
            return "not one of the trio";
        if (!command.Held)
        {
            LetGoOfUse(run, player);
            return null;
        }
        if (HeroOf(player) is not { } hero)
            return "the hero is down";
        if (seat > 0 && (CompanionDown(player) || CompanionDormant(player)))
            return "the Grunt is down";
        if ((command.Target ?? StoryUseTarget(player)) is not { } target)
            return "nothing to use here";
        if (target.StartsWith(SeatTarget, StringComparison.Ordinal))
        {
            if (seat != 0)
                return "only the Locator puts a Grunt together";
            if (!int.TryParse(target[SeatTarget.Length..], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id)
                || run.Seats.IndexOf(new PlayerId(id)) < 1 || !CompanionDown(new PlayerId(id)) || HeroOf(new PlayerId(id)) is not { } wreck)
                return "nobody to put together there";
            if (Vector2.Distance(wreck.Position, hero.Position) > ReviveReach)
                return "too far away";
            if (!run.Crew.Holding.TryGetValue(player.Value, out var before) || before != target)
                run.Crew.Of(new PlayerId(id)).ReviveTicks = 0;
            run.Crew.Holding[player.Value] = target;
            HoldStill(hero.Id);
            return null;
        }
        if (Deck?.Markers.FirstOrDefault(m => m.Name == target) is not { } marker)
            return "nothing to use there";
        if (Vector2.Distance(marker.Position, hero.Position) > UseReach)
            return "too far away";
        if (marker.Tags.Contains("switch"))
        {
            run.Crew.Holding[player.Value] = target;
            return null;
        }
        if (seat != 0)
            return "only the Locator uses that";
        if (run.Flags.Contains(StoryMarks.Done(target)))
            return "that is done with";
        if (marker.Tags.Contains("pickup"))
            return TakeUp(run, player, marker);
        if (marker.Tags.Contains("hatch"))
        {
            if (!run.Hacks.Done.Contains(target))
                return "launch clearance denied";
            run.Flags.Add(StoryMarks.Done(target));
            foreach (var grunt in run.Seats.Skip(1))
                AssignCompanion(grunt, CompanionTask.Board, target);
            return null;
        }
        return "nothing to use there";
    }

    /// <summary>
    /// What Use would act on for a player of the trio, nearest first and by name on a tie: for the Locator a downed Grunt in
    /// reach ("seat:&lt;id&gt;"), a weapon on its rack or the pod's hatch; for anyone a gate's switch. Null when nothing is in
    /// reach (hacking is the hack's: HackDesk).
    /// </summary>
    public string? StoryUseTarget(PlayerId player)
    {
        if (Story is not { } run || run.Seats.IndexOf(player) is var seat && seat < 0 || HeroOf(player) is not { } hero)
            return null;
        var near = new List<(string Target, float Distance)>();
        if (seat == 0)
        {
            foreach (var grunt in run.Seats.Skip(1))
            {
                if (CompanionDown(grunt) && HeroOf(grunt) is { } wreck && Vector2.Distance(wreck.Position, hero.Position) is var d && d <= ReviveReach)
                    near.Add((SeatTarget + grunt.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), d));
            }
        }
        foreach (var marker in Deck?.Markers ?? [])
        {
            var usable = marker.Tags.Contains("switch") || seat == 0 && (marker.Tags.Contains("pickup") || marker.Tags.Contains("hatch"));
            if (!usable || run.Flags.Contains(StoryMarks.Done(marker.Name)) || Vector2.Distance(marker.Position, hero.Position) is var d && d > UseReach)
                continue;
            near.Add((marker.Name, d));
        }
        return near.OrderBy(n => n.Distance).ThenBy(n => n.Target, StringComparer.Ordinal).Select(n => n.Target).FirstOrDefault();
    }

    /// <summary>A hero lets go of Use: a revive not finished starts again from nothing next time.</summary>
    private void LetGoOfUse(StoryRun run, PlayerId player)
    {
        if (!run.Crew.Holding.Remove(player.Value, out var what) || !what.StartsWith(SeatTarget, StringComparison.Ordinal))
            return;
        if (int.TryParse(what[SeatTarget.Length..], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id))
            run.Crew.Of(new PlayerId(id)).ReviveTicks = 0;
    }

    /// <summary>
    /// The Locator takes up the weapon on a rack (a marker tagged <c>pickup</c>, <c>pickup_&lt;weapon&gt;</c>): it goes into the
    /// first free weapon socket it fits (docs/STORY.md, Erste Gegenwehr: "eine kleine Waffe"), and the rack is done with.
    /// </summary>
    private string? TakeUp(StoryRun run, PlayerId player, DeckMarker marker)
    {
        var state = Wired!.Heroes[player];
        var weapon = marker.Name.StartsWith("pickup_", StringComparison.Ordinal) ? marker.Name["pickup_".Length..].ToUpperInvariant() : WiredBalance.StartWeapon;
        if (WiredCatalogue.KindOf(weapon) != WiredKind.Weapon)
            return "nothing to take there";
        var item = state.Add(WiredKind.Weapon, weapon, 0);
        var slot = Enum.GetValues<Mounting>().Select(WiredSlot.Weapon)
            .FirstOrDefault(s => state.Loadout.At(s) is null && state.Loadout.WhyNotFit(item, s) is null);
        if (slot is null)
            return "no socket for it";
        Wear(state, state.Loadout.With(slot, item));
        run.Flags.Add(StoryMarks.Done(marker.Name));
        return null;
    }

    // ---- crates -------------------------------------------------------------------------------------------

    /// <summary>
    /// The crates of a deck's pile still standing on it, in id order: the artefacts of the props named <c>pile_*</c> where the
    /// deck put them (one carried off into a slot is no longer the pile's).
    /// </summary>
    public List<Artefact> PileCrates()
    {
        if (Deck is not { } deck)
            return [];
        var piled = deck.Props.Where(p => p.Name.StartsWith("pile_", StringComparison.Ordinal)).ToDictionary(p => p.Id, p => p.Position);
        return _artefacts.Values
            .Where(a => !a.IsDead && a.Tag is { } tag && piled.TryGetValue(tag, out var home) && Vector2.DistanceSquared(a.Position, home) < 1)
            .OrderBy(a => a.Id).ToList();
    }

    /// <summary>The barricade slots before a door (markers <c>barricade_&lt;letter&gt;_&lt;n&gt;</c>), in order.</summary>
    public List<DeckMarker> BarricadeSlots(string door) =>
        Deck?.Markers.Where(m => m.Name.StartsWith($"barricade_{door}_", StringComparison.Ordinal)).OrderBy(m => m.Name, StringComparer.Ordinal).ToList() ?? [];

    /// <summary>Whether a crate (any artefact) stands in a slot.</summary>
    public bool SlotFilled(DeckMarker slot) => _artefacts.Values.Any(a => !a.IsDead && Vector2.DistanceSquared(a.Position, slot.Position) < 0.5f * 0.5f);

    /// <summary>A Grunt takes a crate off the pile: it leaves the map whole and goes with the Grunt.</summary>
    internal CarriedCrate? TakeCrate(int id)
    {
        if (!_artefacts.TryGetValue(id, out var crate) || crate.IsDead)
            return null;
        var carried = new CarriedCrate(crate.Placement, crate.Type, crate.SubType, crate.Tag, crate.Boundary.Select(p => p - crate.Position).ToList(), crate.Position);
        RemoveArtefact(id);
        return carried;
    }

    /// <summary>Sets a carried crate down at a point, as it stood (its id is its place in the scenario again): it blocks the way there.</summary>
    internal Artefact SetCrate(CarriedCrate crate, Vector2 at)
    {
        var artefact = AddArtefact(crate.Placement, at, crate.Type, crate.SubType, crate.Shape.Select(p => at + p).ToList());
        artefact.Tag = crate.Tag;
        _artefactsFiled = -1;
        return artefact;
    }

    /// <summary>
    /// Where a Grunt stands before a slot of a door, clear of a crate there: on the room's side of it, or with
    /// <paramref name="outside"/> on the door's (where it set the last crate down from the far side and cannot get round).
    /// The way into the room is across the door's long side, from the door's middle toward the slot.
    /// </summary>
    internal Vector2 BeforeSlot(string door, DeckMarker slot, Machine grunt, bool outside = false)
    {
        var inward = DoorInward(door, slot.Position);
        return slot.Position + inward * ((outside ? -1 : 1) * (1 + grunt.Radius + 0.75f));
    }

    /// <summary>Whether a Grunt is on the door's side of a slot rather than the room's.</summary>
    internal bool OutsideSlot(string door, DeckMarker slot, Machine grunt) =>
        Vector2.Dot(grunt.Position - slot.Position, DoorInward(door, slot.Position)) < 0;

    private readonly Dictionary<char, (Vector2 Middle, bool Wide)> _doorShapes = [];

    private Vector2 DoorInward(string door, Vector2 slot)
    {
        if (Deck is not { } deck || door.Length != 1)
            return Vector2.UnitY;
        var letter = door[0];
        if (!_doorShapes.TryGetValue(letter, out var shape))
        {
            var (minX, minY, maxX, maxY, count) = (int.MaxValue, int.MaxValue, int.MinValue, int.MinValue, 0);
            for (var y = 0; y < deck.Depth; y++)
            {
                for (var x = 0; x < deck.Width; x++)
                {
                    if (deck.At(x, y) != letter)
                        continue;
                    (minX, minY, maxX, maxY) = (Math.Min(minX, x), Math.Min(minY, y), Math.Max(maxX, x), Math.Max(maxY, y));
                    count++;
                }
            }
            shape = count == 0 ? (slot, true) : (new Vector2((minX + maxX + 1) / 2f, (minY + maxY + 1) / 2f), maxX - minX >= maxY - minY);
            _doorShapes[letter] = shape;
        }
        var away = slot - shape.Middle;
        return shape.Wide ? new Vector2(0, away.Y >= 0 ? 1 : -1) : new Vector2(away.X >= 0 ? 1 : -1, 0);
    }

    // ---- hits: downed instead of destroyed ------------------------------------------------------------------

    /// <summary>Whether a hit lands on <paramref name="target"/> in the story: a downed or dormant Grunt takes none.</summary>
    private bool StoryHit(Actor target) => !StoryInert(target);

    /// <summary>
    /// After a hit in the story: a Grunt it would destroy lies downed at one hit point instead (the same actor), and a
    /// Locator that is hit loses its hold on a Grunt it puts together, which starts again from nothing.
    /// </summary>
    private void StoryStruck(Actor target)
    {
        var run = Story!;
        if (target is not Machine machine)
            return;
        if (HeroOf(run.Locator)?.Id == machine.Id)
        {
            foreach (var companion in run.Crew.Grunts.Values)
                companion.ReviveTicks = 0;
            return;
        }
        if (machine.HitPoints > 0 || CompanionSeat(machine) is not { } seat)
            return;
        LayDown(run, machine, seat);
    }

    /// <summary>
    /// The director knocks a Grunt down (<see cref="Story.Then.KnockDown"/>): it lies downed as if a hit had destroyed it.
    /// False when the machine is no Grunt of the trio standing up.
    /// </summary>
    internal bool KnockDownForStory(int id)
    {
        if (Story is not { } run || Find<Machine>(id) is not { IsDead: false } machine || CompanionSeat(machine) is not { } seat
            || CompanionDown(seat) || CompanionDormant(seat))
            return false;
        LayDown(run, machine, seat);
        return true;
    }

    private void LayDown(StoryRun run, Machine machine, PlayerId seat)
    {
        machine.HitPoints = 1;
        machine.Armour = 0;
        machine.ArmourRegen = 0;
        run.Flags.Add(StoryMarks.Down(seat));
        var downed = run.Crew.Of(seat);
        EndJob(run, downed);
        (downed.ReviveTicks, downed.RepairTicks) = (0, 0);
        StartAfresh(machine);
        HoldStill(machine.Id);
        CompanionBark(run, seat, CompanionBarks.Cue.Down, urgent: true);
    }

    /// <summary>A downed Grunt is back: some of its hit points, its job and its order as they were before it fell (followed again).</summary>
    private void Revive(StoryRun run, PlayerId seat)
    {
        if (!run.Flags.Remove(StoryMarks.Down(seat)) || HeroOf(seat) is not { } grunt)
            return;
        grunt.HitPoints = Math.Max(grunt.HitPoints, Math.Max(1, (int)(grunt.MaxHitPoints * ReviveShare)));
        var companion = run.Crew.Of(seat);
        (companion.ReviveTicks, companion.RepairTicks) = (0, 0);
        foreach (var (player, what) in run.Crew.Holding.ToList())
        {
            if (what == SeatTarget + seat.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                run.Crew.Holding.Remove(player);
        }
        CompanionBark(run, seat, CompanionBarks.Cue.Up, urgent: true);
    }

    // ---- the tick -----------------------------------------------------------------------------------------

    /// <summary>
    /// A tick of the companions: the stuns wear off, Grunts whose terminal was hacked wake, the Locator's hold on a downed
    /// Grunt counts on, the repair station mends the trio and brings the downed back, and every
    /// <see cref="CompanionBot.ThinkEvery"/> ticks the computer's Grunts think, in seat order.
    /// </summary>
    private void UpdateCompanions()
    {
        var run = Story!;
        var crew = run.Crew;
        foreach (var (id, until) in crew.Stunned.ToList())
        {
            if (Tick >= until)
                crew.Stunned.Remove(id);
        }
        WakeTerminals(run);
        UpdateHolds(run);
        UpdateRepairStation(run);
        // A Grunt a person took over carries no crate in the computer's hands.
        foreach (var seat in run.Seats.Skip(1))
        {
            if (!StoryBotPlays(seat) && crew.Grunts.TryGetValue(seat.Value, out var companion) && companion.Carrying is not null)
                EndJob(run, companion);
        }
        if (Tick % CompanionBot.ThinkEvery != 0)
            return;
        foreach (var seat in run.Bots)
            new CompanionBot(this, seat).Think();
    }

    /// <summary>The terminal by each Grunt's clamp, in seat order: hacking it wakes that Grunt (docs/STORY.md, Erwachen).</summary>
    public static readonly IReadOnlyList<string> Terminals = ["", "terminal_ike", "terminal_bob"];

    /// <summary>A dormant Grunt whose terminal the Locator hacked wakes.</summary>
    private void WakeTerminals(StoryRun run)
    {
        for (var i = 1; i < run.Seats.Count && i < Terminals.Count; i++)
        {
            if (CompanionDormant(run.Seats[i]) && run.Hacks.Done.Contains(Terminals[i]))
                SetCompanionDormant(run.Seats[i], false);
        }
    }

    /// <summary>Holds on Use: the Locator puts a downed Grunt together while it stays in reach, and lets go of what is out of reach.</summary>
    private void UpdateHolds(StoryRun run)
    {
        foreach (var (player, what) in run.Crew.Holding.ToList())
        {
            var holder = new PlayerId(player);
            if (HeroOf(holder) is not { } hero || StoryInert(hero))
            {
                LetGoOfUse(run, holder);
                continue;
            }
            if (!what.StartsWith(SeatTarget, StringComparison.Ordinal))
            {
                if (Deck?.Markers.FirstOrDefault(m => m.Name == what) is not { } marker || Vector2.Distance(marker.Position, hero.Position) > UseReach)
                    run.Crew.Holding.Remove(player);
                continue;
            }
            var seat = new PlayerId(int.Parse(what[SeatTarget.Length..], System.Globalization.CultureInfo.InvariantCulture));
            if (!CompanionDown(seat) || HeroOf(seat) is not { } wreck || Vector2.Distance(wreck.Position, hero.Position) > ReviveReach)
            {
                LetGoOfUse(run, holder);
                continue;
            }
            HoldStill(hero.Id);
            var companion = run.Crew.Of(seat);
            if (++companion.ReviveTicks >= (int)(ReviveSeconds * TicksPerSecond))
                Revive(run, seat);
        }
    }

    /// <summary>The repair station (the deck's zone <see cref="RepairZone"/>) mends the trio standing in it, and brings a downed Grunt in it back after a while.</summary>
    private void UpdateRepairStation(StoryRun run)
    {
        if (Deck?.Zone(RepairZone) is not { } zone)
            return;
        for (var i = 0; i < run.Seats.Count; i++)
        {
            var seat = run.Seats[i];
            if (HeroOf(seat) is not { } hero || i > 0 && CompanionDormant(seat))
                continue;
            var inside = zone.Contains(hero.Position);
            if (i > 0 && CompanionDown(seat))
            {
                var companion = run.Crew.Of(seat);
                companion.RepairTicks = inside ? companion.RepairTicks + 1 : 0;
                if (companion.RepairTicks >= (int)(RepairReviveSeconds * TicksPerSecond))
                    Revive(run, seat);
                continue;
            }
            if (!inside)
                continue;
            AddHitPoints(hero, hero.MaxHitPoints * WiredBalance.FreeRepair * Dt);
            if (hero.Armour < hero.MaxArmour)
                hero.ArmourRegen += hero.MaxArmour * WiredBalance.FreeRepair * Dt;
        }
    }

    // ---- barks --------------------------------------------------------------------------------------------

    /// <summary>The least time between two barks of one Grunt, unless the bark is urgent.</summary>
    public const float BarkGap = 4;

    /// <summary>
    /// A Grunt the computer plays says a line over the radio (<see cref="StoryRun.Barks"/>, which the director makes lines of);
    /// a Grunt a person plays says nothing. Not within <see cref="BarkGap"/> of its last unless <paramref name="urgent"/>; a
    /// line said <paramref name="once"/> is never said again.
    /// </summary>
    internal void CompanionBark(StoryRun run, PlayerId seat, CompanionBarks.Cue cue, bool urgent = false, bool once = false)
    {
        var index = run.Seats.IndexOf(seat);
        if (index < 1 || !StoryBotPlays(seat) || CompanionBarks.Line(cue, index) is not { } line)
            return;
        var companion = run.Crew.Of(seat);
        if (!urgent && Tick - companion.LastBark < BarkGap * TicksPerSecond)
            return;
        if (once && !companion.Said.Add(line))
            return;
        companion.LastBark = Tick;
        run.Crew.Bark(new StoryBark(line, CompanionBarks.Speaker(index), Tick, seat));
    }
}
