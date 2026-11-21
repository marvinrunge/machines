using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Sim.Ai;

/// <summary>
/// The computer's mind for a Grunt of the story (Ike or Bob; plan WP-F): it plays a seat of <see cref="StoryRun.Bots"/>, a
/// bot's from the start or one whose person left, and a person who joins takes the seat over as it stands. It carries out
/// the job the story gave the Grunt (barricade a door, hold a switch, board the pod), else the Locator's last order (follow,
/// wait, distract), and rams an enemy that fires at the trio when it comes near. All it knows lies in the run's
/// <see cref="StoryCrew"/>, so it keeps nothing of its own: it looks at the world inside the world's tick every
/// <see cref="ThinkEvery"/> ticks, as a battle's bot does, and acts on the world directly, which every peer does alike.
/// </summary>
public sealed class CompanionBot(GameWorld world, PlayerId seat)
{
    /// <summary>The ticks between two looks at the world.</summary>
    public const int ThinkEvery = 10;

    /// <summary>How near an enemy firing at the trio must be for a following Grunt to go and ram it.</summary>
    public const float FightReach = 14;

    /// <summary>How near the distraction's point, the pile or a slot counts as there.</summary>
    private const float There = 3;

    /// <summary>How far behind the Locator a following Grunt may fall where it does not fit before it says so.</summary>
    private const float LeftBehind = 16;

    /// <summary>How long a honk lasts, and how long a trip of a job may take before the Grunt gives the job up.</summary>
    private const float HonkSeconds = 2, TripSeconds = 40;

    public PlayerId Seat => seat;

    /// <summary>One look at the world.</summary>
    public void Think()
    {
        if (world.Story is not { Over: false } run || !world.StoryBotPlays(seat) || world.HeroOf(seat) is not { } grunt
            || world.CompanionDown(seat) || world.CompanionDormant(seat))
            return;
        // Unarmed and told to stay where it is put: it does not run from fire.
        grunt.DefCon = DefCon.High;
        var companion = run.Crew.Of(seat);
        switch (companion.Task)
        {
            case CompanionTask.Barricade:
                Barricade(run, companion, grunt);
                return;
            case CompanionTask.HoldSwitch:
                HoldSwitch(run, companion, grunt);
                return;
            case CompanionTask.Board:
                Board(run, companion, grunt);
                return;
            case CompanionTask.Go:
                GoTo(run, companion, grunt);
                return;
        }
        switch (companion.Order)
        {
            case CompanionOrder.Distract:
                Distract(run, companion, grunt);
                break;
            case CompanionOrder.Wait:
                world.CarryOut(run, companion, grunt);
                RamIfNear(run, grunt);
                break;
            default:
                Follow(run, companion, grunt);
                break;
        }
    }

    /// <summary>Keeps with the Locator; goes for an enemy that fires at the trio near it, and rams it once it is close.</summary>
    private void Follow(StoryRun run, Companion companion, Machine grunt)
    {
        if (RamIfNear(run, grunt))
            return;
        if (companion.RamReadyAt <= world.Tick && world.CompanionFoe(run, grunt, FightReach) is { } foe)
        {
            // To within its ram's reach on this side of the foe, not onto it: a foe that hunts is on the move, and one heading
            // onto a machine that moves waits for it to get out of the way (GameWorld.HeldUp).
            var away = grunt.Position - foe.Position;
            var reach = GameWorld.RamDistance(grunt, foe) - 0.5f;
            world.CompanionMove(grunt, away.LengthSquared() > reach * reach ? foe.Position + Vector2.Normalize(away) * reach : grunt.Position);
            return;
        }
        world.CarryOut(run, companion, grunt);
        // The Locator crawled where a Grunt does not fit: Ike says so, once.
        if (world.HeroOf(run.Locator) is { } locator && world.Deck?.CellAt(locator.Position) is DeckCell.Duct or DeckCell.Lip
            && Vector2.Distance(locator.Position, grunt.Position) > LeftBehind)
            world.CompanionBark(run, seat, CompanionBarks.Cue.Vent, once: true);
    }

    /// <summary>Rams the enemy firing at the trio that is within its ram's reach, if any and the ram is ready.</summary>
    private bool RamIfNear(StoryRun run, Machine grunt)
    {
        if (run.Crew.Of(seat).RamReadyAt > world.Tick || world.CompanionFoe(run, grunt, FightReach) is not { } foe
            || Vector2.Distance(foe.Position, grunt.Position) > GameWorld.RamDistance(grunt, foe))
            return false;
        return world.RamWith(run, seat, foe.Id) is null;
    }

    /// <summary>Off to the point, a noise there, back to where it was told, and there it waits.</summary>
    private void Distract(StoryRun run, Companion companion, Machine grunt)
    {
        switch (companion.Step)
        {
            case CompanionStep.Going:
                if (Vector2.Distance(grunt.Position, companion.Point) <= There || GameWorld.Arrived(grunt) || world.Tick >= companion.StepUntil)
                {
                    world.CompanionStand(grunt);
                    world.Honk(run, grunt);
                    companion.Step = CompanionStep.Honking;
                    companion.StepUntil = world.Tick + (int)(HonkSeconds * GameWorld.TicksPerSecond);
                }
                else
                    world.CompanionMove(grunt, companion.Point);
                break;
            case CompanionStep.Honking:
                if (world.Tick < companion.StepUntil)
                    break;
                companion.Step = CompanionStep.Fleeing;
                companion.StepUntil = world.Tick + (int)(TripSeconds * GameWorld.TicksPerSecond);
                world.CompanionMove(grunt, companion.Back);
                break;
            case CompanionStep.Fleeing:
                if (Vector2.Distance(grunt.Position, companion.Back) <= There || GameWorld.Arrived(grunt) || world.Tick >= companion.StepUntil)
                {
                    companion.Order = CompanionOrder.Wait;
                    companion.Step = CompanionStep.None;
                    run.Flags.Remove(StoryMarks.Distracting(seat));
                    world.CarryOut(run, companion, grunt);
                }
                else
                    world.CompanionMove(grunt, companion.Back);
                break;
            default:
                world.CarryOut(run, companion, grunt);
                break;
        }
    }

    /// <summary>
    /// Carries crates from the pile into the free slots before the door, one at a time, the two Grunts never after the same
    /// crate or slot; once the slots are full, the pile is empty or out of its reach (behind the crates it set down) it stands
    /// its ground before the last slot it filled.
    /// </summary>
    private void Barricade(StoryRun run, Companion companion, Machine grunt)
    {
        var door = companion.Place ?? "";
        var slots = world.BarricadeSlots(door);
        if (slots.Count == 0)
        {
            world.AssignCompanion(seat, CompanionTask.None);
            return;
        }
        switch (companion.Step)
        {
            case CompanionStep.ToPile:
            {
                var crate = world.PileCrates().FirstOrDefault(c => c.Id == companion.Crate);
                if (crate is null || world.Tick >= companion.StepUntil)
                {
                    (companion.Step, companion.Crate) = (CompanionStep.None, null);
                    break;
                }
                if (Vector2.Distance(crate.Position, grunt.Position) <= crate.Radius + grunt.Radius + There)
                {
                    companion.Carrying = world.TakeCrate(crate.Id);
                    companion.Crate = null;
                    companion.Step = CompanionStep.ToSlot;
                    companion.StepUntil = world.Tick + (int)(TripSeconds * GameWorld.TicksPerSecond);
                    if (slots.FirstOrDefault(s => s.Name == companion.Slot) is { } slot)
                        world.CompanionMove(grunt, world.BeforeSlot(door, slot, grunt));
                }
                else
                    world.CompanionMove(grunt, Beside(crate, grunt));
                return;
            }
            case CompanionStep.ToSlot:
            {
                if (companion.Carrying is not { } carried || slots.FirstOrDefault(s => s.Name == companion.Slot) is not { } slot || world.SlotFilled(slot))
                {
                    // The slot was filled by another: the crate goes into the next free one, or back on the pile.
                    if (companion.Carrying is { } held)
                    {
                        if (FreeSlot(run, slots) is { } other)
                        {
                            companion.Slot = other.Name;
                            world.CompanionMove(grunt, world.BeforeSlot(door, other, grunt));
                            return;
                        }
                        world.SetCrate(held, held.From);
                        companion.Carrying = null;
                    }
                    companion.Step = CompanionStep.None;
                    break;
                }
                // It sets the crate down from the room's side where it gets there, else from as near as its way goes.
                var before = world.BeforeSlot(door, slot, grunt);
                var near = GameWorld.Arrived(grunt) || Vector2.Distance(grunt.Position, before) <= 1.5f;
                if (near && Vector2.Distance(grunt.Position, slot.Position) <= grunt.Radius + 2 + There && !Occupied(slot.Position, carried, grunt))
                {
                    world.SetCrate(carried, slot.Position);
                    companion.Carrying = null;
                    companion.Step = CompanionStep.None;
                    break;
                }
                if (world.Tick >= companion.StepUntil)
                {
                    world.AssignCompanion(seat, CompanionTask.None);
                    world.CarryOut(run, companion, grunt);
                    return;
                }
                world.CompanionMove(grunt, before);
                return;
            }
            case CompanionStep.Standing:
                world.CompanionStand(grunt);
                return;
        }
        // Between trips: the next crate into the next free slot, or stand.
        if (FreeSlot(run, slots) is { } free && FreeCrate(run, grunt, free) is { } next)
        {
            companion.Slot = free.Name;
            companion.Crate = next.Id;
            companion.Step = CompanionStep.ToPile;
            companion.StepUntil = world.Tick + (int)(TripSeconds * GameWorld.TicksPerSecond);
            world.CompanionMove(grunt, Beside(next, grunt));
            return;
        }
        // The work done: before the slot it filled last, on the side it is on (the room's, unless it set the last crate down
        // from the door's side and cannot get round), clear of the crates.
        var stand = slots.FirstOrDefault(s => s.Name == companion.Slot) ?? slots[Math.Min(run.Seats.IndexOf(seat), slots.Count) - 1];
        if (companion.Step != CompanionStep.ToStand)
        {
            companion.Slot = stand.Name;
            companion.Step = CompanionStep.ToStand;
            companion.StepUntil = world.Tick + (int)(StandSeconds * GameWorld.TicksPerSecond);
        }
        var spot = world.BeforeSlot(door, stand, grunt, world.OutsideSlot(door, stand, grunt));
        if (Vector2.Distance(grunt.Position, spot) > 1.5f && world.Tick < companion.StepUntil)
        {
            world.CompanionMove(grunt, spot);
            return;
        }
        companion.Step = CompanionStep.Standing;
        world.CompanionStand(grunt);
        world.CompanionBark(run, seat, CompanionBarks.Cue.Barricaded, urgent: true);
    }

    /// <summary>How long a Grunt tries to get to its place before the crates before it stands where it got to.</summary>
    private const float StandSeconds = 10;

    /// <summary>The first slot with no crate in it that no other Grunt carries one to.</summary>
    private DeckMarker? FreeSlot(StoryRun run, List<DeckMarker> slots) =>
        slots.FirstOrDefault(s => !world.SlotFilled(s)
            && !run.Crew.Grunts.Values.Any(c => c.Seat != seat && c.Task == CompanionTask.Barricade && c.Slot == s.Name
                && c.Step is CompanionStep.ToPile or CompanionStep.ToSlot));

    /// <summary>
    /// The crate nearest the slot (the loose ones by the door first, then the pile) that no other Grunt goes for and this one
    /// can get to: once two crates stand in a doorway the Grunts behind them cannot get back to the pile, and the barricade
    /// is what they built.
    /// </summary>
    private Artefact? FreeCrate(StoryRun run, Machine grunt, DeckMarker slot) =>
        world.PileCrates().OrderBy(crate => Vector2.DistanceSquared(crate.Position, slot.Position)).ThenBy(crate => crate.Id)
            .FirstOrDefault(crate => !run.Crew.Grunts.Values.Any(c => c.Seat != seat && c.Crate == crate.Id)
                && world.CompanionReaches(grunt, crate.Position, crate.Radius + grunt.Radius + There));

    /// <summary>A point beside a crate on the Grunt's side, where it can take it.</summary>
    private static Vector2 Beside(Artefact crate, Machine grunt)
    {
        var away = grunt.Position - crate.Position;
        away = away.LengthSquared() > 1e-4f ? Vector2.Normalize(away) : Vector2.UnitY;
        return crate.Position + away * (crate.Radius + grunt.Radius + 0.5f);
    }

    /// <summary>Whether a machine other than the Grunt stands where a crate would go.</summary>
    private bool Occupied(Vector2 at, CarriedCrate crate, Machine grunt)
    {
        var size = crate.Shape.Count > 0 ? crate.Shape.Max(p => p.Length()) : 1;
        return world.Machines.Any(m => m != grunt && !m.IsDead && m.Inside is null && Vector2.Distance(m.Position, at) < size + m.Radius);
    }

    /// <summary>Drives onto the switch and stands on it; says so once it holds.</summary>
    private void HoldSwitch(StoryRun run, Companion companion, Machine grunt)
    {
        if (world.Deck is not { } deck || deck.Markers.FirstOrDefault(m => m.Name == companion.Place) is not { } marker)
        {
            world.AssignCompanion(seat, CompanionTask.None);
            return;
        }
        var zone = deck.Zone(marker.Name + "_zone");
        var spot = zone is null ? marker.Position : (zone.Min + zone.Max) / 2;
        var on = zone?.Contains(grunt.Position) ?? Vector2.Distance(grunt.Position, marker.Position) <= GameWorld.UseReach;
        if (!on)
        {
            companion.Step = CompanionStep.None;
            world.CompanionMove(grunt, spot);
            return;
        }
        world.CompanionStand(grunt);
        if (companion.Step == CompanionStep.Standing)
            return;
        companion.Step = CompanionStep.Standing;
        world.CompanionBark(run, seat, CompanionBarks.Cue.Gate, urgent: true);
    }

    /// <summary>
    /// To the job's place by its own way (the cable duct while the Locator crawls ahead), and waits there; on the way and
    /// there it rams an enemy firing at the trio that comes within its ram's reach.
    /// </summary>
    private void GoTo(StoryRun run, Companion companion, Machine grunt)
    {
        if (StoryMarks.Locate(world.Deck, companion.Place) is not { } spot)
        {
            world.AssignCompanion(seat, CompanionTask.None);
            return;
        }
        if (RamIfNear(run, grunt))
            return;
        // Once there (or as near as its way goes) it stays unless pushed well off.
        var off = Vector2.Distance(grunt.Position, spot);
        var stays = companion.Step == CompanionStep.Standing && off <= 3 * There || companion.Step == CompanionStep.Going && GameWorld.Arrived(grunt);
        if (off > There && !stays)
        {
            companion.Step = CompanionStep.Going;
            world.CompanionMove(grunt, spot);
            return;
        }
        world.CompanionStand(grunt);
        companion.Step = CompanionStep.Standing;
    }

    /// <summary>Follows the Locator into the escape pod: to its place in the pod's zone beside the hatch, and stays there.</summary>
    private void Board(StoryRun run, Companion companion, Machine grunt)
    {
        if (world.Deck is not { } deck || deck.Zone(GameWorld.PodZone) is not { } pod)
        {
            world.AssignCompanion(seat, CompanionTask.None);
            return;
        }
        var hatch = deck.Markers.FirstOrDefault(m => m.Name == companion.Place)?.Position ?? (pod.Min + pod.Max) / 2;
        var side = run.Seats.IndexOf(seat) == 1 ? -1 : 1;
        var spot = hatch + new Vector2(4 * side, 3);
        if (!pod.Contains(grunt.Position) || Vector2.Distance(grunt.Position, spot) > There && !GameWorld.Arrived(grunt))
        {
            companion.Step = CompanionStep.None;
            world.CompanionMove(grunt, spot);
            return;
        }
        world.CompanionStand(grunt);
        if (companion.Step == CompanionStep.Standing)
            return;
        companion.Step = CompanionStep.Standing;
        // Ike urges the others in ("In, in, in!"): the last of the trio aboard launches the pod, and his urging then came
        // as it dropped. Bob only says he is aboard.
        var urges = run.Seats.IndexOf(seat) == 1;
        var everyone = world.CompanionsAboard() && world.HeroOf(run.Locator) is { } locator && pod.Contains(locator.Position);
        if (!urges || !everyone)
            world.CompanionBark(run, seat, CompanionBarks.Cue.Pod, urgent: true);
    }
}
