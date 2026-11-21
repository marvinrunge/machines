using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Story;
using Machines.Sim.Survival;

namespace Machines.Sim.World;

/// <summary>
/// The story's stealth (docs/STORY.md, Horror-Level; plan WP-D). The ship's machines do not see the trio as machines see
/// each other elsewhere (anything within the scanner's reach): a seeker sees a hero only with a clear line to it, and only
/// when the hero is lit (by a searchlight's beam, a lit room, its own lamp) or near enough to touch; a searchlight (the
/// Thresher) sees only inside its cone. What it does not see it may hear, by the hero's signature: how loud and visible it
/// is (still and dark all but nothing, more moving, with the lamp, after a shot, the scanner's pulse or a hack's alarm, the
/// Grunts' wheels twice as loud). What a seeker sees or hears makes it suspicious; at full suspicion it hunts what it sees,
/// or searches where it heard it, and a hunt that loses sight of its quarry searches where it was last seen
/// (<see cref="SearchOrder"/>), then takes up its round again. Being hit always gives the shooter away.
/// <para>
/// A fair hunt (docs/STORY.md, Technische Notizen of the prologue): a seeker that spots a hero calls the others near it by
/// data link (<see cref="Call"/>; while the trio still sneaks the ship stays alert for a while, <see cref="StoryMarks.Alert"/>);
/// it draws before it fires and holds its fire beyond <see cref="StorySight.FireReach"/> (<see cref="StoryHoldsFire"/>),
/// its hits on the trio weigh <see cref="StorySeeker.Harm"/>, and a caller (the Commander) never fires at all.
/// </para>
/// <para>
/// The seams, as the Phantom's shadow has them (GameWorld.WiredShadow): <see cref="NearestEnemy"/> passes over a hero its
/// seeker has not spotted, a patrol stops when wary and dwells at its points, a hit tells who fired. The lamps of the
/// machines and the deck's light groups are the sim's here, so the view follows them. Nothing here runs without
/// <see cref="Story"/>: no other match is touched.
/// </para>
/// </summary>
public sealed partial class GameWorld
{
    // ---- lights and lamps ----------------------------------------------------------------------------

    /// <summary>The deck's light groups with the zones they light, worked out once (the deck never changes).</summary>
    private List<(string Group, DeckZone Zone)>? _storyLightZones;

    /// <summary>How a light group of the deck is lit now: "on", "off" or "emergency" (as the deck starts it, until the story switches it); null for no such group.</summary>
    public string? StoryLightState(string group) =>
        Story?.Sight.Lights.TryGetValue(group, out var state) == true ? state : Deck?.Lights.FirstOrDefault(l => l.Name == group)?.State;

    /// <summary>
    /// Lights a group of the deck's lamps (LIGHTS) "on", "off" or "emergency", as the director does when the power fails or
    /// a room's lamps come up: the seekers see by it, and the view follows. False for an unknown group or state.
    /// </summary>
    public bool SetStoryLights(string group, string state)
    {
        if (Story is not { } run || Deck?.Lights.Any(l => l.Name == group) != true || state is not ("on" or "off" or "emergency"))
            return false;
        run.Sight.Lights[group] = state;
        return true;
    }

    /// <summary>How the deck is lit at a point: "on" if a lit group's zone holds it, else "emergency" if a red one does, else "off".</summary>
    public string StoryLightAt(Vector2 point)
    {
        if (Deck is not { } deck)
            return "off";
        _storyLightZones ??= [.. deck.Lights.Select(l => (l.Name, deck.Zone(l.Zone))).Where(l => l.Item2 is not null).Select(l => (l.Name, l.Item2!))];
        var best = "off";
        foreach (var (group, zone) in _storyLightZones)
        {
            if (!zone.Contains(point))
                continue;
            var state = StoryLightState(group);
            if (state == "on")
                return "on";
            if (state == "emergency")
                best = "emergency";
        }
        return best;
    }

    /// <summary>
    /// Whether a machine's lamp shines in the story: as the story switched it (the director, a hero's Lamp key), else the
    /// Locator's on, a machine hanging switched off in a clamp of the deck (the dark rows, Ike and Bob before they wake) off,
    /// and any other on. Outside a story every lamp may shine (the view lights them by the dark alone).
    /// </summary>
    public bool StoryLampOn(int id)
    {
        if (Story is not { } run)
            return true;
        if (run.Sight.Lamps.TryGetValue(id, out var on))
            return on;
        if (Find<Machine>(id) is not { } machine)
            return true;
        if (run.Seats.Count > 0 && HeroOf(run.Locator)?.Id == id)
            return true;
        if (Deck is not { } deck)
            return true;
        _storyClamps ??= [.. deck.Markers.Where(m => m.Tags.Contains("clamp")).Select(m => m.Position)];
        foreach (var clamp in _storyClamps)
        {
            if (Vector2.DistanceSquared(clamp, machine.Position) < ClampReach * ClampReach)
                return false;
        }
        return true;
    }

    /// <summary>Where the deck's clamps hold their machines, worked out once.</summary>
    private Vector2[]? _storyClamps;

    /// <summary>How near a clamp's marker a machine hangs in it, metres.</summary>
    private const float ClampReach = 1.5f;

    /// <summary>Switches a machine's lamp for the story (the director: the scare machine's lamp comes on); null leaves it to <see cref="StoryLampOn"/>'s rule again.</summary>
    public void SetStoryLamp(int id, bool? on)
    {
        if (Story is not { } run)
            return;
        if (on is { } value)
            run.Sight.Lamps[id] = value;
        else
            run.Sight.Lamps.Remove(id);
    }

    internal string? SwitchStoryLamp(StoryLampCommand command)
    {
        var run = Story!;
        var player = command.Issuer ?? run.Locator;
        if (!run.Seats.Contains(player))
            return "only the trio has lamps";
        if (HeroOf(player) is not { } hero)
            return "the hero is down";
        run.Sight.Lamps[hero.Id] = command.On ?? !StoryLampOn(hero.Id);
        return null;
    }

    // ---- the heroes' signature -----------------------------------------------------------------------

    /// <summary>How loud and visible a hero of the story is this tick (0.05 still and dark, up to about 3 after a shot with the lamp on); 0 for any other.</summary>
    public float StorySignature(int hero) => Story?.Sight.Signatures.GetValueOrDefault(hero) ?? 0;

    /// <summary>The trio's heroes the ship can find: standing, out on the deck, neither asleep in a clamp nor lying down, in seat order.</summary>
    private List<(PlayerId Seat, WiredState State, Machine Hero)> StoryQuarries(StoryRun run)
    {
        var heroes = new List<(PlayerId, WiredState, Machine)>();
        foreach (var seat in run.Seats)
        {
            if (Wired?.Heroes.GetValueOrDefault(seat) is not { } state || HeroOf(state) is not { Inside: null } hero
                || run.Flags.Contains(StoryMarks.Dormant(seat)) || run.Flags.Contains(StoryMarks.Down(seat)))
                continue;
            heroes.Add((seat, state, hero));
        }
        return heroes;
    }

    private void UpdateSignatures(StoryRun run, List<(PlayerId Seat, WiredState State, Machine Hero)> heroes)
    {
        var sight = run.Sight;
        var standing = new HashSet<int>();
        foreach (var (seat, state, hero) in heroes)
        {
            standing.Add(hero.Id);
            var was = sight.Last.TryGetValue(hero.Id, out var last) ? last : hero.Position;
            var share = Math.Clamp(Vector2.Distance(was, hero.Position) / Dt / MathF.Max(1, hero.Stats.Speed), 0, 1);
            var signature = StorySight.Still + StorySight.Moving * share * (hero.Stats.Locomotion == Locomotion.Wheels ? StorySight.Wheels : 1);
            if (StoryLampOn(hero.Id))
                signature += StorySight.Lamp;
            if (Time - state.LastShotAt < StorySight.ShotFor)
                signature += StorySight.Shot;
            if (Time - (state.LocateReadyAt - WiredBalance.LocateCooldown) < StorySight.PulseFor)
                signature += StorySight.Pulse;
            if (run.Hacks.LastAlarmBy == seat && run.Hacks.LastAlarmTick >= 0 && (Tick - run.Hacks.LastAlarmTick) * Dt < StorySight.TracedFor)
                signature += StorySight.Traced;
            // A honk (the companions' distraction) is heard as far as its loudness carries, and drawn to its maker.
            if (run.Crew.Noises.LastOrDefault(n => n.Source == hero.Id) is { } noise && (Tick - noise.Tick) * Dt < StorySight.NoiseFor)
                signature += StorySight.Noise * noise.Loudness;
            sight.Signatures[hero.Id] = signature;
            sight.Last[hero.Id] = hero.Position;
        }
        foreach (var gone in sight.Signatures.Keys.Where(id => !standing.Contains(id)).ToList())
        {
            sight.Signatures.Remove(gone);
            sight.Last.Remove(gone);
        }
    }

    // ---- the seekers -----------------------------------------------------------------------------------

    /// <summary>What the story keeps of a seeker's eyes and suspicion; null for a machine that is none (yet).</summary>
    public StorySeeker? StorySeekerOf(int id) => Story?.Sight.Seekers.GetValueOrDefault(id);

    /// <summary>
    /// Makes a machine a seeker of the story (every fighting machine of the ship's becomes one on its first tick by itself):
    /// the Thresher (Reaper 4) with a searchlight and, in the prologue, nothing can hurt it. The director may change both,
    /// and blind a machine switched off in a clamp row.
    /// </summary>
    public StorySeeker StoryWatch(Machine machine)
    {
        var run = Story ?? throw new InvalidOperationException("only a story has seekers");
        var sight = run.Sight;
        if (sight.Seekers.TryGetValue(machine.Id, out var known))
            return known;
        var thresher = machine.Spec.TypeName == "AGGRESSOR ASSASSIN" && machine.Stats.HardwareLevel == 4;
        var prologue = run.Options.Chapter == StoryOptions.Prologue;
        var seeker = new StorySeeker(machine.Id)
        {
            Searchlight = thresher,
            Unkillable = thresher && prologue,
            // The horror of the decks draws fast and hits whole; the Commander leads his squad and leaves the killing to it.
            Draw = thresher ? StorySight.ThresherDraw : StorySight.Draw,
            Harm = thresher ? 1 : StorySight.HunterHarm,
            Caller = prologue && machine.Spec.TypeName == "ADMINISTRATOR COMMANDER",
        };
        sight.Seekers[machine.Id] = seeker;
        return seeker;
    }

    /// <summary>Whether a machine of another race than the trio's watches for it by itself: it fights, is the trio's enemy, and nobody drives it.</summary>
    private bool WatchesByItself(StoryRun run, Machine machine) =>
        !machine.IsDead && machine.CanFight && !machine.DrivenByPlayer && machine.Race != run.Options.Race && AreEnemies(machine.Race, run.Options.Race);

    /// <summary>How suspicious the most suspicious seeker is of a hero (0 to 1): the HUD's ticker and the music's threat.</summary>
    public float StorySuspicionOf(int hero) =>
        Story?.Sight.Seekers.Values.Where(s => s.Quarry == hero).Select(s => s.Suspicion).DefaultIfEmpty(0).Max() ?? 0;

    /// <summary>
    /// What <paramref name="seeker"/> makes of <paramref name="hero"/> now (<see cref="StoryGlimpse"/>). Within its reach (half
    /// its scanner's in the deck's dark, all of it with night eyes: its own, or a commander within 100 m) and with a clear line
    /// it sees the hero when lit: by its own searchlight's beam within 40 m, a lit room, the hero's lamp, or within 15 m a
    /// room's emergency light; a searchlight looks only inside its cone (25° either way of where its upper body points). Nearer
    /// than touch (6 m, widened by the signature) it sees it whatever the light or way it looks. Otherwise it hears it within
    /// 30 m a point of signature, half that through a wall. Changes nothing.
    /// </summary>
    public StoryGlimpse StoryPerceives(Machine seeker, Machine hero)
    {
        if (Story is not { } run || seeker.IsDead || hero.IsDead || hero.Inside is not null || seeker.Inside is not null)
            return StoryGlimpse.None;
        var eyes = run.Sight.Seekers.GetValueOrDefault(seeker.Id);
        if (eyes?.Blind == true)
            return StoryGlimpse.None;
        var signature = StorySignature(hero.Id);
        var distance = Vector2.Distance(seeker.Position, hero.Position);
        var nightEyes = seeker.Stats.NightVision || CommanderFor(seeker) is { } commander && Vector2.Distance(commander.Position, seeker.Position) < 100;
        var reach = seeker.Stats.ScannerRange * (nightEyes ? 1 : 0.5f);
        var touch = StorySight.Touch * (1 + signature) + hero.Radius;
        var hearing = StorySight.Hearing * signature;
        if (distance > MathF.Max(reach, MathF.Max(touch, hearing)))
            return StoryGlimpse.None;
        var clear = ClearShot(seeker, hero);
        if (clear && distance <= touch)
            return StoryGlimpse.Seen;
        if (clear && distance <= reach && (eyes is not { Searchlight: true } || InBeam(seeker, hero)))
        {
            var light = StoryLightAt(hero.Position);
            var lit = nightEyes || light == "on" || StoryLampOn(hero.Id)
                || eyes is { Searchlight: true } && distance <= StorySight.BeamLength && StoryLampOn(seeker.Id)
                || light == "emergency" && distance <= StorySight.Dim;
            if (lit)
                return StoryGlimpse.Seen;
        }
        return distance <= (clear ? hearing : hearing / 2) ? StoryGlimpse.Heard : StoryGlimpse.None;
    }

    /// <summary>Whether a hero stands inside a searchlight's cone: within its half-angle of where the seeker's upper body points.</summary>
    private bool InBeam(Machine seeker, Machine hero)
    {
        var way = hero.Position - seeker.Position;
        if (way.LengthSquared() < 0.01f)
            return true;
        var off = MathF.IEEERemainder(DetMath.Atan2(way.Y, way.X) - GunHeading(seeker), 2 * MathF.PI);
        return MathF.Abs(off) <= StorySight.BeamHalfAngle * MathF.PI / 180;
    }

    /// <summary>
    /// The seam in <see cref="NearestEnemy"/>: a machine of the ship's picks out a hero of the trio only once it has spotted it
    /// (full suspicion) and sees it now. Anything else, and any other pair, it picks out as ever.
    /// </summary>
    private bool StoryHides(Actor from, Actor target)
    {
        if (Story is not { } run || target is not Machine hero || hero.Race != run.Options.Race || from is not Machine seeker || seeker.Race == run.Options.Race
            || !run.Seats.Any(seat => Wired?.Heroes.GetValueOrDefault(seat)?.Hero == hero.Id))
            return false;
        return run.Sight.Seekers.GetValueOrDefault(seeker.Id) is not { Suspicion: >= 1 } eyes || !eyes.Sees.Contains(hero.Id);
    }

    /// <summary>
    /// A tick of the story's stealth, first in the story's tick so the director reads what came of it: the heroes'
    /// signatures, new seekers watched and gone ones forgotten, then each seeker in id order looks and listens, grows more
    /// or less suspicious, and hunts, searches, loses its quarry or calms down.
    /// </summary>
    private void UpdateSight()
    {
        var run = Story!;
        var sight = run.Sight;
        sight.News.RemoveAll(news => news.Tick != Tick);
        var heroes = StoryQuarries(run);
        UpdateSignatures(run, heroes);
        foreach (var machine in Machines.Where(m => !sight.Seekers.ContainsKey(m.Id) && WatchesByItself(run, m)).OrderBy(m => m.Id).ToList())
            StoryWatch(machine);
        foreach (var gone in sight.Seekers.Keys.Where(id => Find<Machine>(id) is not { IsDead: false }).ToList())
            sight.Seekers.Remove(gone);

        var spotted = false;
        // The ship alert after a call while the trio sneaks: suspicion lingers (StoryMarks.Alert).
        var alert = StoryMarks.Alerted(run, Time);
        foreach (var eyes in sight.Seekers.Values)
        {
            var seeker = Find<Machine>(eyes.Id)!;
            eyes.Sees.Clear();
            var (glimpse, quarry) = (StoryGlimpse.None, (Machine?)null);
            if (!eyes.Blind && !seeker.DrivenByPlayer && seeker.Inside is null)
            {
                foreach (var (_, _, hero) in heroes)
                {
                    var now = StoryPerceives(seeker, hero);
                    if (now == StoryGlimpse.Seen)
                        eyes.Sees.Add(hero.Id);
                    // The stronger glimpse wins; of two alike the hero it had noticed already, then the nearer.
                    if (now > glimpse || now == glimpse && now != StoryGlimpse.None && quarry is not null && quarry.Id != eyes.Quarry
                        && (hero.Id == eyes.Quarry || Vector2.DistanceSquared(hero.Position, seeker.Position) < Vector2.DistanceSquared(quarry.Position, seeker.Position)))
                        (glimpse, quarry) = (now, hero);
                }
            }
            eyes.Suspicion = Math.Clamp(eyes.Suspicion + Dt * glimpse switch
            {
                StoryGlimpse.Seen => StorySight.RiseSeen,
                StoryGlimpse.Heard => StorySight.RiseHeard,
                _ => -StorySight.Fall * (alert ? StorySight.AlertFall : 1),
            }, 0, 1);
            // Since when it has each hero it sees in its sights, sure of it (its spotting's stinger comes then): it draws on
            // one before it fires (StoryHoldsFire), and a hero out of its sight gets that moment again.
            foreach (var gone in eyes.SightSince.Keys.Where(id => !eyes.Sees.Contains(id)).ToList())
                eyes.SightSince.Remove(gone);
            if (eyes.Suspicion >= 1)
            {
                foreach (var seen in eyes.Sees)
                    eyes.SightSince.TryAdd(seen, Time);
            }
            if (quarry is not null)
            {
                eyes.Quarry = quarry.Id;
                eyes.Spot = quarry.Position;
                if (glimpse == StoryGlimpse.Seen)
                    eyes.SeenAt = Time;
            }
            if (!eyes.Wary && eyes.Suspicion >= StorySight.WaryAt)
            {
                eyes.Wary = true;
                Tell(StorySightKind.Suspicious, eyes);
            }
            React(run, seeker, eyes, glimpse, quarry);
            if (seeker.Order is AttackOrder { Target: var target } && eyes.Sees.Contains(target))
                spotted = true;
        }
        // The HUD's SIGNATURE DETECTED (StoryMarks.Spotted) while a hunter has its quarry in sight.
        if (spotted)
            run.Flags.Add(StoryMarks.Spotted);
        else
            run.Flags.Remove(StoryMarks.Spotted);
    }

    /// <summary>What a seeker does with what it saw or heard this tick.</summary>
    private void React(StoryRun run, Machine seeker, StorySeeker eyes, StoryGlimpse glimpse, Machine? quarry)
    {
        if (seeker.DrivenByPlayer)
            return;
        switch (seeker.Order)
        {
            case AttackOrder attack when Find<Machine>(attack.Target) is { } hunted && run.Seats.Any(seat => Wired?.Heroes.GetValueOrDefault(seat)?.Hero == hunted.Id):
                // On the hunt: it keeps its eyes on its quarry while it can, and goes to look where it saw it last when it cannot.
                if (eyes.Quarry != hunted.Id || eyes.SeenAt < 0)
                    (eyes.Quarry, eyes.Spot, eyes.SeenAt) = (hunted.Id, hunted.Position, Time);
                if (eyes.Sees.Contains(hunted.Id))
                {
                    (eyes.Spot, eyes.SeenAt, eyes.Suspicion) = (hunted.Position, Time, 1);
                    eyes.Wary = true;
                }
                else if (Time - eyes.SeenAt >= StorySight.LoseAfter)
                {
                    Search(seeker, eyes.Spot, attack.Back);
                    Tell(StorySightKind.Lost, eyes);
                }
                break;

            case SearchOrder search:
                if (eyes.Suspicion < 1 || quarry is null)
                    break;
                if (glimpse == StoryGlimpse.Seen)
                    Hunt(seeker, eyes, quarry, search.Back);
                else if (Vector2.DistanceSquared(quarry.Position, search.Spot) > SearchMoves * SearchMoves)
                    // It hears it somewhere else now: it looks there instead.
                    Search(seeker, quarry.Position, search.Back);
                break;

            case AttackOrder:
                // A fight of its own with something else: it sees to that first.
                break;

            default:
                if (eyes.Caller)
                {
                    // The leader does not hunt: it turns to what it sees and calls it out to its squad.
                    if (eyes.Suspicion >= 1 && quarry is not null && glimpse == StoryGlimpse.Seen)
                    {
                        Face(seeker, quarry.Position);
                        if (Call(run, seeker, eyes, quarry))
                            Tell(StorySightKind.Spotted, eyes);
                    }
                    else if (eyes.Wary && eyes.Suspicion <= 0)
                    {
                        eyes.Wary = false;
                        Tell(StorySightKind.Calm, eyes);
                    }
                }
                else if (eyes.Suspicion >= 1 && quarry is not null)
                {
                    if (glimpse == StoryGlimpse.Seen)
                        Hunt(seeker, eyes, quarry, PutAside(seeker));
                    else
                    {
                        Search(seeker, quarry.Position, PutAside(seeker));
                        Tell(StorySightKind.Searching, eyes);
                    }
                }
                else if (eyes.Wary && eyes.Suspicion <= 0)
                {
                    eyes.Wary = false;
                    Tell(StorySightKind.Calm, eyes);
                }
                break;
        }
    }

    /// <summary>How far from where it searches a seeker must hear a hero again to go and look there instead, metres.</summary>
    private const float SearchMoves = 8;

    /// <summary>It has spotted a hero: one that fights hunts it as a counterattack (what it did comes back after); one that cannot goes to look.</summary>
    private void Hunt(Machine seeker, StorySeeker eyes, Machine hero, Interrupted? back)
    {
        eyes.Suspicion = 1;
        (eyes.Quarry, eyes.Spot, eyes.SeenAt) = (hero.Id, hero.Position, Time);
        if (seeker.CanFight)
        {
            // From a search, the hunt goes back to what the search broke off.
            if (seeker.Order is SearchOrder && back is not null)
            {
                var orderedAt = seeker.OrderedAt;
                Send(seeker, new AttackOrder(hero.Id, Back: back with { Chases = true, TargetLastNotEvading = Time, NextClosingCheck = Time + ClosingCheckInterval }), hero.Position);
                seeker.OrderedAt = orderedAt;
            }
            else
                Counterattack(seeker, hero);
        }
        else
            Search(seeker, hero.Position, back);
        Tell(StorySightKind.Spotted, eyes);
        Call(Story!, seeker, eyes, hero);
    }

    /// <summary>
    /// A seeker that spotted a hero calls the others by its data link (docs/STORY.md, Klang: Datenfunk): every other seeker
    /// of the ship within <see cref="StorySight.CallReach"/> that is not hunting one of the trio already turns to the spot,
    /// wary; those within half the reach that can come go to search it. While the trio still sneaks (before the core's
    /// alarm) the ship stays alert for <see cref="StorySight.AlertFor"/> seconds (<see cref="StoryMarks.Alert"/>). The view
    /// hears a cue (<c>datalink</c>: the modem's screech) at the caller. A seeker calls at most once in
    /// <see cref="StorySight.CallEvery"/> seconds; false when it did not call now.
    /// </summary>
    private bool Call(StoryRun run, Machine caller, StorySeeker eyes, Machine hero)
    {
        if (eyes.CalledAt >= 0 && Time - eyes.CalledAt < StorySight.CallEvery)
            return false;
        eyes.CalledAt = Time;
        var spot = hero.Position;
        foreach (var other in run.Sight.Seekers.Values)
        {
            if (other.Id == eyes.Id || other.Blind || Find<Machine>(other.Id) is not { IsDead: false, DrivenByPlayer: false, Inside: null } machine
                || Vector2.Distance(machine.Position, caller.Position) > StorySight.CallReach
                || machine.Order is AttackOrder { Target: var aim } && run.Seats.Any(seat => Wired?.Heroes.GetValueOrDefault(seat)?.Hero == aim))
                continue;
            (other.Quarry, other.Spot) = (hero.Id, spot);
            other.Suspicion = MathF.Max(other.Suspicion, StorySight.WaryAt);
            if (!other.Wary)
            {
                other.Wary = true;
                Tell(StorySightKind.Suspicious, other);
            }
            if (!other.Caller && machine.Order is not SearchOrder && Vector2.Distance(machine.Position, caller.Position) <= StorySight.CallReach / 2)
                Search(machine, spot, PutAside(machine));
        }
        if (!run.Flags.Contains(StoryMarks.Alarm))
            run.Timers[StoryMarks.Alert] = Time + StorySight.AlertFor;
        StoryEvents.Cue(this, "datalink", spot, caller.Id);
        return true;
    }

    /// <summary>Whether a machine is the hero of one of the trio's seats.</summary>
    private bool IsTrioHero(StoryRun run, Machine machine) => run.Seats.Any(seat => Wired?.Heroes.GetValueOrDefault(seat)?.Hero == machine.Id);

    /// <summary>
    /// The seam in <see cref="UpdateCombat"/>: a seeker of the ship's holds its fire at a hero of the trio until it has drawn
    /// on it, <see cref="StorySeeker.Draw"/> seconds of seeing it without a break (the stinger of its spotting comes first,
    /// and a hero that breaks the line gets that moment again); one it does not see now, or further than
    /// <see cref="StorySight.FireReach"/>, it does not fire at; a caller (the Commander) never fires at the trio. It still
    /// aims, so the Grunts know whom to ram. Any other shot goes as ever.
    /// </summary>
    private bool StoryHoldsFire(Machine shooter, Actor? target)
    {
        if (Story is not { } run || target is not Machine hero || !IsTrioHero(run, hero) || run.Sight.Seekers.GetValueOrDefault(shooter.Id) is not { } eyes)
            return false;
        if (eyes.Caller || Vector2.Distance(shooter.Position, hero.Position) > StorySight.FireReach)
            return true;
        return !eyes.SightSince.TryGetValue(hero.Id, out var since) || Time - since < eyes.Draw;
    }

    /// <summary>The share of a hit a hero of the trio takes from a seeker of the ship's (<see cref="StorySeeker.Harm"/>): the story's difficulty; 1 for any other hit.</summary>
    private float StoryHarm(Actor target, Actor? dealer)
    {
        if (Story is not { } run || target is not Machine hero || dealer is null || !IsTrioHero(run, hero)
            || run.Sight.Seekers.GetValueOrDefault(dealer.Id) is not { } eyes)
            return 1;
        return eyes.Harm;
    }

    /// <summary>What a seeker puts aside to search: its round where it was going, an order with the end of its way, or where it stood.</summary>
    private static Interrupted PutAside(Machine machine) => machine.Order switch
    {
        PatrolOrder patrol => new Interrupted(patrol, patrol.Points[patrol.Next], Chases: false),
        IdleOrder => new Interrupted(new ReturnOrder(machine.Position, machine.Position + new Vector2(DetMath.Cos(machine.Facing), DetMath.Sin(machine.Facing)), 1), null, Chases: false),
        _ => new Interrupted(machine.Order, machine.PathIndex < machine.Path.Count ? machine.Path[^1] : machine.Position, Chases: false),
    };

    /// <summary>Sends a seeker to look about <paramref name="spot"/>; what a search broke off is what this one comes back to.</summary>
    private void Search(Machine seeker, Vector2 spot, Interrupted? back)
    {
        if (back?.Order is SearchOrder older)
            back = older.Back;
        var orderedAt = seeker.OrderedAt;
        Send(seeker, new SearchOrder(spot, Time + StorySight.SearchFor, back), spot);
        seeker.OrderedAt = orderedAt;
    }

    /// <summary>A searching machine walks to the spot, then looks about it, sweeping, and when its time is up takes up what it put aside.</summary>
    private void UpdateSearch(Machine machine, SearchOrder search)
    {
        if (search.Since is not { } since)
        {
            if (Time < search.Until && FollowPath(machine))
                return;
            machine.Path.Clear();
            machine.PathIndex = 0;
            machine.Order = search with { Since = Time, Until = MathF.Max(search.Until, Time + StorySight.SweepAtLeast), Facing = machine.Facing };
            return;
        }
        if (Time >= search.Until)
        {
            // It found nothing: it calms down at once, and goes back to its round.
            if (StorySeekerOf(machine.Id) is { } eyes)
            {
                (eyes.Suspicion, eyes.Wary) = (0, false);
                Tell(StorySightKind.Calm, eyes);
            }
            if (search.Back is { } back)
                TakeUpAgain(machine, back);
            else
                machine.Order = Order.Idle;
            return;
        }
        var swing = StorySight.SearchSweep * MathF.PI / 180 * DetMath.Sin(2 * MathF.PI * (Time - since) / StorySight.SearchPeriod);
        LookTowards(machine, search.Facing + swing);
    }

    /// <summary>Turns a machine where it stands to a heading (radians), at its own rate.</summary>
    private void LookTowards(Machine machine, float heading) =>
        Face(machine, machine.Position + new Vector2(DetMath.Cos(heading), DetMath.Sin(heading)) * 10);

    /// <summary>
    /// The seam at the head of a machine's order: a seeker of the story that is wary, but not yet sure, stops where it is and
    /// turns to what it noticed (on its round, idle, standing its ground or going back); one dwelling at a point of its
    /// round stands sweeping its beam. True while it holds so, and its order waits.
    /// </summary>
    private bool StoryHolds(Machine machine)
    {
        if (machine.Order is not (PatrolOrder or IdleOrder or StandGroundOrder or ReturnOrder) || StorySeekerOf(machine.Id) is not { } eyes || eyes.Blind)
            return false;
        if (eyes.Suspicion >= StorySight.WaryAt && eyes.Suspicion < 1 && eyes.Quarry != 0)
        {
            Face(machine, eyes.Spot);
            return true;
        }
        if (machine.Order is PatrolOrder && Time < eyes.DwellUntil)
        {
            var since = eyes.DwellUntil - StorySight.DwellTime;
            LookTowards(machine, eyes.DwellFacing + StorySight.DwellSweep * MathF.PI / 180 * DetMath.Sin(2 * MathF.PI * (Time - since) / StorySight.DwellTime));
            return true;
        }
        return false;
    }

    /// <summary>
    /// The seam where a patrol reaches a point of its round: at a dwell point (<see cref="PatrolOrder.Pauses"/>) a seeker of
    /// the story stands for <see cref="StorySight.DwellTime"/> seconds sweeping its beam either way of the point's heading
    /// (the marker's, or the way it came); true while it starts that. The next time it is asked it goes on.
    /// </summary>
    private bool StoryDwells(Machine machine, PatrolOrder patrol)
    {
        if (patrol.Pauses is not { } pauses || !pauses.Contains(patrol.Next) || StorySeekerOf(machine.Id) is not { } eyes)
            return false;
        if (eyes.DwelledAt == patrol.Next)
        {
            eyes.DwelledAt = -1;
            return false;
        }
        eyes.DwelledAt = patrol.Next;
        eyes.DwellUntil = Time + StorySight.DwellTime;
        eyes.DwellFacing = eyes.DwellAngles.TryGetValue(patrol.Next, out var angle) ? angle * MathF.PI / 180 : machine.Facing;
        return true;
    }

    /// <summary>
    /// Sends a machine on a round of the deck's markers <paramref name="prefix"/>_1, _2 … in turn (the Thresher's
    /// <c>patrol_thresher</c>), dwelling at those tagged <c>dwell</c> and sweeping about the marker's heading there.
    /// </summary>
    public PatrolOrder StoryPatrol(Machine machine, string prefix)
    {
        var deck = Deck ?? throw new InvalidOperationException("a round of the deck's markers needs a deck");
        var points = new List<DeckMarker>();
        for (var i = 1; deck.Markers.FirstOrDefault(m => m.Name == $"{prefix}_{i}") is { } marker; i++)
            points.Add(marker);
        if (points.Count == 0)
            throw new KeyNotFoundException($"deck {deck.Name} has no marker {prefix}_1");
        var pauses = Enumerable.Range(0, points.Count).Where(i => points[i].Tags.Contains("dwell")).ToList();
        if (Story is not null)
        {
            var eyes = StoryWatch(machine);
            eyes.DwellAngles.Clear();
            foreach (var i in pauses)
                eyes.DwellAngles[i] = points[i].Angle;
        }
        var patrol = new PatrolOrder([.. points.Select(p => p.Position)], 0, pauses);
        Send(machine, patrol, patrol.Points[0]);
        return patrol;
    }

    /// <summary>Notes what a seeker's suspicion came to (<see cref="StorySight.News"/>).</summary>
    private void Tell(StorySightKind kind, StorySeeker eyes) =>
        Story!.Sight.News.Add(new StorySightNews(Tick, kind, eyes.Id, eyes.Quarry, eyes.Spot, eyes.Suspicion));

    // ---- hits ------------------------------------------------------------------------------------------

    /// <summary>
    /// The seam in <see cref="ReactToHit"/>: a hit always gives the shooter away. A seeker hit by a hero of the trio is sure
    /// of it at once and sees it this tick, so it may turn on it.
    /// </summary>
    private void StoryStruck(Actor target, Actor shooter)
    {
        if (Story is not { } run || target is not Machine struck || shooter is not Machine hero || hero.Race != run.Options.Race
            || !run.Seats.Any(seat => Wired?.Heroes.GetValueOrDefault(seat)?.Hero == hero.Id))
            return;
        var eyes = run.Sight.Seekers.GetValueOrDefault(struck.Id) ?? (WatchesByItself(run, struck) ? StoryWatch(struck) : null);
        if (eyes is null || eyes.Blind)
            return;
        (eyes.Suspicion, eyes.Quarry, eyes.Spot, eyes.SeenAt) = (1, hero.Id, hero.Position, Time);
        eyes.Sees.Add(hero.Id);
        if (!eyes.Wary)
        {
            eyes.Wary = true;
            Tell(StorySightKind.Spotted, eyes);
        }
    }

    /// <summary>
    /// The story's veto on a hit (in WiredHit): nothing hurts an unkillable seeker (the prologue's Thresher). The hit still
    /// gives its dealer away. True when the hit is vetoed.
    /// </summary>
    private bool StoryShrugs(Actor target, Actor? dealer)
    {
        if (StorySeekerOf(target.Id) is not { Unkillable: true })
            return false;
        if (dealer is not null)
            ReactToHit(target, dealer);
        return true;
    }
}
