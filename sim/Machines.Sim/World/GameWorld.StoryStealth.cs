using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Story;

namespace Machines.Sim.World;

/// <summary>
/// Section 2's Thresher (plan stufe7 §4 S, design-stealth): while the story's <see cref="StoryMarks.Stealth"/> is set, a
/// searchlight seeker looks from its 9-m eye (low cover hides nothing, the deck's high stacks and walls everything), goes
/// at a pace by its order, keeps rounds that switch at its next point, holds a guard post with its look back, takes the
/// director's scripted looks and rush, answers lures with an escalation per source, and what it did to a hero heals once
/// it has left it alone a while. The computer's Grunts it does not see while the Locator cannot see them (the offscreen
/// grace). Nothing here runs without the flag, so the armoury's and the escape's hunters keep today's rule (C6).
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>Whether a machine is a searchlight seeker under section 2's rule now (the flag set, a searchlight, not blind).</summary>
    public bool StoryStalks(Machine machine) =>
        Story is { } run && run.Flags.Contains(StoryMarks.Stealth) && run.Sight.Seekers.TryGetValue(machine.Id, out var eyes) && eyes.Searchlight && !eyes.Blind;

    /// <summary>Whether a machine stands at rest (neither driving nor turning, no way left to go), for the computer's Grunts' lane rule.</summary>
    internal bool StoryAtRest(Machine machine) => !IsMoving(machine) && machine.PathIndex >= machine.Path.Count;

    // ---- his eye --------------------------------------------------------------------------------------

    /// <summary>
    /// Whether his 9-m eye has a clear line to a hero: no wall, high stack ('h') or shut door leaf between (a crate cover and a
    /// grate hide nothing from that height), nor a machine hanging in a clamp, as <see cref="ClearShot"/> has them.
    /// </summary>
    public bool StoryEyeClear(Machine seeker, Machine hero)
    {
        if (Deck is not { } deck)
            return ClearShot(seeker, hero);
        var (from, to) = (seeker.Position, hero.Position);
        var length = Vector2.Distance(from, to);
        if (length > 2)
        {
            var samples = (int)MathF.Ceiling(length / 0.5f);
            for (var i = 1; i < samples; i++)
            {
                var at = Vector2.Lerp(from, to, i / (float)samples);
                if (Vector2.Distance(at, from) < 1 || Vector2.Distance(at, to) < 1)
                    continue;
                if (deck.Cell((int)MathF.Floor(at.X), (int)MathF.Floor(at.Y)) is DeckCell.Wall or DeckCell.High)
                    return false;
            }
        }
        var (a, b) = (AimPoint(seeker), AimPoint(hero));
        return FirstOnLine(a, b, actor => actor != seeker && actor != hero && (actor is not Machine || !AreEnemies(seeker.Race, actor.Race))) is null;
    }

    /// <summary>Whether a point lies in a zone of the deck tagged so (<c>shadow</c>, <c>row</c>).</summary>
    private bool InTaggedZone(Vector2 point, string tag)
    {
        foreach (var zone in Deck?.Zones ?? [])
        {
            if (zone.Tags.Contains(tag) && zone.Contains(point))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The offscreen grace (design-stealth §6.3): a Grunt the computer plays while the Locator crawls in a duct or is
    /// <see cref="StorySight.GraceApart"/> metres away, or a seat the director keeps unseen (<see cref="Then.Unseen"/>).
    /// </summary>
    private bool StoryGraced(StoryRun run, Machine hero)
    {
        if (run.Stealth.Unseen.Count > 0 && run.Seats.Any(s => run.Stealth.Unseen.Contains(s.Value) && HeroOf(s)?.Id == hero.Id))
            return true;
        if (CompanionSeat(hero) is not { } seat)
            return false;
        if (!StoryBotPlays(seat) || HeroOf(run.Locator) is not { } locator)
            return false;
        return Deck?.CellAt(locator.Position) is DeckCell.Duct or DeckCell.Lip || Vector2.Distance(locator.Position, hero.Position) > StorySight.GraceApart;
    }

    /// <summary>What his eye makes of a hero under section 2's rule (design-stealth §3.3); <see cref="StoryPerceives"/> asks it for a stalker.</summary>
    private StoryGlimpse StalkerPerceives(StoryRun run, Machine seeker, Machine hero)
    {
        if (StoryGraced(run, hero))
            return StoryGlimpse.None;
        var signature = StorySignature(hero.Id);
        var distance = Vector2.Distance(seeker.Position, hero.Position);
        var touch = StorySight.Touch * StorySight.TouchScale * (1 + signature) + hero.Radius;
        var hearing = StorySight.Hearing * signature;
        if (distance > MathF.Max(StorySight.LampSight, MathF.Max(touch, MathF.Max(hearing, StorySight.EdgeReach))))
            return StoryGlimpse.None;
        var clear = StoryEyeClear(seeker, hero);
        if (clear && distance <= touch)
            return StoryGlimpse.Seen;
        if (clear && InBeam(seeker, hero))
        {
            var lamp = StoryLampOn(hero.Id);
            var shadowed = !lamp && signature <= StorySight.ShadowStill && InTaggedZone(hero.Position, "shadow");
            var reach = lamp ? StorySight.LampSight : !StoryLampOn(seeker.Id) ? 0 : shadowed ? StorySight.ShadowLit : StorySight.BeamLength;
            if (distance <= reach || StoryLightAt(hero.Position) == "on" && !shadowed)
                return StoryGlimpse.Seen;
        }
        if (clear && OnEdge(seeker, hero, distance, signature))
            return StoryGlimpse.Heard;
        return distance <= (clear ? hearing : hearing / 2) ? StoryGlimpse.Heard : StoryGlimpse.None;
    }

    /// <summary>The edge of his eye: near, wide of the beam, and the hero moving loud enough to catch it.</summary>
    private bool OnEdge(Machine seeker, Machine hero, float distance, float signature)
    {
        if (distance > StorySight.EdgeReach || signature < StorySight.EdgeSignature)
            return false;
        var way = hero.Position - seeker.Position;
        var off = MathF.IEEERemainder(DetMath.Atan2(way.Y, way.X) - GunHeading(seeker), 2 * MathF.PI);
        return MathF.Abs(off) <= StorySight.EdgeAngle * MathF.PI / 180;
    }

    /// <summary>How fast his suspicion of a hero rises a second for what he made of it (close in the beam faster, the edge slower than sight).</summary>
    private float StalkerRise(Machine seeker, Machine hero, StoryGlimpse glimpse)
    {
        var distance = Vector2.Distance(seeker.Position, hero.Position);
        return glimpse switch
        {
            StoryGlimpse.Seen => distance <= StorySight.CloseBeam ? StorySight.CloseRise : StorySight.RiseSeen,
            StoryGlimpse.Heard when OnEdge(seeker, hero, distance, StorySignature(hero.Id)) => MathF.Max(StorySight.EdgeRise, StorySight.RiseHeard),
            _ => StorySight.RiseHeard,
        };
    }

    /// <summary>A Grunt of the trio standing still with its lamp off in a clamp row: he takes it for one of the row's (<see cref="StorySight.CamoCap"/>).</summary>
    public bool StoryCamouflaged(Machine hero) =>
        Story is { } run && CompanionSeat(hero) is not null && !StoryLampOn(hero.Id) && StorySignature(hero.Id) <= StorySight.ShadowStill
        && InTaggedZone(hero.Position, "row") && run.Flags.Contains(StoryMarks.Stealth);

    // ---- his paces ----------------------------------------------------------------------------------

    /// <summary>His share of top speed under section 2's rule by what he does; null for any other machine or time.</summary>
    private float? StalkerPace(StoryRun run, Machine machine)
    {
        if (!StoryStalks(machine))
            return null;
        if (run.Stealth.Rushing.Contains(machine.Id) || machine.Order is AttackOrder)
            return StorySight.HuntPace;
        if (machine.Order is SearchOrder || machine.Order is MoveOrder && run.Stealth.Guards.ContainsKey(machine.Id))
            return StorySight.SearchPace;
        return StorySight.PatrolPace;
    }

    /// <summary>His share of the turn rate under section 2's rule; null for any other machine or time.</summary>
    private float? StalkerTurn(StoryRun run, Machine machine)
    {
        if (!StoryStalks(machine))
            return null;
        if (run.Stealth.Rushing.Contains(machine.Id) || machine.Order is AttackOrder || run.Stealth.Guards.ContainsKey(machine.Id) && machine.Order is IdleOrder)
            return 1;
        // Standing at the end of a lane he pivots at his whole turn (as for the Rückblick); the patrol's slow turn is for
        // the curves he walks.
        if (machine.Order is PatrolOrder && (!_motion.TryGetValue(machine.Id, out var motion) || motion.Speed == 0))
            return 1;
        return machine.Order is SearchOrder ? StorySight.SearchTurn : StorySight.PatrolTurn;
    }

    // ---- rounds -------------------------------------------------------------------------------------

    /// <summary>
    /// Sends a machine on one of section 2's rounds (<see cref="StoryStealth.Rounds"/>) from its point <paramref name="from"/>
    /// (-1: the nearest), dwelling where the markers say (<c>dwell=</c>, <c>sweep=</c>, <c>look=</c>).
    /// </summary>
    public PatrolOrder StoryRound(Machine machine, string round, int from = -1)
    {
        var run = Story ?? throw new InvalidOperationException("a round needs a story");
        var places = StoryStealth.Rounds[round];
        var points = places.Select(p => StoryMarks.Locate(Deck, p) ?? throw new KeyNotFoundException($"no place {p}")).ToList();
        var eyes = StoryWatch(machine);
        eyes.DwellAngles.Clear();
        eyes.DwellTimes.Clear();
        eyes.DwellSweeps.Clear();
        var pauses = new List<int>();
        for (var i = 0; i < places.Count; i++)
        {
            if (Deck?.Markers.FirstOrDefault(m => m.Name == places[i]) is not { } marker || TagOf(marker, "dwell") is not { } dwell)
                continue;
            pauses.Add(i);
            // A dwell only on the way east (a2's listening) is kept negative.
            eyes.DwellTimes[i] = Number(dwell, StorySight.DwellTime) * (marker.Tags.Contains("eastbound") ? -1 : 1);
            eyes.DwellSweeps[i] = Number(TagOf(marker, "sweep"), StorySight.DwellSweep);
            eyes.DwellAngles[i] = Number(TagOf(marker, "look"), marker.Angle);
        }
        if (from < 0)
        {
            from = 0;
            for (var i = 1; i < points.Count; i++)
            {
                if (Vector2.DistanceSquared(points[i], machine.Position) < Vector2.DistanceSquared(points[from], machine.Position))
                    from = i;
            }
        }
        run.Stealth.RoundOf[machine.Id] = round;
        run.Stealth.Laps[machine.Id] = 0;
        run.Stealth.Guards.Remove(machine.Id);
        run.Stealth.NextRound.Remove(machine.Id);
        eyes.DwelledAt = -1;
        var patrol = new PatrolOrder(points, from % points.Count, pauses);
        Send(machine, patrol, patrol.Points[patrol.Next]);
        return patrol;
    }

    /// <summary>A marker's tag <c>key=value</c>'s value; null without it.</summary>
    private static string? TagOf(DeckMarker marker, string key) =>
        marker.Tags.FirstOrDefault(t => t.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..];

    private static float Number(string? text, float otherwise) =>
        float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : otherwise;

    /// <summary>
    /// He reached a point of his round: a round the director gave him takes over here (never mid-lane), and the last point
    /// of a round counts a lap. True when the round changed (the patrol was replaced).
    /// </summary>
    private bool StoryArrives(Machine machine, PatrolOrder patrol)
    {
        if (Story is not { } run || !run.Stealth.RoundOf.TryGetValue(machine.Id, out var round))
            return false;
        if (run.Stealth.NextRound.Remove(machine.Id, out var next))
        {
            StoryRound(machine, next.Round, next.From);
            return true;
        }
        if (patrol.Next == patrol.Points.Count - 1 && StorySeekerOf(machine.Id)?.DwelledAt != patrol.Next)
            run.Stealth.Laps[machine.Id] = run.Stealth.Laps.GetValueOrDefault(machine.Id) + 1;
        _ = round;
        return false;
    }

    // ---- guard, looks, rush -----------------------------------------------------------------------

    /// <summary>Puts a machine on guard at a post: there at the search's pace, then sweeping with its look back.</summary>
    public void StoryGuardAt(Machine machine, string place)
    {
        var run = Story!;
        var at = StoryMarks.Locate(Deck, place) ?? throw new KeyNotFoundException($"no place {place}");
        run.Stealth.Guards[machine.Id] = new StoryGuard(place, Time);
        run.Stealth.RoundOf.Remove(machine.Id);
        run.Stealth.NextRound.Remove(machine.Id);
        run.Stealth.Rushing.Remove(machine.Id);
        Send(machine, new MoveOrder(at), at);
    }

    /// <summary>
    /// The seam at the head of his order for section 2: a scripted look holds him looking; at his guard post he sweeps, and
    /// every <see cref="StorySight.GuardEvery"/> seconds looks back. True while it holds him so.
    /// </summary>
    private bool StalkerHolds(StoryRun run, Machine machine)
    {
        if (run.Stealth.Looks.TryGetValue(machine.Id, out var look))
        {
            if (Time < look.Until && machine.Order is not (AttackOrder or SearchOrder))
            {
                LookTowards(machine, look.Heading);
                return true;
            }
            if (Time >= look.Until)
                run.Stealth.Looks.Remove(machine.Id);
        }
        if (!run.Stealth.Guards.TryGetValue(machine.Id, out var guard) || machine.Order is not IdleOrder
            || Deck?.Markers.FirstOrDefault(m => m.Name == guard.Place) is not { } post
            || Vector2.Distance(post.Position, machine.Position) > 4)
            return false;
        var heading = Number(TagOf(post, "look"), post.Angle) * MathF.PI / 180;
        var sweep = Number(TagOf(post, "sweep"), 35) * MathF.PI / 180;
        var t = (Time - guard.Since) % StorySight.GuardEvery;
        var back = StorySight.GuardEvery - (2 * StorySight.GuardTurn + StorySight.GuardHold);
        if (t >= back)
            LookTowards(machine, heading + MathF.PI);
        else
            LookTowards(machine, heading + sweep * DetMath.Sin(2 * MathF.PI * t / StorySight.GuardPeriod));
        return true;
    }

    /// <summary>
    /// Section 2's part of the story's tick, after the sight: the valves' clangs come out at their far ends, a guard that
    /// stands idle off its post goes back, a rush that arrived ends, what he did to a hero heals once he left it alone, and a
    /// Grunt put back together chirps (and says its dark line, not the workshop's).
    /// </summary>
    private void UpdateStealth(StoryRun run)
    {
        var stealth = run.Stealth;
        for (var i = 0; i < stealth.Pipes.Count; i++)
        {
            var pipe = stealth.Pipes[i];
            if (Time < pipe.Due)
                continue;
            stealth.Pipes.RemoveAt(i--);
            if (StoryMarks.Locate(Deck, pipe.Far) is { } far)
            {
                StoryEvents.Cue(this, $"pipe far {pipe.Far}", far);
                StoryLure(far, pipe.Loud, pipe.Source);
            }
        }
        if (!run.Flags.Contains(StoryMarks.Stealth))
            return;
        foreach (var (id, guard) in stealth.Guards)
        {
            if (Find<Machine>(id) is not { IsDead: false } machine || machine.Order is not IdleOrder || StoryMarks.Locate(Deck, guard.Place) is not { } at
                || Vector2.Distance(at, machine.Position) <= 4)
                continue;
            Send(machine, new MoveOrder(at), at);
        }
        foreach (var id in stealth.Rushing.ToList())
        {
            if (Find<Machine>(id) is not { IsDead: false } machine || machine.Order is not MoveOrder)
                stealth.Rushing.Remove(id);
        }
        // What he sees he marks; what he did heals once he has left it alone (design-stealth §3.4).
        foreach (var eyes in run.Sight.Seekers.Values.Where(e => e.Searchlight))
        {
            foreach (var seen in eyes.Sees)
            {
                if (Find<Machine>(seen) is not { } hero)
                    continue;
                if (!stealth.ThreatAt.TryGetValue(seen, out var at) || Time - at >= StorySight.HealAfter)
                    stealth.HealTo[seen] = hero.HitPoints;
                stealth.ThreatAt[seen] = Time;
            }
        }
        foreach (var (id, to) in stealth.HealTo.ToList())
        {
            if (Time - stealth.ThreatAt.GetValueOrDefault(id) < StorySight.HealAfter)
                continue;
            stealth.HealTo.Remove(id);
            if (Find<Machine>(id) is { IsDead: false } hero && !StoryLies(hero) && hero.HitPoints < to)
                hero.HitPoints = Math.Min(hero.MaxHitPoints, to);
        }
        // A Grunt back on its wheels in the dark: its reboot chirps, and it says the dark decks' line (P_IKE_UP_DARK).
        for (var i = 1; i < run.Seats.Count; i++)
        {
            var seat = run.Seats[i];
            var down = run.Flags.Contains(StoryMarks.Down(seat));
            if (down)
            {
                stealth.Down.Add(seat.Value);
                continue;
            }
            if (!stealth.Down.Remove(seat.Value) || HeroOf(seat) is not { } grunt)
                continue;
            run.Crew.Noise(new StoryNoise(grunt.Position, StorySight.RebootLoud, Tick, grunt.Id));
            StoryEvents.Cue(this, "reboot chirp", grunt.Position, grunt.Id);
            for (var b = 0; b < run.Crew.Barks.Count; b++)
            {
                if (run.Crew.Barks[b] is { } bark && bark.Tick >= Tick - 1 && bark.Seat == seat && bark.Line is "P_IKE_UP" or "P_BOB_UP")
                    run.Crew.Barks[b] = bark with { Line = i == 1 ? Prologue.Lines.IkeUpDark : Prologue.Lines.BobUpDark };
            }
        }
    }

    // ---- lures ----------------------------------------------------------------------------------------

    /// <summary>
    /// A noise made on purpose at <paramref name="at"/> as loud as <paramref name="loud"/> (a valve's far end, a source, the
    /// director's <see cref="Then.Lure"/>): every searchlight seeker within <see cref="StorySight.LureHeard"/> × loud (half
    /// through walls) that is not hunting turns, suspicious, and goes to look there; the second time at one source within
    /// <see cref="StorySight.Escalate"/> seconds it looks longer. It is told (<c>lure heard</c>) for the view.
    /// </summary>
    public void StoryLure(Vector2 at, float loud, string source)
    {
        if (Story is not { } run)
            return;
        var again = run.Stealth.BangsAt(source, Time) >= 2;
        foreach (var eyes in run.Sight.Seekers.Values.ToList())
        {
            if (!eyes.Searchlight || eyes.Blind || Find<Machine>(eyes.Id) is not { IsDead: false, DrivenByPlayer: false, Inside: null } seeker
                || seeker.Order is AttackOrder)
                continue;
            var distance = Vector2.Distance(seeker.Position, at);
            var reach = StorySight.LureHeard * loud * (Clear(seeker.Position, at) ? 1 : 0.5f);
            if (distance > reach)
                continue;
            (eyes.Quarry, eyes.Spot) = (0, at);
            eyes.Suspicion = MathF.Max(eyes.Suspicion, StorySight.LureSuspicion);
            if (!eyes.Wary)
            {
                eyes.Wary = true;
                Tell(StorySightKind.Suspicious, eyes);
            }
            var back = seeker.Order is SearchOrder { Back: var was } ? was : PutAside(seeker);
            var spot = ReachableSpot(seeker, at);
            var orderedAt = seeker.OrderedAt;
            Send(seeker, new SearchOrder(spot, Time + (again ? StorySight.LureSearchAgain : StorySight.LureSearch), back), spot);
            seeker.OrderedAt = orderedAt;
            run.Stealth.Looks.Remove(seeker.Id);
            Tell(StorySightKind.Searching, eyes);
            StoryEvents.Cue(this, $"lure heard {source}", at, seeker.Id);
        }
    }

    /// <summary>
    /// Whether no wall lies on the flat line between two points of the deck: a wall halves a lure's sound. The 8-m stacks do
    /// not; a bang carries over them in the tall halls, or the valves by the Nordgang could never draw him off his guard
    /// before H to their pipes' far ends 72 m away (design-stealth §3.5's window), with deck B's stacks between.
    /// </summary>
    private bool Clear(Vector2 from, Vector2 to)
    {
        if (Deck is not { } deck)
            return true;
        var length = Vector2.Distance(from, to);
        var samples = (int)MathF.Ceiling(length / 0.5f);
        for (var i = 1; i < samples; i++)
        {
            var at = Vector2.Lerp(from, to, i / (float)samples);
            if (deck.Cell((int)MathF.Floor(at.X), (int)MathF.Floor(at.Y)) is DeckCell.Wall)
                return false;
        }
        return true;
    }

    /// <summary>
    /// A noise source of the deck (a marker tagged <c>noise</c>) is banged by <paramref name="by"/> (a Grunt, or 0 for the
    /// director): a valve (<c>far=</c>) clangs a little at itself and loud at its pipe's far end <see cref="StorySight.PipeDelay"/>
    /// later; any other sounds at itself (<c>loud=</c>, 2.5 by default). The third bang within <see cref="StorySight.Escalate"/>
    /// seconds draws his fire into it, and it is spent. Null when done, else why not.
    /// </summary>
    public string? StoryBang(string source, int by)
    {
        if (Story is not { } run || Deck?.Markers.FirstOrDefault(m => m.Name == source && m.Tags.Contains("noise")) is not { } marker)
            return "no such noise source";
        if (run.Stealth.Spent.Contains(source))
            return "that source is spent";
        if (!run.Stealth.Bangs.TryGetValue(source, out var times))
            run.Stealth.Bangs[source] = times = [];
        times.RemoveAll(t => Time - t >= StorySight.Escalate);
        times.Add(Time);
        if (times.Count >= StorySight.SpentAfter)
        {
            run.Stealth.Spent.Add(source);
            var at = TagOf(marker, "far") is { } spent && StoryMarks.Locate(Deck, spent) is { } end ? end : marker.Position;
            StoryEvents.Cue(this, $"lure volley {source}", at, by);
        }
        var loud = Number(TagOf(marker, "loud"), 2.5f);
        if (TagOf(marker, "far") is { } far)
        {
            StoryEvents.Cue(this, $"valve bang {source} {far}", marker.Position, by);
            StoryLure(marker.Position, StorySight.ValveLoud, source);
            run.Stealth.Pipes.Add(new StoryPipe(Time + StorySight.PipeDelay, source, far, loud));
        }
        else
        {
            StoryEvents.Cue(this, $"source bang {source}", marker.Position, by);
            StoryLure(marker.Position, loud, source);
        }
        return null;
    }
}
