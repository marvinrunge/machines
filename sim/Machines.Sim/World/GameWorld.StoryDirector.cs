using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Story;

namespace Machines.Sim.World;

/// <summary>
/// What the story's director (<see cref="StoryDirector"/>, plan WP-C) does to the world through it: orders given as the
/// world gives its own, heroes held still, machines and artefacts set down. Only a story's director calls any of it.
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>A command the director gives, applied at once as one of the world's own (as Wired's waves get their orders).</summary>
    internal string? DirectForStory(Command command)
    {
        var outer = _commandId;
        _commandId = ++_commands;
        var refused = Apply(command);
        _commandId = outer;
        return refused;
    }

    /// <summary>An order the director gives one machine, as the world's own.</summary>
    internal void SendForStory(Machine machine, Order order, Vector2? destination)
    {
        var outer = _commandId;
        _commandId = ++_commands;
        Send(machine, order, destination);
        _commandId = outer;
    }

    /// <summary>
    /// Whether the director holds a machine still (the Locator in its clamp, a Grunt asleep in its own, the Locator while the
    /// core's alarm plays): its driver may look about, but neither drive nor fire.
    /// </summary>
    public bool HeldByStory(int machine) => Story?.Director.Held.Contains(machine) == true;

    /// <summary>A held machine stands: its wheels let go, no trigger held; one the computer moves has nothing to do.</summary>
    internal void HoldForStory(int id)
    {
        if (Find<Machine>(id) is not { IsDead: false } machine)
            return;
        HoldStill(id);
        if (!machine.DrivenByPlayer && machine.Order is not (IdleOrder or StandGroundOrder))
            StartAfresh(machine);
    }

    /// <summary>
    /// A machine of the ship set down for the story at the nearest free ground to <paramref name="at"/> (or exactly there,
    /// for one hanging in a clamp), of the type, level and weapons named.
    /// </summary>
    internal Machine SpawnForStory(Race race, Vector2 at, StoryUnit unit, bool exact = false)
    {
        var spec = Data.Parm.Machine(unit.SubType is null ? unit.Type : $"{unit.Type} {unit.SubType}");
        var clearance = MathF.Max(1, spec.Level(unit.Level).LowClearance);
        return AddMachine(race, exact ? at : FreeSpotNear(at, clearance), unit.Type, unit.SubType, unit.Level, unit.Weapons);
    }

    /// <summary>A hero of the story set down at a place for a checkpoint, facing the way given (degrees, 0 east, 90 north), standing.</summary>
    internal void SetHeroForStory(Machine hero, Vector2 at, float degrees)
    {
        StartAfresh(hero);
        hero.Position = at;
        hero.Facing = degrees * MathF.PI / 180;
    }

    /// <summary>
    /// Sets an artefact of a type the chapter's scenario has down at a place, turned by degrees (debris falling, a crate set
    /// down): its ground is blocked as a scenario's is, and the view hears a cue to show it
    /// (<c>artefact &lt;placement&gt; &lt;type&gt; &lt;x&gt; &lt;y&gt; &lt;degrees&gt;</c>). Null when the scenario has no such type.
    /// </summary>
    public Artefact? PlaceArtefact(string typeName, Vector2 at, float degrees)
    {
        if (Story is not { } run || run.Director.TypeOf(typeName) is not { } known)
            return null;
        var (sin, cos) = (DetMath.Sin(degrees * MathF.PI / 180), DetMath.Cos(degrees * MathF.PI / 180));
        var boundary = known.Type.Boundary.Select(p => at + new Vector2(p.X * cos - p.Y * sin, p.X * sin + p.Y * cos)).ToList();
        var placement = StoryDirector.PlacedBase + run.Director.Placed++;
        var artefact = AddArtefact(placement, at, known.Type, known.SubType, boundary);
        _artefactsFiled = -1;
        StoryEvents.Cue(this, FormattableString.Invariant($"artefact {placement} {typeName} {at.X} {at.Y} {degrees}"), at);
        return artefact;
    }

    /// <summary>
    /// Hands the director what the sight and the companions noted on this tick (<see cref="StoryDirector.Hear"/>): each
    /// seeker's news (<see cref="StorySight.News"/>) and each bark of the computer's Grunts (<see cref="StoryRun.Barks"/>), in
    /// the order they were noted. A seeker that searches on a sound alone counts as suspicious at its full suspicion; one
    /// that calmed down as suspicious at none.
    /// </summary>
    private void TellDirector(StoryRun run)
    {
        foreach (var news in run.Sight.News)
        {
            if (news.Tick != Tick)
                continue;
            PlayerId? seat = null;
            foreach (var each in run.Seats)
            {
                if (Wired?.Heroes.GetValueOrDefault(each)?.Hero == news.Hero && news.Hero != 0)
                    seat = each;
            }
            var kind = news.Kind switch
            {
                StorySightKind.Spotted => StoryNewsKind.Spotted,
                StorySightKind.Lost => StoryNewsKind.Lost,
                _ => StoryNewsKind.Suspicious,
            };
            var level = news.Kind == StorySightKind.Calm ? 0 : news.Suspicion;
            run.Director.Hear(new StoryNews(kind, news.Seeker, seat, news.Where, Level: level));
        }
        foreach (var bark in run.Barks)
        {
            if (bark.Tick != Tick)
                continue;
            var speaker = HeroOf(bark.Seat);
            run.Director.Hear(new StoryNews(StoryNewsKind.Bark, speaker?.Id ?? 0, bark.Seat, speaker?.Position ?? default, bark.Line));
        }
    }

    /// <summary>
    /// The weapon on a rack (a marker tagged <c>pickup</c>) taken up by a hero as if it had used the rack (a checkpoint after the
    /// Bolter was found): the hero wears it and the rack is done with. Null when it took it, otherwise why not.
    /// </summary>
    internal string? TakeUpForStory(PlayerId player, string rack)
    {
        if (Story is not { } run || Deck?.Markers.FirstOrDefault(m => m.Name == rack && m.Tags.Contains("pickup")) is not { } marker)
            return "no such rack";
        if (run.Flags.Contains(StoryMarks.Done(rack)))
            return "that is done with";
        return Wired?.Heroes.ContainsKey(player) == true ? TakeUp(run, player, marker) : "not one of the trio";
    }

    /// <summary>A hero of the story kept from harm until a game time (the Locator while the core's alarm plays out).</summary>
    internal void SpareHeroUntil(PlayerId seat, float until)
    {
        if (Wired?.Heroes.GetValueOrDefault(seat) is { } state)
            state.InvulnerableUntil = MathF.Max(state.InvulnerableUntil, until);
    }
}
