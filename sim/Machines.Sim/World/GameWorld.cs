using System.Numerics;
using Machines.Import.Model;
using Machines.Import.Planets;
using Machines.Sim.Combat;
using Machines.Sim.Navigation;

namespace Machines.Sim.World;

/// <summary>
/// The whole simulation state of one match. It advances in fixed steps of <see cref="Dt"/> seconds and is
/// changed only through <see cref="Issue"/> commands, so the client and the AI never touch it directly.
/// </summary>
public sealed partial class GameWorld
{
    public const int TicksPerSecond = 20;
    public const float Dt = 1f / TicksPerSecond;

    private readonly Dictionary<int, Actor> _actors = [];
    private readonly Queue<Command> _pending = new();
    private readonly List<GameEvent> _events = [];
    private readonly List<CommandRejected> _rejected = [];
    private int _nextId = 1;

    public GameWorld(GameData data, Planet planet, int seed)
    {
        Data = data;
        Planet = planet;
        Random = new Random(seed);
        Nav = new NavGrid(planet.Surface.Width, planet.Surface.Depth, planet.ConfigSpace.Obstacles);
    }

    public GameData Data { get; }
    public Planet Planet { get; }
    public NavGrid Nav { get; }
    public Random Random { get; }

    public int Tick { get; private set; }
    public float Time => Tick * Dt;

    public Dictionary<Race, RaceState> Races { get; } = [];
    public List<MineralSiteState> Sites { get; } = [];
    public List<Ai.AiController> AiControllers { get; } = [];

    /// <summary>The AI that plays each race when no one does (its scenario's script), also for players' races.</summary>
    public Dictionary<Race, Func<Ai.AiController>> StandIns { get; } = [];

    /// <summary>The AI takes over a player's race, e.g. when the player left a network match.</summary>
    public void HandOverToAi(Race race)
    {
        if (!Races.TryGetValue(race, out var state) || state.Controller == Controller.Ai || !StandIns.TryGetValue(race, out var standIn))
            return;
        state.Controller = Controller.Ai;
        AiControllers.Add(standIn());
    }

    /// <summary>A player takes their race back from the AI (they rejoined a network match).</summary>
    public void HandBackFromAi(Race race)
    {
        if (!Races.TryGetValue(race, out var state) || state.Controller != Controller.Ai)
            return;
        state.Controller = Controller.Player;
        AiControllers.RemoveAll(ai => ai.Race == race);
    }

    /// <summary>Building plans from the scenario that an AI race may build (<c>NOT_BUILT</c> constructions).</summary>
    public Dictionary<Race, List<Import.Model.ScenarioConstruction>> BuildingPlans { get; } = [];

    public IEnumerable<Actor> Actors => _actors.Values;
    public IEnumerable<Machine> Machines => _actors.Values.OfType<Machine>();
    public IEnumerable<Construction> Constructions => _actors.Values.OfType<Construction>();

    /// <summary>Events raised during the last <see cref="Step"/>.</summary>
    public IReadOnlyList<GameEvent> Events => _events;

    /// <summary>Commands refused during the last <see cref="Step"/>, with the reason.</summary>
    public IReadOnlyList<CommandRejected> Rejected => _rejected;

    public Race? Winner { get; private set; }

    public MatchOptions Options { get; set; } = new();

    /// <summary>Seconds left in a timed game, otherwise null.</summary>
    public float? TimeLeft => Options.Victory == VictoryCondition.Timer ? MathF.Max(0, Options.TimeLimit - Time) : null;

    public Actor? Find(int id) => _actors.GetValueOrDefault(id);

    public T? Find<T>(int id) where T : Actor => _actors.GetValueOrDefault(id) as T;

    public void Issue(Command command) => _pending.Enqueue(command);

    /// <summary>Advances the world by one tick.</summary>
    public void Step()
    {
        _events.Clear();
        _rejected.Clear();

        while (_pending.TryDequeue(out var command))
        {
            var reason = Apply(command);
            if (reason is not null)
                _rejected.Add(new CommandRejected(command, reason));
        }

        Nav.NewTick();
        Timed("index", IndexActors);

        if (Tick % TicksPerSecond == 0)
        {
            Timed("ai", () =>
            {
                foreach (var ai in AiControllers)
                    ai.Think(this);
            });
        }

        Timed("machines", () =>
        {
            foreach (var machine in Machines.ToList())
            {
                if (!machine.IsDead && machine.Inside is null && Profile is null)
                    UpdateMachine(machine);
                else if (!machine.IsDead && machine.Inside is null)
                    Timed("  " + machine.Order.GetType().Name, () => UpdateMachine(machine));
            }
        });

        Timed("economy", UpdateEconomy);
        Timed("support", UpdateSupport);
        Timed("transport", UpdateTransport);
        Timed("combat", UpdateCombat);
        Timed("evasion", UpdateEvasion);
        Timed("squadrons", UpdateSquadAttacks);
        Timed("mines", UpdateLandMines);
        Timed("spies", UpdateSpies);
        Timed("dead", RemoveDead);
        Timed("debris", BurnOutDebris);
        Timed("victory", CheckVictory);

        Tick++;
    }

    /// <summary>For profiling (tools/sim-bench): milliseconds spent in each part of the tick, summed, when set.</summary>
    public Dictionary<string, double>? Profile { get; set; }

    private void Timed(string part, Action action)
    {
        if (Profile is null)
        {
            action();
            return;
        }
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        action();
        Profile[part] = Profile.GetValueOrDefault(part) + System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    // ---- creating actors ------------------------------------------------------------------------

    public Machine AddMachine(Race race, Vector2 position, string type, string? subType, int level, string? weaponCombo)
    {
        var spec = Data.Parm.Machine(subType is null ? type : $"{type} {subType}");
        var machine = new Machine(_nextId++, race, position, spec, spec.Level(level), weaponCombo);
        // The AI's machines take the initiative, players' wait for a reason (MachLogScenario: DEFCON_LOW, DEFCON_NORMAL).
        if (Races.TryGetValue(race, out var owner) && owner.Controller == Controller.Ai)
            machine.DefCon = DefCon.Low;
        machine.Mines = machine.Stats.MineCapacity ?? 0;
        if (weaponCombo is not null)
        {
            foreach (var (mounting, weapon) in WeaponCombos.Split(weaponCombo))
            {
                var weaponSpec = Data.Parm.Weapons.FirstOrDefault(w => w.TypeName == weapon);
                if (weaponSpec is not null)
                    machine.Weapons.Add(new WeaponMount(mounting, weaponSpec));
            }
        }
        _actors.Add(machine.Id, machine);
        return machine;
    }

    public Construction AddConstruction(Race race, Vector2 position, string type, string? subType, int level, int orientation, bool complete, string? weaponCombo = null)
    {
        var spec = Data.Parm.Construction(subType is null ? type : $"{type} {subType}");
        var construction = new Construction(_nextId++, race, position, spec, spec.Level(level), orientation);
        if (complete)
            construction.MakeComplete();

        // Turrets and missile emplacements fire the weapons of their combo; one placed without a combo
        // takes the combo of its research item.
        if (type == "MISSILE_EMPLACEMENT" && weaponCombo is null)
            weaponCombo = Data.ResearchTree.Items.FirstOrDefault(i => i.Key.Type == type && i.Key.SubType == subType && i.Key.HardwareLevel == level)?.Key.WeaponCombo;
        construction.WeaponCombo = weaponCombo;
        if (weaponCombo is not null && type == "MISSILE_EMPLACEMENT")
        {
            foreach (var (mounting, weapon) in WeaponCombos.Split(weaponCombo))
            {
                var weaponSpec = Data.Parm.Weapons.FirstOrDefault(w => w.TypeName == weapon);
                if (weaponSpec is null)
                    continue;
                // A nuclear silo starts charging when it is placed (MachLogWeapon's lastFireTime_).
                var mount = new WeaponMount(mounting, weaponSpec);
                if (mount.IsStrike)
                    mount.Reload = weaponSpec.ReloadTime;
                construction.Weapons.Add(mount);
            }
        }

        _actors.Add(construction.Id, construction);
        Nav.SetBuilding(construction.Footprint, present: true);

        // A placed mine digs at a found site in reach; a scenario's own mine finds the site it stands at
        // (MachLogMine's CHECK_DISCOVERED_FLAG and IGNORE_DISCOVERED_FLAG).
        if (type == "MINE")
            AssignMineralSite(construction, discover: complete);
        return construction;
    }

    internal void Raise(GameEvent e) => _events.Add(e);

    /// <summary>How close (squared) a mine must stand to its mineral site: <c>MINERAL_DISTANCE</c> in parmdata.</summary>
    private static float MineSiteReachSquared(Construction mine) => mine.Stats.MineralDistance ?? 900;

    /// <summary>
    /// Whether a mine may dig at a site: it has ore left, lies within the mine's MINERAL_DISTANCE and has been found,
    /// by any race (MachLogMine::validMineSite asks hasBeenDiscovered, not by whom). Sites may be shared.
    /// </summary>
    private static bool InMineReach(MineralSiteState site, Vector2 mine, float reachSquared, bool foundOrNot = false) =>
        site.Ore > 0 && (foundOrNot || site.DiscoveredBy.Count > 0) && Vector2.DistanceSquared(site.Position, mine) < reachSquared;

    /// <summary>
    /// Gives a mine the nearest site it may dig at (MachLogMine::assignToNearestMineralSite), or none; with
    /// <paramref name="discover"/> any site will do and the mine's race finds it (discoverAndAssignToNearestMineralSite).
    /// </summary>
    private bool AssignMineralSite(Construction mine, bool discover = false)
    {
        var reach = MineSiteReachSquared(mine);
        var site = Sites.Where(s => InMineReach(s, mine.Position, reach, foundOrNot: discover))
            .MinBy(s => Vector2.DistanceSquared(s.Position, mine.Position));
        mine.MineralSite = site?.Id;
        if (site is not null && discover)
            site.DiscoveredBy.Add(mine.Race);
        return site is not null;
    }

    // ---- races ----------------------------------------------------------------------------------

    /// <summary>
    /// The BMU storage limit: the pod's capacity plus every finished smelter's. A race may hold more than this
    /// at the start (the original allows it); income stops at the limit.
    /// </summary>
    public int BmuCapacity(Race race) => Constructions
        .Where(c => c.Race == race && c.Complete && c.Spec.Type is "SMELTER" or "POD")
        .Sum(c => c.Stats.Capacity ?? 0);

    /// <summary>Adds BMUs up to the storage limit; returns how many were added (the original's smartAddBMUs).</summary>
    public float AddBmu(Race race, float amount)
    {
        var state = Races[race];
        var room = MathF.Max(0, BmuCapacity(race) - state.Bmu);
        var added = MathF.Min(amount, room);
        state.Bmu += added;
        state.Score.BmusMined += added;
        return added;
    }

    /// <summary>Takes up to the given BMUs from the bank; returns how many were taken.</summary>
    public float SpendBmu(Race race, float amount)
    {
        var state = Races[race];
        var taken = MathF.Min(amount, state.Bmu);
        state.Bmu -= taken;
        return taken;
    }

    public bool AreEnemies(Race a, Race b) => a != b;

    // ---- removing the dead and ending the game --------------------------------------------------

    private void RemoveDead()
    {
        foreach (var actor in _actors.Values.Where(a => a.IsDead).ToList())
        {
            _actors.Remove(actor.Id);
            Raise(new GameEvent(GameEventKind.Destroyed, actor.Id, actor.Position));
            DropDebris(actor);
            if (actor.LastHitBy is { } killer && killer != actor.Race && Races.TryGetValue(killer, out var gaining)
                && Races.TryGetValue(actor.Race, out var losing))
            {
                gaining.Score.Destruction += actor.Cost;
                losing.Score.Destruction -= actor.Cost / 2;
                var destroyed = actor is Machine ? gaining.Score.MachinesDestroyed : gaining.Score.ConstructionsDestroyed;
                destroyed[actor.Race] = destroyed.GetValueOrDefault(actor.Race) + 1;
            }
            if (Races.TryGetValue(actor.Race, out var owner))
            {
                if (actor is Machine)
                    owner.Score.MachinesLost++;
                else
                    owner.Score.ConstructionsLost++;
            }

            if (actor is Machine { Passengers.Count: > 0 } apc)
                KillPassengers(apc);
            if (actor is Construction building)
            {
                Nav.SetBuilding(building.Footprint, present: false);
                // Technicians inside a destroyed lab die with it; a garrison's machines get out.
                foreach (var technician in building.Technicians.Select(Find<Machine>).OfType<Machine>())
                    technician.HitPoints = 0;
                foreach (var machine in building.Garrisoned.Select(Find<Machine>).OfType<Machine>())
                {
                    machine.Inside = null;
                    machine.Position = DoorOf(building, machine);
                }
            }
            _hitPointCarry.Remove(actor.Id);
        }
    }

    /// <summary>Takes an actor off the map without it counting as destroyed (recycling).</summary>
    private void RemoveQuietly(Actor actor)
    {
        _actors.Remove(actor.Id);
        _hitPointCarry.Remove(actor.Id);
    }

    private void CheckVictory()
    {
        if (Winner is not null)
            return;

        foreach (var state in Races.Values.Where(r => !r.Defeated))
        {
            var isOut = !Actors.Any(a => a.Race == state.Race)
                || Options.Victory == VictoryCondition.Pod && state.StartedWithPod
                    && !Constructions.Any(c => c.Race == state.Race && c.IsType("POD") && c.Complete);
            if (isOut)
                Defeat(state);
        }

        // Timed games: the highest score wins when the time is up (MachLogWinOrLoseAction).
        if (TimeLeft == 0)
        {
            var best = Races.Values.Where(r => !r.Defeated).OrderByDescending(r => r.Score.Gross).ThenBy(r => r.Race).First();
            foreach (var state in Races.Values.Where(r => !r.Defeated && r != best))
                Defeat(state);
        }

        var alive = Races.Values.Where(r => !r.Defeated).ToList();
        if (alive.Count == 1 && Races.Count > 1)
            Winner = alive[0].Race;

        void Defeat(RaceState state)
        {
            state.Defeated = true;
            Raise(new GameEvent(GameEventKind.RaceDefeated, 0, Vector2.Zero, Detail: state.Race.ToString()));
        }
    }
}
