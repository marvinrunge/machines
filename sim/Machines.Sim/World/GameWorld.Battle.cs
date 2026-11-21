using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Survival;

namespace Machines.Sim.World;

/// <summary>
/// Battle, the remake's own team mode: two teams of heroes, one pod each, a short opening, then a fight until a team is
/// wiped out, has its pod taken or the time is up. The heroes are Wired's (a <see cref="WiredRun"/> in phase
/// <see cref="WiredPhase.Battle"/>, with no waves, shop or hangar); what is Battle's own is here. Nothing here runs
/// without <see cref="Battle"/>.
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>The battle, in a battle; null in every other match.</summary>
    public BattleRun? Battle { get; private set; }

    /// <summary>The computer's heroes in a battle, by player: they think in player order (with one player per race, race order).</summary>
    public PlayerMap<Ai.BattleBot> HeroBots { get; } = [];

    /// <summary>Whether a race won: in a battle with its team, elsewhere alone. False while the match lasts and on a draw.</summary>
    public bool Won(Race race) => Battle is { } battle ? battle.Won(race) : Winner == race;

    /// <summary>Whether a race lost: in a battle with its team, elsewhere when it is out. False while a battle lasts and on a draw.</summary>
    public bool Lost(Race race) => Battle is { } battle ? battle.Lost(race) : Races.TryGetValue(race, out var state) && state.Defeated;

    /// <summary>A team's pod in a battle, while it stands.</summary>
    public Construction? BattlePod(int team) =>
        Battle is { } battle && team is 0 or 1 && battle.Pods[team] is { } id && Find<Construction>(id) is { IsDead: false } pod ? pod : null;

    /// <summary>
    /// Starts the battle on the map of <paramref name="scenario"/>: every seat of the teams (<see cref="BattleSeats.Of"/>)
    /// gets its player's hero at its team's pod, the first at the door and the others beside it, and every bot of the table
    /// a bot. A seat the options name no hero for gets a bot's, composed for that map by <see cref="BattleBots.Compose"/> as
    /// the relay and the pod compose it; a hero that does not resolve is clamped to a bot's loadout. In today's layout the
    /// seats are the teams' races, each its own first player, and the bots the computer's races, all in race order.
    /// </summary>
    internal void StartBattle(BattleOptions options, string scenario)
    {
        var tier = Math.Clamp(options.Tier, 1, BattleCatalogue.MostTier);
        var sized = BattleSeats.Size(options) > 0;
        var teams = new[] { options.TeamA.Distinct().ToList(), options.TeamB.Distinct().ToList() };
        // With a team size only the anchors fight, as the match was set up (BattleSeats.Teams).
        if (sized)
            teams = [.. teams.Select(team => team.Take(1).ToList())];
        var fielded = options with { Tier = tier, TeamA = teams[0], TeamB = teams[1] };
        var composed = BattleBots.Compose(fielded, options.Heroes, scenario).Heroes;
        var seats = BattleSeats.Of(fielded);
        var heroes = new List<BattleHero>();
        foreach (var seat in seats)
        {
            var hero = sized ? composed.First(h => h.Player == seat.Player) : composed.First(h => h.Race == seat.Race);
            heroes.Add(BattleBots.Resolved(hero with { Race = seat.Race }, tier, seat.Place, scenario));
        }
        var played = options with
        {
            Tier = tier,
            Lives = Math.Clamp(options.Lives, 1, 5),
            RespawnWait = MathF.Max(0, options.RespawnWait),
            TimeLimit = Math.Max(1, options.TimeLimit),
            TeamA = teams[0],
            TeamB = teams[1],
            Heroes = heroes.OrderBy(h => h.Player ?? PlayerId.Of(h.Race)).ToList(),
        };
        Wired = new WiredRun(new WiredOptions()) { Phase = WiredPhase.Battle };
        var run = Battle = new BattleRun(played, teams[0], teams[1], Time + BattleBalance.Opening);
        for (var team = 0; team < 2; team++)
        {
            var anchor = teams[team][0];
            run.Pods[team] = Constructions.Where(c => c.Race == anchor && c.IsType("POD")).OrderBy(c => c.Id).FirstOrDefault()?.Id;
        }
        for (var i = 0; i < seats.Count; i++)
        {
            var (seat, hero) = (seats[i], heroes[i]);
            var pod = BattlePod(seat.Team);
            var state = WiredState.ForBattle(Data, seat.Race, hero, played.Lives);
            state.Player = seat.Player;
            Wired.Heroes[seat.Player] = state;
            run.Tally[seat.Player] = new BattleTally { Chassis = hero.Chassis };
            state.Pod = pod?.Id;
            if (pod is null)
                continue;
            var build = HeroBuild.Resolve(Data, state.Loadout);
            state.Hero = AddHeroAtPod(state, pod, build).Id;
        }
        foreach (var bot in Players.ById.Where(row => row.Kind == PlayerKind.Bot))
            AddBot(bot.Id);
    }

    /// <summary>How far apart heroes set down beside the pod's door stand, at least.</summary>
    private const float BesideTheDoor = 4;

    /// <summary>How far before a hero set down at the pod nobody may stand in its way, where that can be had.</summary>
    private const float ClearAhead = 40;

    /// <summary>
    /// A way round a hero standing at the wheel that a machine on its way to <paramref name="to"/> ran into (in a battle a
    /// player may hold the pod's door for minutes, and its mate set down beside it had to go past it along the pod's wall):
    /// one point or two out on either side, wider each time, each in sight and with nobody resting in the way, from where
    /// <paramref name="to"/> is in sight. Null when there is none; the machine then waits as before.
    /// </summary>
    private List<Vector2>? RoundTheWheel(Machine machine, Vector2 to, Machine standing)
    {
        var from = machine.Position;
        var travel = to - from;
        if (travel.LengthSquared() < 1e-6f)
            return null;
        var water = MobilityOf(machine);
        var direction = Vector2.Normalize(travel);
        var clearance = machine.Radius + standing.Radius;
        var hit = ContactPoint(from, direction, standing.Position, clearance);
        bool Clear(Vector2 a, Vector2 b) => Nav.LineOfSight(a, b, water) && RestingBlocker(machine, a, b) is null;
        bool Open(Vector2 p) => Nav.OnPlanet(p) && Nav.IsFree(p, water);
        foreach (var width in (ReadOnlySpan<float>)[1.5f, 2.5f, 3.5f])
        {
            foreach (var side in (ReadOnlySpan<int>)[1, -1])
            {
                var aside = hit + new Vector2(direction.Y, -direction.X) * (side * width * clearance);
                if (!Open(aside) || !Clear(from, aside))
                    continue;
                if (Clear(aside, to))
                    return [aside];
                // Past the one standing as well, then on.
                var past = aside + direction * (2 * clearance);
                if (Open(past) && Clear(aside, past) && Clear(past, to))
                    return [aside, past];
            }
        }
        return null;
    }

    /// <summary>
    /// The ways from straight ahead, in sixteenths of a turn, that a hero set down at the pod is put aside when the door is
    /// taken: square to the side first, then a little back or forth, never in front of the one at the door; the side away
    /// from the enemy pod first, so that it does not stand on the way its mate at the door sets out on.
    /// </summary>
    private static readonly int[] Aside = [4, -4, 5, -5, 3, -3, 6, -6];

    /// <summary>A player's hero set down at its team's pod, looking out of the door (<see cref="BattleSpot"/>).</summary>
    private Machine AddHeroAtPod(WiredState state, Construction pod, HeroBuild build)
    {
        var enemyPod = Battle!.TeamOf(state.Player) is var team and >= 0 ? BattlePod(1 - team)?.Position : null;
        var hero = AddHero(state.Race, BattleSpot(pod, Machine.RadiusOf(build.Stats), enemyPod, out var facing), build, state.Player);
        hero.Facing = facing;
        return hero;
    }

    /// <summary>
    /// Where a hero of <paramref name="radius"/> is set down at its team's pod, and which way it looks: out of the door, away
    /// from the pod. It stands at the door, or where that is taken beside it, square to the way it looks, so that a team-mate
    /// set down there stands at its side and not in front of it; a spot overlaps no machine, the largest standing about
    /// included. Only where nothing beside is free does it take the nearest free spot anywhere.
    /// </summary>
    private Vector2 BattleSpot(Construction pod, float radius, Vector2? enemyPod, out float facing)
    {
        var door = pod.EntranceOutside ?? FreePointNear(pod);
        var outward = door - pod.Position;
        facing = outward.LengthSquared() > 1 ? DetMath.Atan2(outward.Y, outward.X) : 0;
        // The enemy to the left (a positive turn) puts the mate to the right first, and the other way round.
        var toEnemy = (enemyPod ?? door) - door;
        var away = outward.X * toEnemy.Y - outward.Y * toEnemy.X > 0 ? -1 : 1;
        var largest = Machines.Where(m => m.Inside is null && !m.IsDead).Select(m => m.Radius).DefaultIfEmpty(0).Max();
        var clearance = MathF.Max(BesideTheDoor, (radius + largest) / 2);
        var taken = Machines.Where(m => m.Inside is null && !m.IsDead)
            .SelectMany(m => m.Path.Count > 0 ? new[] { m.Position, m.Path[^1] } : [m.Position])
            .ToList();
        // The whole disc on open ground, not only its middle (a gorilla stood with a third of itself in rock).
        bool Open(Vector2 p) => Nav.IsFree(p, crossesWater: false)
            && Enumerable.Range(0, 8).All(k => Nav.IsFree(p + new Vector2(DetMath.Cos(k * MathF.Tau / 8), DetMath.Sin(k * MathF.Tau / 8)) * radius * 0.9f, crossesWater: false));
        bool Free(Vector2 p) => Open(p) && taken.All(t => Vector2.DistanceSquared(t, p) >= 4 * clearance * clearance);
        // Nobody standing, or on its way to stand, right before it the way it looks: a mate guarding out of the door filled
        // the view of a hero coming back there. Where every spot beside has someone ahead, one that is only free will do.
        var (ahead, sideways) = (new Vector2(DetMath.Cos(facing), DetMath.Sin(facing)), radius + largest + 2);
        bool Clear(Vector2 p) => taken.All(t =>
        {
            var way = t - p;
            var along = Vector2.Dot(way, ahead);
            return along <= 0 || along > ClearAhead || MathF.Abs(way.X * ahead.Y - way.Y * ahead.X) >= sideways;
        });
        foreach (var clearAhead in (ReadOnlySpan<bool>)[true, false])
        {
            if (Free(door) && (!clearAhead || Clear(door)))
                return door;
            for (var ring = 1; ring <= 6; ring++)
            {
                foreach (var turn in Aside)
                {
                    var angle = facing + away * turn * MathF.Tau / 16;
                    var candidate = door + new Vector2(DetMath.Cos(angle), DetMath.Sin(angle)) * (ring * 2.2f * clearance);
                    if (Free(candidate) && (!clearAhead || Clear(candidate)))
                        return candidate;
                }
            }
        }
        return FreeSpotNear(door, clearance);
    }

    /// <summary>The computer takes a player's hero: it holds its target and does not run.</summary>
    private void AddBot(PlayerId player)
    {
        if (Battle is not { } run || HeroBots.ContainsKey(player))
            return;
        run.Bots.Add(player);
        HeroBots[player] = new Ai.BattleBot(this, player);
        if (HeroOf(player) is { } hero)
        {
            if (hero.DrivenByPlayer)
            {
                // As a DriveCommand that lets go of the wheel: the driver's last input goes with it.
                LetGoInside(hero);
                hero.DrivenByPlayer = false;
                ForgetDriver(hero);
            }
            StartAfresh(hero);
            hero.DefCon = DefCon.High;
        }
    }

    /// <summary>A player takes their hero back from the computer.</summary>
    private void RemoveBot(PlayerId player)
    {
        if (Battle is not { } run || !HeroBots.Remove(player))
            return;
        run.Bots.Remove(player);
        if (HeroOf(player) is { } hero)
        {
            StartAfresh(hero);
            hero.DefCon = DefCon.Normal;
        }
    }

    /// <summary>The computer's heroes think, each in turn.</summary>
    private void ThinkBots()
    {
        if (Battle is { Over: true })
            return;
        foreach (var bot in HeroBots.Values)
            bot.Think();
    }

    /// <summary>A tick of the battle: the clock and the capture of the pods.</summary>
    private void UpdateBattle()
    {
        var run = Battle!;
        if (run.Over)
            return;
        run.Elapsed = MathF.Max(0, Time - run.OpeningEnds);
        if (run.InOpening(Time))
            return;
        for (var team = 0; team < 2; team++)
            UpdateCapture(run, team);
    }

    /// <summary>
    /// The team's heroes in the other team's circle add to their shares, a point a second each; one that is out of it, or
    /// down, loses its share. Defenders in the circle do not hold it up.
    /// </summary>
    private void UpdateCapture(BattleRun run, int team)
    {
        var pod = BattlePod(1 - team);
        var before = run.Progress(team);
        foreach (var player in run.Members[team])
        {
            var state = Wired?.Heroes.GetValueOrDefault(player);
            var hero = state is null ? null : HeroOf(state);
            var inside = pod is not null && hero is { Inside: null } && Vector2.Distance(hero.Position, pod.Position) <= BattleBalance.CaptureRadius;
            if (!inside)
            {
                LoseShare(run, player, hero, hero is null ? "down" : "left");
                continue;
            }
            // In the other team's circle a hero is seen, shadow or not: no pod is taken unseen.
            Reveal(state!);
            if (run.Options.Win == BattleWin.Wipe)
                continue;
            run.Shares[player] = BattleRun.Gained(run.Shares.GetValueOrDefault(player));
            run.Tally[player].Capture = run.Shares[player];
        }
        var after = run.Progress(team);
        if (pod is not null && after > before && (before == 0 || (int)(after / 10) > (int)(before / 10)))
            Raise(new GameEvent(GameEventKind.BattleCaptureProgress, pod.Id, pod.Position, team, ((int)after).ToString()));
    }

    /// <summary>A player's hero's share of a capture is gone, and the capture points it was worth with it.</summary>
    private void LoseShare(BattleRun run, PlayerId player, Machine? hero, string why)
    {
        if (!run.Shares.Remove(player))
            return;
        if (run.Tally.GetValueOrDefault(player) is { } tally)
            tally.Capture = 0;
        var race = Wired?.Heroes.GetValueOrDefault(player)?.Race ?? Players.RaceOf(player);
        Raise(new GameEvent(GameEventKind.BattleCaptureReset, hero?.Id ?? 0, hero?.Position ?? Vector2.Zero, (int)race, why, Player: player));
    }

    /// <summary>
    /// Whether a hit lands at all in a battle: the pods take none, and no race hurts its own or an ally's, with rounds or with
    /// blasts. The round still stops at the first it meets.
    /// </summary>
    private bool BattleHits(Actor target, Race? by) =>
        target is Artefact || target is not Construction && !(by is { } race && !AreEnemies(race, target.Race));

    /// <summary>
    /// A hit landed on a hero in a battle: a capturer loses its share, and the player who dealt it counts the damage and the
    /// kill: of the race that dealt it, the player whose round or blast it was (<paramref name="hitter"/>), else the race's
    /// first player (<see cref="Credited"/>).
    /// </summary>
    private void BattleStruck(Actor target, int lost, Race? by, PlayerId? hitter)
    {
        var run = Battle!;
        if (run.Over || target is not Machine hero || HeroStateOf(hero) is not { } state)
            return;
        var victim = state.Player;
        if (lost > 0)
            LoseShare(run, victim, hero, "hit");
        // Only an enemy counts the damage and the kill; a hero that falls to anything else (a tree, its own blast) still falls.
        var killer = by is { } race && AreEnemies(race, target.Race) && Credited(race, hitter) is { } credited && run.Tally.ContainsKey(credited)
            ? credited : (PlayerId?)null;
        if (killer is { } enemy)
            run.Tally[enemy].Damage += lost;
        if (!hero.IsDead)
            return;
        if (killer is { } last)
            run.Tally[last].Killed.Add(run.Tally.GetValueOrDefault(victim)?.Chassis ?? hero.Spec.TypeName);
        Raise(new GameEvent(GameEventKind.BattleHeroKilled, hero.Id, hero.Position, killer is null ? null : (int)by!.Value, target.Race.ToString(),
            Player: victim, By: killer));
    }

    /// <summary>
    /// The end, checked each tick in this order: a team with no hero standing and none to come loses (both at once is a
    /// draw); a team that took the other's pod wins; when the time is up it is a draw. With victory by taking the pod alone
    /// a team wiped out has not lost yet: the other must still take its pod (only both wiped out is the end, a draw).
    /// </summary>
    private void CheckBattleEnd()
    {
        if (Battle is not { Over: false } run)
            return;
        var wiped = new[] { Wiped(run, 0), Wiped(run, 1) };
        if (run.Options.Win == BattleWin.Capture ? wiped[0] && wiped[1] : wiped[0] || wiped[1])
            EndBattle(run, wiped[0] && wiped[1] ? null : wiped[0] ? 1 : 0, BattleEnd.Wipe);
        else if (run.Options.Win != BattleWin.Wipe && (run.Progress(0) >= run.CaptureTarget || run.Progress(1) >= run.CaptureTarget))
        {
            var (a, b) = (run.Progress(0) >= run.CaptureTarget, run.Progress(1) >= run.CaptureTarget);
            EndBattle(run, a && b ? null : a ? 0 : 1, BattleEnd.Capture);
        }
        else if (!run.InOpening(Time) && run.Elapsed >= run.Options.TimeLimit)
            EndBattle(run, null, BattleEnd.Time);
    }

    /// <summary>Whether no hero of a team stands and none has a body to come; a body comes only to a pod that stands.</summary>
    private bool Wiped(BattleRun run, int team) =>
        run.Members[team].All(player => Wired!.Heroes.GetValueOrDefault(player) is not { } state
            || HeroOf(state) is null && (state.RespawnAt is null || WiredPod(state) is null));

    private void EndBattle(BattleRun run, int? winner, BattleEnd reason)
    {
        run.Result = new BattleResult(winner, reason, Time);
        foreach (var (player, tally) in run.Tally)
            tally.AliveAtEnd = HeroOf(player) is not null;
        // A hero that fell waits for no body: none comes now.
        foreach (var state in Wired!.Heroes.Values)
            state.RespawnAt = null;
        if (winner is { } team)
        {
            foreach (var race in run.Teams[1 - team].Where(r => Races.TryGetValue(r, out var state) && !state.Defeated))
            {
                Races[race].Defeated = true;
                Raise(new GameEvent(GameEventKind.RaceDefeated, 0, Vector2.Zero, Detail: race.ToString()));
            }
        }
        Raise(new GameEvent(GameEventKind.BattleEnded, 0, Vector2.Zero, winner, reason.ToString()));
    }

    /// <returns>Why a command is refused in a battle, or null: the teams are fixed, and in the opening nothing but looking about is allowed.</returns>
    private string? WhyNotInBattle(Command command)
    {
        if (Battle is not { } run)
            return null;
        if (WhyNotBorrowed(command) is { } refused)
            return refused;
        if (run.InOpening(Time) && command is not (DriveCommand or DefConCommand))
            return "the battle has not begun yet";
        return null;
    }

    /// <returns>null if the story takes the command, otherwise why not: what a battle refuses of every match that borrows the heroes.</returns>
    private string? WhyNotInStory(Command command) => WhyNotBorrowed(command);

    /// <returns>
    /// What a battle and the story refuse alike, each in its own words: the sides are fixed (a battle's teams; the trio and the
    /// ship), and nothing self-destructs. In a battle a pod blown up by its own side could be neither taken nor its team
    /// wiped, and a hero that blew itself up would rob its killer of the kill; in the story the Locator's own end would be a
    /// way out of the chapter.
    /// </returns>
    private string? WhyNotBorrowed(Command command) => command switch
    {
        AllianceCommand => Battle is not null ? "teams are fixed in a battle" : "sides are fixed in the story",
        SelfDestructCommand => Battle is not null ? "nothing self-destructs in a battle" : "nothing self-destructs in the story",
        _ => null,
    };

    /// <summary>In the opening a driver may look about but neither drive nor fire.</summary>
    private DriveCommand HeldInOpening(DriveCommand drive) =>
        Battle is { } run && run.InOpening(Time) && drive.Driving
            ? drive with { Forward = 0, Turn = 0, Strafe = 0, Weapons = 0, Buttons = false }
            : drive;

    /// <summary>The battle's state for the checksum.</summary>
    private long BattleFingerprint() => Battle!.Fingerprint();
}
