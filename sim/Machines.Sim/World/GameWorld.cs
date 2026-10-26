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

        if (Tick % TicksPerSecond == 0)
        {
            foreach (var ai in AiControllers)
                ai.Think(this);
        }

        foreach (var machine in Machines.ToList())
        {
            if (!machine.IsDead && machine.Inside is null)
                UpdateMachine(machine);
        }

        UpdateEconomy();
        UpdateSupport();
        UpdateTransport();
        UpdateCombat();
        RemoveDead();
        CheckVictory();

        Tick++;
    }

    // ---- creating actors ------------------------------------------------------------------------

    public Machine AddMachine(Race race, Vector2 position, string type, string? subType, int level, string? weaponCombo)
    {
        var spec = Data.Parm.Machine(subType is null ? type : $"{type} {subType}");
        var machine = new Machine(_nextId++, race, position, spec, spec.Level(level), weaponCombo);
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

        if (type == "MINE")
        {
            var site = Sites.Where(s => s.Mine is null).MinBy(s => Vector2.Distance(s.Position, position));
            if (site is not null && Vector2.DistanceSquared(site.Position, position) <= MineSiteReachSquared(construction))
            {
                site.Mine = construction.Id;
                construction.MineralSite = site.Id;
            }
        }
        return construction;
    }

    internal void Raise(GameEvent e) => _events.Add(e);

    /// <summary>How close (squared) a mine must stand to its mineral site: <c>MINERAL_DISTANCE</c> in parmdata.</summary>
    private static float MineSiteReachSquared(Construction mine) => mine.Stats.MineralDistance ?? 900;

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
            if (actor.LastHitBy is { } killer && killer != actor.Race && Races.TryGetValue(killer, out var gaining)
                && Races.TryGetValue(actor.Race, out var losing))
            {
                gaining.Score.Destruction += actor.Cost;
                losing.Score.Destruction -= actor.Cost / 2;
            }

            if (actor is Machine { Passengers.Count: > 0 } apc)
                KillPassengers(apc);
            if (actor is Construction building)
            {
                Nav.SetBuilding(building.Footprint, present: false);
                foreach (var site in Sites.Where(s => s.Mine == building.Id))
                    site.Mine = null;
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
