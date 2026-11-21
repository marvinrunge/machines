using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Sim.Story;

/// <summary>Whose presence a trigger asks for: the Locator, any Grunt, any of the trio, or all of the trio up and about.</summary>
public enum StoryWho
{
    Locator,
    AnyCompanion,
    AnyHero,
    /// <summary>Every hero of the trio that lives and is not down (a Grunt down is waited for by nobody).</summary>
    All,
}

/// <summary>
/// One beat of a chapter's script: when its trigger holds (and the beats it comes after have fired), its actions are done,
/// once; a beat that repeats fires again each time it holds, at most every <paramref name="Every"/> seconds. Beats are
/// looked at in their order every tick, so one that fires can let the next fire in the same tick.
/// </summary>
/// <param name="Span">The checkpoint whose stretch of the chapter the beat belongs to: a chapter started from a later checkpoint counts it as fired.</param>
public sealed record StoryBeat(string Name, int Span, StoryTrigger When, IReadOnlyList<StoryAction> Then, IReadOnlyList<string>? After = null,
    bool Repeat = false, float Every = 0);

/// <summary>
/// The story's director (plan WP-C): the chapter's script (its <see cref="StoryBeat"/>s, e.g. <see cref="Prologue"/>), run
/// on the world's ticks, and what it keeps between them: which beats fired and when, the machines it cast by their roles,
/// who it holds still, the doors that open as the trio comes, the artefacts it keeps whole and how far each
/// hack had come. Everything it does to the world it does on ticks, so co-op peers, recordings and a skipped cutscene agree;
/// the view only hears its events (<see cref="StoryEvents"/>). All of it is in the checksum (<see cref="Fingerprint"/>).
/// <para>The sight (plan WP-D) and the companions (plan WP-F) tell it their news through <see cref="Hear"/>.</para>
/// </summary>
public sealed class StoryDirector
{
    /// <summary>How near a hero of the trio must come to a door that opens by itself, in metres from the door's middle.</summary>
    public const float DoorReach = 4.5f;

    /// <summary>The placements of artefacts the director sets down are counted from here (the scenario's are far fewer).</summary>
    public const int PlacedBase = 10_000;

    private readonly List<StoryNews> _inbox = [];
    private readonly Dictionary<string, (ArtefactType Type, int SubType)> _types = new(StringComparer.Ordinal);

    public StoryDirector(IReadOnlyList<StoryBeat> script) => Script = script;

    /// <summary>The chapter's beats, in the order they are looked at.</summary>
    public IReadOnlyList<StoryBeat> Script { get; }

    /// <summary>The beats that fired, by name, with the game time each last fired.</summary>
    public SortedDictionary<string, float> Fired { get; } = new(StringComparer.Ordinal);

    /// <summary>The machines the director set down, by their role in the chapter (<c>thresher</c>, <c>reaper</c> ...), in id order.</summary>
    public SortedDictionary<string, List<int>> Cast { get; } = new(StringComparer.Ordinal);

    /// <summary>Machines held still: neither their driver's keys nor the computer move them (the Locator in its clamp, a sleeping Grunt).</summary>
    public SortedSet<int> Held { get; } = [];

    /// <summary>The deck's doors that open by themselves once a hero of the trio comes near, by letter.</summary>
    public SortedSet<char> AutoDoors { get; } = [];

    /// <summary>Artefacts (by id) that take no lasting harm until the director lets them go: the core until its scripted end.</summary>
    public SortedSet<int> Shielded { get; } = [];

    /// <summary>The director's counters (which line of a cycle comes next ...), by name.</summary>
    public SortedDictionary<string, int> Counters { get; } = new(StringComparer.Ordinal);

    /// <summary>How far each hack had come when last looked at, for its events.</summary>
    public SortedDictionary<string, (int Stage, bool Open, bool Done, int Alarms, bool Broken)> Seen { get; } = new(StringComparer.Ordinal);

    /// <summary>How many artefacts the director has set down.</summary>
    public int Placed { get; internal set; }

    /// <summary>A beat has fired.</summary>
    public bool Has(string beat) => Fired.ContainsKey(beat);

    /// <summary>The machines of a role that still stand, in id order.</summary>
    public IEnumerable<Machine> Of(GameWorld world, string role) =>
        Cast.TryGetValue(role, out var ids) ? ids.Select(world.Find<Machine>).OfType<Machine>().Where(m => !m.IsDead) : [];

    /// <summary>
    /// News from the sight or the companions, heard on this tick: it becomes its event at the director's next look in the same
    /// tick (the director ticks after the machines and the reactions). The world hands it the sight's StorySightNews and
    /// the companions' barks of each tick (GameWorld.TellDirector) just before it looks.
    /// </summary>
    public void Hear(StoryNews news) => _inbox.Add(news);

    /// <summary>The artefact types of the chapter's scenario by name, for what the director sets down; learnt as the chapter starts.</summary>
    internal void LearnTypes(IEnumerable<Artefact> artefacts)
    {
        foreach (var artefact in artefacts.OrderBy(a => a.Id))
            _types.TryAdd(artefact.Type.Name, (artefact.Type, artefact.SubType));
    }

    internal (ArtefactType Type, int SubType)? TypeOf(string name) => _types.TryGetValue(name, out var known) ? known : null;

    // ---- a tick ------------------------------------------------------------------------------------

    /// <summary>
    /// A tick of the chapter: the news heard become events, the hacks' steps become events, the doors near the trio open, the
    /// shielded artefacts mend, the held stand, then the script's beats are looked at in order.
    /// </summary>
    internal void Update(GameWorld world, StoryRun run)
    {
        foreach (var news in _inbox)
            StoryEvents.News(world, news);
        _inbox.Clear();
        WatchHacks(world, run);
        OpenNearDoors(world, run);
        foreach (var id in Shielded)
            if (world.FindTarget(id) is { IsDead: false } kept)
                kept.HitPoints = kept.MaxHitPoints;
        foreach (var id in Held)
            world.HoldForStory(id);
        foreach (var beat in Script)
        {
            if (run.Over)
                break;
            if (!Due(beat, world.Time) || beat.After is { } after && !after.All(Has) || !beat.When.Holds(world, run, this))
                continue;
            Fired[beat.Name] = world.Time;
            foreach (var action in beat.Then)
                action.Do(world, run, this);
        }
    }

    private bool Due(StoryBeat beat, float now) =>
        !Fired.TryGetValue(beat.Name, out var last) || beat.Repeat && now - last >= beat.Every;

    /// <summary>Counts every beat of a stretch before <paramref name="checkpoint"/> as fired (at the start), as the checkpoint's world already shows them done.</summary>
    internal void SkipTo(int checkpoint)
    {
        foreach (var beat in Script.Where(b => b.Span < checkpoint && !b.Repeat))
            Fired[beat.Name] = 0;
    }

    private void WatchHacks(GameWorld world, StoryRun run)
    {
        foreach (var (target, session) in run.Hacks.Sessions)
        {
            var now = (session.Stage, session.Open, Done: run.Hacks.Done.Contains(target), session.Alarms, session.Broken);
            var before = Seen.GetValueOrDefault(target);
            if (now == before)
                continue;
            Seen[target] = now;
            var at = run.Hacks.Find(target, world.Deck)?.Position ?? Vector2.Zero;
            var hacker = session.Hacker ?? run.Locator;
            if (now.Done && !before.Done)
                StoryEvents.Hack(world, "done", target, at, hacker);
            else if (now.Alarms > before.Alarms)
                StoryEvents.Hack(world, "traced", target, at, hacker);
            else if (now.Broken && !before.Broken)
                StoryEvents.Hack(world, "broken", target, at, hacker);
            else if (now.Stage > before.Stage)
                StoryEvents.Hack(world, "board", target, at, hacker);
            else if (now.Open && !before.Open)
                StoryEvents.Hack(world, "open", target, at, hacker);
            else if (!now.Open && before.Open)
                StoryEvents.Hack(world, "left", target, at, hacker);
        }
    }

    private void OpenNearDoors(GameWorld world, StoryRun run)
    {
        if (AutoDoors.Count == 0)
            return;
        var heroes = StoryTrigger.Heroes(world, run, StoryWho.AnyHero).ToList();
        foreach (var letter in AutoDoors.ToList())
        {
            var leaves = world.DoorLeaves(letter);
            if (leaves.Count == 0)
            {
                AutoDoors.Remove(letter);
                continue;
            }
            var middle = leaves.Aggregate(Vector2.Zero, (sum, leaf) => sum + leaf.Position) / leaves.Count;
            if (heroes.Any(h => Vector2.Distance(h.Position, middle) <= DoorReach + h.Radius))
            {
                world.OpenDoor(letter);
                AutoDoors.Remove(letter);
                StoryEvents.Cue(world, $"door {letter}", middle);
            }
        }
    }

    // ---- the checksum ---------------------------------------------------------------------------------

    /// <summary>A fingerprint of what the director keeps, for the world's checksum.</summary>
    public long Fingerprint()
    {
        var hash = new WiredHash();
        foreach (var (beat, at) in Fired)
        {
            hash.Mix(beat);
            hash.Mix(BitConverter.SingleToInt32Bits(at));
        }
        foreach (var (role, ids) in Cast)
        {
            hash.Mix(role);
            foreach (var id in ids)
                hash.Mix(id);
        }
        foreach (var id in Held)
            hash.Mix(id);
        foreach (var letter in AutoDoors)
            hash.Mix(letter);
        foreach (var id in Shielded)
            hash.Mix(id);
        foreach (var (name, count) in Counters)
        {
            hash.Mix(name);
            hash.Mix(count);
        }
        foreach (var (target, seen) in Seen)
        {
            hash.Mix(target);
            hash.Mix(seen.Stage);
            hash.Mix((seen.Open ? 1 : 0) | (seen.Done ? 2 : 0) | (seen.Broken ? 4 : 0));
            hash.Mix(seen.Alarms);
        }
        hash.Mix(Placed);
        return hash.Value;
    }
}

// ---- triggers ------------------------------------------------------------------------------------------

/// <summary>When a beat fires. Each looks at the world as the tick left it; none changes anything.</summary>
public abstract record StoryTrigger
{
    internal abstract bool Holds(GameWorld world, StoryRun run, StoryDirector director);

    /// <summary>The heroes of the trio a trigger counts for <paramref name="who"/>: living ones, and for <see cref="StoryWho.All"/> none that is down.</summary>
    internal static IEnumerable<Machine> Heroes(GameWorld world, StoryRun run, StoryWho who)
    {
        for (var i = 0; i < run.Seats.Count; i++)
        {
            if (who == StoryWho.Locator && i > 0 || who == StoryWho.AnyCompanion && i == 0)
                continue;
            var seat = run.Seats[i];
            if (run.Flags.Contains(StoryMarks.Dormant(seat)) || who == StoryWho.All && run.Flags.Contains(StoryMarks.Down(seat)))
                continue;
            if (world.HeroOf(seat) is { } hero)
                yield return hero;
        }
    }

    /// <summary>Whether the heroes <paramref name="who"/> asks for are where <paramref name="inside"/> says: all of them (and at least the Locator) for <see cref="StoryWho.All"/>, any one otherwise.</summary>
    internal static bool Present(GameWorld world, StoryRun run, StoryWho who, Func<Machine, bool> inside)
    {
        if (who != StoryWho.All)
            return Heroes(world, run, who).Any(inside);
        if (world.HeroOf(run.Locator) is null)
            return false;
        return Heroes(world, run, who).All(inside);
    }
}

/// <summary>The triggers a chapter's beats are written with (<c>using static Machines.Sim.Story.When;</c>).</summary>
public static class When
{
    /// <summary>Holds always: a beat that only waits for the beats it comes after.</summary>
    public sealed record Always : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) => true;
    }

    /// <summary>Heroes of the trio inside a zone of the deck.</summary>
    public sealed record InZone(string Zone, StoryWho Who = StoryWho.Locator) : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) =>
            world.Deck?.Zone(Zone) is { } zone && Present(world, run, Who, h => zone.Contains(h.Position));
    }

    /// <summary>Heroes of the trio within a radius of a place (a marker, a zone's middle or "x,y").</summary>
    public sealed record Near(string Place, float Radius, StoryWho Who = StoryWho.Locator) : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) =>
            StoryMarks.Locate(world.Deck, Place) is { } at && Present(world, run, Who, h => Vector2.Distance(h.Position, at) <= Radius);
    }

    /// <summary>A hack is won.</summary>
    public sealed record HackDone(string Target) : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) => run.Hacks.Done.Contains(Target);
    }

    /// <summary>A hack has come at least this far (0 to 1, its boards counted), or was broken off.</summary>
    public sealed record HackAt(string Target, float Share) : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) =>
            run.Hacks.Done.Contains(Target) || run.Hacks.Session(Target) is { } session && (session.Broken || session.Progress >= Share);
    }

    /// <summary>Someone is at a hack.</summary>
    public sealed record HackOpen(string Target) : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) => run.Hacks.Session(Target) is { Open: true };
    }

    /// <summary>A beat fired at least so many seconds ago.</summary>
    public sealed record Since(string Beat, float Seconds) : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) =>
            director.Fired.TryGetValue(Beat, out var at) && world.Time - at >= Seconds;
    }

    /// <summary>A flag of the run is set (the director's own, or one the companions or the sight set).</summary>
    public sealed record Flagged(string Flag) : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) => run.Flags.Contains(Flag);
    }

    /// <summary>Every machine of a role the director cast is gone (and it cast at least one).</summary>
    public sealed record Dead(string Role) : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) =>
            director.Cast.TryGetValue(Role, out var ids) && ids.Count > 0 && !director.Of(world, Role).Any();
    }

    /// <summary>A machine of a role the director cast is within a radius of a place.</summary>
    public sealed record CastNear(string Role, string Place, float Radius) : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) =>
            StoryMarks.Locate(world.Deck, Place) is { } at && director.Of(world, Role).Any(m => Vector2.Distance(m.Position, at) <= Radius);
    }

    /// <summary>The countdown runs and has at most so many seconds left (0: it ran out).</summary>
    public sealed record CountdownAt(float Seconds) : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) =>
            run.Timers.TryGetValue(StoryMarks.Countdown, out var ends) && ends - world.Time <= Seconds;
    }

    /// <summary>The chapter is in a section.</summary>
    public sealed record InSection(int Section) : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) => run.Section == Section;
    }

    /// <summary>All of the triggers hold.</summary>
    public sealed record AllOf(params StoryTrigger[] Triggers) : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) => Triggers.All(t => t.Holds(world, run, director));
    }

    /// <summary>Any of the triggers holds.</summary>
    public sealed record AnyOf(params StoryTrigger[] Triggers) : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) => Triggers.Any(t => t.Holds(world, run, director));
    }

    /// <summary>The trigger does not hold.</summary>
    public sealed record Not(StoryTrigger Trigger) : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) => !Trigger.Holds(world, run, director);
    }
}

// ---- actions -------------------------------------------------------------------------------------------

/// <summary>What a beat does to the world, on the tick it fires.</summary>
public abstract record StoryAction
{
    internal abstract void Do(GameWorld world, StoryRun run, StoryDirector director);

    /// <summary>A place of the deck as <see cref="StoryMarks.Locate"/> reads it.</summary>
    internal static Vector2 Where(GameWorld world, string place) =>
        StoryMarks.Locate(world.Deck, place) ?? throw new InvalidOperationException($"the deck has no place {place}");
}

/// <summary>A machine of the story: its type, subtype, level and weapons, as the game's data names them.</summary>
public sealed record StoryUnit(string Type, string? SubType, int Level, string? Weapons);

/// <summary>The actions a chapter's beats are written with (<c>using static Machines.Sim.Story.Then;</c>).</summary>
public static class Then
{
    /// <summary>A line is said: the ship's for everyone, a Grunt's as its bark (<see cref="StoryAudience.Companion"/>) or for its player alone.</summary>
    public sealed record Say(string Line, StoryAudience Audience = StoryAudience.Everyone, int Seat = -1) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            PlayerId? seat = Seat >= 0 && Seat < run.Seats.Count ? run.Seats[Seat] : null;
            var speaker = seat is { } player ? world.HeroOf(player) : null;
            StoryEvents.Line(world, Line, Audience, seat, speaker?.Id ?? 0, speaker?.Position ?? default);
        }
    }

    /// <summary>
    /// A Grunt's line said as its own: its bark while the computer plays it, the words only its player gets while a person
    /// does (Bob's foreign messages, docs/STORY.md "Die beiden Grunts").
    /// </summary>
    public sealed record SayToSeat(string Line, int Seat) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            if (Seat < 0 || Seat >= run.Seats.Count)
                return;
            var audience = world.StoryBotPlays(run.Seats[Seat]) ? StoryAudience.Companion : StoryAudience.Private;
            new Say(Line, audience, Seat).Do(world, run, director);
        }
    }

    /// <summary>The next line of a cycle (the ship's announcements that come round again).</summary>
    public sealed record SayNext(string Cycle, IReadOnlyList<string> Lines) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            var next = director.Counters.GetValueOrDefault(Cycle);
            director.Counters[Cycle] = next + 1;
            StoryEvents.Line(world, Lines[next % Lines.Count]);
        }
    }

    /// <summary>The trio's objective (English, the HUD translates it) and where it lies; null text takes it away.</summary>
    public sealed record Objective(string? Text, string? Place = null) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            StoryMarks.SetObjective(run, Text, Place);
            StoryEvents.Objective(world, Text, StoryMarks.Locate(world.Deck, Place));
        }
    }

    /// <summary>A checkpoint is reached: a later start of the chapter begins here (only ever forward).</summary>
    public sealed record Checkpoint(int Number) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            if (Number <= run.Checkpoint)
                return;
            run.Checkpoint = Number;
            StoryEvents.Checkpoint(world, Number, world.HeroOf(run.Locator)?.Position ?? Vector2.Zero);
        }
    }

    /// <summary>The chapter goes on to a section (docs/STORY.md numbers them 1 to 8).</summary>
    public sealed record Section(int Number) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director) => run.Section = Number;
    }

    /// <summary>Something for the view to show (a cutscene, a scare, the view leaning), at a place or none.</summary>
    public sealed record Cue(string Words, string? Place = null) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director) =>
            StoryEvents.Cue(world, Words, Place is null ? default : Where(world, Place));
    }

    /// <summary>Something for the view to show about the first machine of a role (a scare machine waking), at where it stands.</summary>
    public sealed record CueCast(string Words, string Role) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            if (director.Of(world, Role).FirstOrDefault() is { } machine)
                StoryEvents.Cue(world, Words, machine.Position, machine.Id);
        }
    }

    /// <summary>A hero of a seat kept from harm for so many seconds (the Locator while the core's alarm plays out).</summary>
    public sealed record Spare(int Seat, float Seconds) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            if (Seat >= 0 && Seat < run.Seats.Count)
                world.SpareHeroUntil(run.Seats[Seat], world.Time + Seconds);
        }
    }

    /// <summary>The view leans as the ship lists (roll and pitch in degrees, eased over seconds): the view's alone, the sim stays level.</summary>
    public sealed record Tilt(float Roll, float Pitch, float Seconds) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director) =>
            StoryEvents.Cue(world, FormattableString.Invariant($"tilt {Roll} {Pitch} {Seconds}"));
    }

    /// <summary>
    /// Light groups of the deck (its LIGHTS) switched on, off or to the emergency light; "*" for every group that is on now.
    /// The sight keeps them (<see cref="GameWorld.SetStoryLights"/>, <see cref="StorySight.Lights"/>): its seekers see by them,
    /// and the view follows.
    /// </summary>
    public sealed record Lights(string State, params string[] Groups) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            var groups = Groups is ["*"]
                ? (world.Deck?.Lights.Select(l => l.Name) ?? []).Where(g => world.StoryLightState(g) == "on").ToList()
                : Groups.ToList();
            foreach (var group in groups)
                world.SetStoryLights(group, State);
        }
    }

    /// <summary>
    /// The lamps of a role's machines switched on or off (<see cref="GameWorld.SetStoryLamp"/>), or with null handed back to
    /// the sight's rule (dark in a clamp, lit elsewhere): the scare Grunt's lamp flaring in its clamp.
    /// </summary>
    public sealed record Lamp(string Role, bool? On) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            foreach (var machine in director.Of(world, Role))
                world.SetStoryLamp(machine.Id, On);
        }
    }

    /// <summary>
    /// A Grunt (seat 1 Ike, 2 Bob) given a job (<see cref="GameWorld.AssignCompanion"/>): barricade a door (its letter), hold a
    /// switch (its marker), board the pod (its hatch marker); <see cref="CompanionTask.None"/> ends it. The computer carries
    /// it out while it plays the seat.
    /// </summary>
    public sealed record Assign(int Seat, CompanionTask Task, string? Place = null) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            if (Seat > 0 && Seat < run.Seats.Count)
                world.AssignCompanion(run.Seats[Seat], Task, Place);
        }
    }

    /// <summary>The crates the Grunts set in a door's barricade slots thrown aside (the assault bursting in): the doorway is free.</summary>
    public sealed record ClearBarricade(string Door) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            foreach (var slot in world.BarricadeSlots(Door))
            {
                foreach (var crate in world.Artefacts.Where(a => !a.IsDead && Vector2.DistanceSquared(a.Position, slot.Position) < 0.25f).OrderBy(a => a.Id).ToList())
                {
                    world.RemoveArtefact(crate.Id);
                    StoryEvents.Cue(world, $"thrown {crate.Id - Artefact.IdBase}", crate.Position);
                }
            }
        }
    }

    /// <summary>Deck doors opened now.</summary>
    public sealed record OpenDoors(string Letters) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            foreach (var letter in Letters)
            {
                director.AutoDoors.Remove(letter);
                world.OpenDoor(letter);
            }
        }
    }

    /// <summary>Deck doors that open by themselves from now on, once a hero of the trio comes near.</summary>
    public sealed record AutoDoors(string Letters) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            foreach (var letter in Letters.Where(l => world.DoorClosed(l)))
                director.AutoDoors.Add(letter);
        }
    }

    /// <summary>Hack targets opened to the Locator (unlocked) or closed to it for now (locked).</summary>
    public sealed record Hackable(bool Open, params string[] Targets) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            foreach (var target in Targets)
            {
                if (Open)
                    run.Hacks.Locked.Remove(target);
                else
                    run.Hacks.Locked.Add(target);
            }
        }
    }

    /// <summary>A hack broken off where it stands (the core's at 90 %).</summary>
    public sealed record BreakHack(string Target) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director) => world.BreakHack(Target);
    }

    /// <summary>
    /// The Locator's probe pulled out of a hack it is plugged into (the core's, a moment after the alarm broke it off): its
    /// board comes down and the target stays as it stands, at no cost.
    /// </summary>
    public sealed record Unplug(string Target) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            if (run.Hacks.Session(Target) is { Open: true } session)
                session.Hacker = null;
        }
    }

    /// <summary>A flag of the run set or cleared.</summary>
    public sealed record Flag(string Name, bool On = true) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            if (On)
                run.Flags.Add(Name);
            else
                run.Flags.Remove(Name);
        }
    }

    /// <summary>The ship's alarm on or off (<see cref="StoryMarks.Alarm"/>).</summary>
    public sealed record Alarm(bool On) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            if (On ? run.Flags.Add(StoryMarks.Alarm) : run.Flags.Remove(StoryMarks.Alarm))
                StoryEvents.Alarm(world, On, world.HeroOf(run.Locator)?.Position ?? default);
        }
    }

    /// <summary>Heroes of seats (0 the Locator, 1 Ike, 2 Bob) held still, or let go.</summary>
    public sealed record Hold(bool On, params int[] Seats) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            foreach (var seat in Seats.Where(s => s < run.Seats.Count))
            {
                if (world.HeroOf(run.Seats[seat]) is not { } hero)
                    continue;
                if (On)
                    director.Held.Add(hero.Id);
                else
                    director.Held.Remove(hero.Id);
            }
        }
    }

    /// <summary>
    /// A Grunt hacked awake: no longer dormant (<see cref="GameWorld.SetCompanionDormant"/>, which says its waking bark and
    /// sets it to follow; the companions wake it by its terminal's hack already) nor held. The computer's companion
    /// (<see cref="Ai.CompanionBot"/>) then keeps it with the Locator.
    /// </summary>
    public sealed record Wake(int Seat) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            if (Seat <= 0 || Seat >= run.Seats.Count)
                return;
            var seat = run.Seats[Seat];
            world.SetCompanionDormant(seat, false);
            if (world.HeroOf(seat) is { } grunt)
                director.Held.Remove(grunt.Id);
        }
    }

    /// <summary>A Grunt (seat 1 Ike, 2 Bob) put to sleep in its clamp (<see cref="GameWorld.SetCompanionDormant"/>) and held there.</summary>
    public sealed record Sleep(int Seat) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            if (Seat <= 0 || Seat >= run.Seats.Count)
                return;
            var seat = run.Seats[Seat];
            world.SetCompanionDormant(seat, true);
            if (world.HeroOf(seat) is { } grunt)
                director.Held.Add(grunt.Id);
        }
    }

    /// <summary>
    /// The machines of a part of the chapter set down, of a role, one at each place (a marker, a zone's middle or "x,y"; the
    /// nearest free ground to it), facing as its marker does. <paramref name="Inert"/> ones are switched off: no weapons to
    /// fire, standing, never moving (the dark rows in the clamps).
    /// </summary>
    public sealed record Spawn(string Role, StoryUnit Unit, IReadOnlyList<string> Places, DefCon DefCon = DefCon.Low, bool Inert = false) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            if (!director.Cast.TryGetValue(Role, out var ids))
                director.Cast[Role] = ids = [];
            foreach (var place in Places)
            {
                var at = Where(world, place);
                var machine = world.SpawnForStory(run.Options.Ship, at, Unit, exact: Inert);
                if (world.Deck?.Markers.FirstOrDefault(m => m.Name == place) is { } marker)
                    machine.Facing = marker.Angle * MathF.PI / 180;
                machine.DefCon = Inert ? DefCon.High : DefCon;
                if (Inert)
                {
                    machine.Weapons.Clear();
                    world.SendForStory(machine, new StandGroundOrder(), null);
                    // Switched off: it sees nothing (the sight's Blind), its lamp dark while it hangs in a clamp.
                    world.StoryWatch(machine).Blind = true;
                }
                ids.Add(machine.Id);
            }
        }
    }

    /// <summary>The machines of a role sent to places: the first to the first place and so on, the rest to the last.</summary>
    public sealed record MoveTo(string Role, params string[] Places) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            var i = 0;
            foreach (var machine in director.Of(world, Role).ToList())
            {
                var at = Where(world, Places[Math.Min(i++, Places.Length - 1)]);
                world.SendForStory(machine, new MoveOrder(at), at);
            }
        }
    }

    /// <summary>The machines of a role at a tagged artefact (the core): they fire at it.</summary>
    public sealed record AttackArtefact(string Role, int Tag) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            if (world.Artefacts.FirstOrDefault(a => a.Tag == Tag && !a.IsDead) is not { } target)
                return;
            foreach (var machine in director.Of(world, Role).ToList())
            {
                world.SendForStory(machine, new AttackOrder(target.Id), target.Position);
            }
        }
    }

    /// <summary>The machines of a role take a readiness (High: they fire only when fired at; Low: they go for what they see).</summary>
    public sealed record Ready(string Role, DefCon DefCon) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            foreach (var machine in director.Of(world, Role))
                machine.DefCon = DefCon;
        }
    }

    /// <summary>
    /// The machines of a role sent on a round of the deck's markers <paramref name="Prefix"/>_1, _2 … (the sight's
    /// <see cref="GameWorld.StoryPatrol"/>: a <see cref="PatrolOrder"/> that dwells at the markers tagged <c>dwell</c> and
    /// sweeps its beam there). A hunt or a search the sight gives them comes back to the round afterwards.
    /// </summary>
    public sealed record Patrol(string Role, string Prefix) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            foreach (var machine in director.Of(world, Role).ToList())
                world.StoryPatrol(machine, Prefix);
        }
    }

    /// <summary>A tagged artefact (a door leaf, a crate, the core) taken off the map whole.</summary>
    public sealed record RemoveProp(int Tag) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            foreach (var artefact in world.Artefacts.Where(a => a.Tag == Tag).OrderBy(a => a.Id).ToList())
            {
                director.Shielded.Remove(artefact.Id);
                world.RemoveArtefact(artefact.Id);
            }
        }
    }

    /// <summary>A tagged artefact harmed by so much (all it has with null: destroyed, with its blast in the view).</summary>
    public sealed record HarmProp(int Tag, int? Damage = null) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            foreach (var artefact in world.Artefacts.Where(a => a.Tag == Tag && !a.IsDead).OrderBy(a => a.Id).ToList())
            {
                director.Shielded.Remove(artefact.Id);
                world.Damage(artefact, Damage ?? artefact.HitPoints + artefact.Armour + 1, null);
            }
        }
    }

    /// <summary>A tagged artefact kept whole (mended every tick) or let go.</summary>
    public sealed record ShieldProp(int Tag, bool On = true) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            foreach (var artefact in world.Artefacts.Where(a => a.Tag == Tag))
            {
                if (On)
                    director.Shielded.Add(artefact.Id);
                else
                    director.Shielded.Remove(artefact.Id);
            }
        }
    }

    /// <summary>An artefact of the scenario's type set down at a place, turned by degrees: debris falling, a crate set down. The view hears a cue.</summary>
    public sealed record PlaceProp(string Type, string Place, float Angle = 0, float Aside = 0) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director) =>
            world.PlaceArtefact(Type, Where(world, Place) + new Vector2(Aside, 0), Angle);
    }

    /// <summary>An explosion at a place (GameWorld.ScriptExplosion): everything within its range takes the damage, less the further off.</summary>
    public sealed record Blast(string Place, float Range, int Damage, float Size) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director) => world.ScriptExplosion(Where(world, Place), Range, Damage, Size);
    }

    /// <summary>The countdown to the crash starts (<see cref="StoryMarks.Countdown"/>), with so many seconds; or, with none, it stops.</summary>
    public sealed record Countdown(float? Seconds) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            if (Seconds is not { } seconds)
            {
                var left = run.Timers.TryGetValue(StoryMarks.Countdown, out var ends) ? ends - world.Time : 0;
                run.Timers.Remove(StoryMarks.Countdown);
                run.Timers.Remove(StoryMarks.CountdownFrom);
                StoryEvents.Countdown(world, "stop", left);
                return;
            }
            run.Timers[StoryMarks.Countdown] = world.Time + seconds;
            run.Timers[StoryMarks.CountdownFrom] = world.Time;
            StoryEvents.Countdown(world, "start", seconds);
        }
    }

    /// <summary>A stage of the countdown, told to the view (its lines and shakes come from beats of their own).</summary>
    public sealed record CountdownStage : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            if (run.Timers.TryGetValue(StoryMarks.Countdown, out var ends))
                StoryEvents.Countdown(world, "stage", ends - world.Time);
        }
    }

    /// <summary>The chapter ends: lost (it starts again from its checkpoint) or escaped.</summary>
    public sealed record End(StoryEnding Ending) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director) => run.Ending ??= Ending;
    }

    /// <summary>Anything else, said in code: for what a chapter does once and no action names.</summary>
    public sealed record Run(string What, Action<GameWorld, StoryRun, StoryDirector> Body) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director) => Body(world, run, director);
    }
}
