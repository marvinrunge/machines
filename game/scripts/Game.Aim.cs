using Godot;
using Machines.Sim.Presentation;
using Machines.Sim.World;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

/// <summary>
/// What a click means: the intelligent cursor (MachGuiDefaultCommand, MachGuiIntelligentCursorOnActor), which reads the
/// first selected actor and what lies under the mouse, and the commands held in hand from the panel (machgui's cmd*.cpp),
/// which say with their cursor what they would do there and refuse what they cannot.
/// </summary>
public partial class Game
{
    /// <summary>What a click would do, as the original's cursor tells it (MachGui::Cursor2dType).</summary>
    public enum Aim
    {
        Menu,
        Select,
        Invalid,
        Move,
        Locate,
        Attack,
        Follow,
        JoinConstruct,
        Repair,
        Capture,
        Deconstruct,
        Recycle,
        Heal,
        Treachery,
        PickUp,
        Transport,
        Scavenge,
        EnterBuilding,
        EnterApc,
        Deploy,
        AssemblyPoint,
    }

    /// <summary>
    /// What the mouse points at, in the world or on the map: an actor, failing that a wreck or a found deposit, and the
    /// ground there.
    /// </summary>
    public readonly record struct Pointed(Actor? Actor, DebrisState? Wreck, NVector2? Ground, bool Deposit = false)
    {
        /// <summary>Something that lies on the ground, cannot be selected and is driven onto.</summary>
        public bool Loose => Actor is null && (Wreck is not null || Deposit);

        /// <summary>Where a move there goes.</summary>
        public NVector2? Point => Ground ?? Wreck?.Position ?? Actor?.Position;
    }

    private static string CursorOf(Aim aim) => aim switch
    {
        Aim.Select => "select",
        Aim.Invalid => "noentry",
        Aim.Move => "move",
        Aim.Locate => "locat",
        Aim.Attack => "attack",
        Aim.Follow => "follow",
        Aim.JoinConstruct => "jconst",
        Aim.Repair => "repair",
        Aim.Capture => "capture",
        Aim.Deconstruct => "deconst",
        Aim.Recycle => "recycle",
        Aim.Heal => "heal",
        Aim.Treachery => "treach",
        Aim.PickUp => "pickup",
        Aim.Transport => "putdn",
        Aim.Scavenge => "scav",
        Aim.EnterBuilding => "enter",
        Aim.EnterApc => "entapc",
        Aim.Deploy => "deploy",
        Aim.AssemblyPoint => "assembl",
        _ => "arrow",
    };

    // What the mouse points at this frame.
    private Pointed _pointed;

    /// <summary>What lies under a point of the screen.</summary>
    private Pointed PointedAt(Vector2 screen)
    {
        var actor = ActorAt(screen) ?? ArtefactAt(screen);
        return new Pointed(actor, actor is null ? DebrisAt(screen) : null, GroundAt(screen), actor is null && SiteAt(screen) is not null);
    }

    /// <summary>The first of the selected that is not inside an APC, on which the cursor is based.</summary>
    private Actor? Leading => _selection.Select(World.Find).OfType<Actor>().FirstOrDefault(a => a is not Machine { Inside: not null });

    private List<Machine> Movers() => SelectedMachines().Where(m => m.Inside is null).ToList();

    // The player's emplacements among the selected that take orders.
    public List<Construction> SelectedEmplacements() =>
        _selection.Select(World.Find<Construction>).OfType<Construction>().Where(c => c.Race == Player && GameWorld.TakesOrders(c)).ToList();

    private bool Fires(Actor shooter, Actor target) =>
        World.CanAttack(Player, target) && shooter is Machine { CanFight: true } or Construction;

    /// <summary>Whether the machines can stand there; anywhere in the dark of the fog of war (MachGuiCommand::cursorInFogOfWar).</summary>
    private bool Free(Pointed at, IEnumerable<Machine> machines) =>
        at.Ground is { } ground && (Fog?.Blind == true || World.Nav.IsFree(ground, machines.Any(GameWorld.CrossesWater)));

    private bool CanGoIn(Construction building) => World.HasFreeBay(building) && Movers().Any(m => World.CanEnter(m, building));

    // MachLogMachine::canEnterAPC: no APC goes in one, nor a glider, and there must be room.
    private bool CanBoard(Machine machine, Machine apc) =>
        machine.Id != apc.Id && !machine.IsType("APC") && machine.Stats.Locomotion != Machines.Import.Model.Locomotion.Glider && World.RoomFor(apc, machine);

    /// <summary>Whether a carrier can fetch from the building now (MachLogMine::worthVisiting).</summary>
    private bool Supplier(Construction building) =>
        GameWorld.Supplies(building, Player) && (!building.IsType("MINE") || GameWorld.WorthVisiting(building));

    /// <summary>What a left click would do with what is selected and held down, had the player no command in hand.</summary>
    private Aim AimAt(Pointed at, bool ctrl, bool alt)
    {
        var lead = Leading;
        if (at.Actor is null && !at.Loose)
            return AimOnTerrain(lead, at, alt);
        if (lead is not null)
            return Intelligent(lead, at, ctrl, alt);
        return at.Actor is Machine or Construction ? Aim.Select : Aim.Menu;
    }

    /// <summary>MachGuiDefaultCommand::cursorOnTerrain.</summary>
    private Aim AimOnTerrain(Actor? lead, Pointed at, bool alt)
    {
        if (lead is Machine machine && machine.Race == Player)
        {
            if (!Free(at, Movers()))
                return Aim.Invalid;
            if (machine.Spec.Type == "GEO_LOCATOR" && !alt)
                return Aim.Locate;
            return machine.IsType("APC") && alt ? Aim.Deploy : Aim.Move;
        }
        return lead is Construction factory && factory.Race == Player && factory.IsType("FACTORY") && alt ? Aim.AssemblyPoint : Aim.Menu;
    }

    /// <summary>MachGuiIntelligentCursorOnActor::cursorType, from the first selected actor.</summary>
    private Aim Intelligent(Actor lead, Pointed at, bool ctrl, bool alt)
    {
        var actor = at.Actor;
        if (lead.Race != Player)
            return !ctrl && actor is Machine or Construction ? Aim.Select : at.Loose || actor is Artefact ? Aim.Menu : Aim.Invalid;
        if (lead is Construction building)
            return actor is not null && GameWorld.TakesOrders(building) && World.CanAttack(Player, actor) ? Aim.Attack : Selecting(lead, at, ctrl, alt);
        var machine = (Machine)lead;
        switch (machine.Spec.Type)
        {
            case "ADMINISTRATOR":
                if (actor is not null && Fires(machine, actor))
                    return alt && !ctrl && actor is Machine && machine.Weapons.Any(w => w.IsTreachery) ? Aim.Treachery : Aim.Attack;
                if (actor is Machine patient && !World.AreEnemies(Player, patient.Race) && machine.CanHeal && patient.Id != machine.Id
                    && !ctrl && patient.HitPoints != patient.MaxHitPoints)
                    return Aim.Heal;
                break;
            case "AGGRESSOR":
                if (actor is not null && Fires(machine, actor))
                    return Aim.Attack;
                break;
            case "CONSTRUCTOR":
                if (actor is Construction job && ForConstructor(job, ctrl, alt) is { } work)
                    return work;
                break;
            case "SPY_LOCATOR" when actor is Construction target && !ctrl:
                if (target.IsType("HARDWARE_LAB") && target.Race != Player)
                    return Aim.EnterBuilding;
                if (target.IsType("GARRISON") && target.Race == Player && machine.Mines < (machine.Stats.MineCapacity ?? 0))
                    return Aim.EnterBuilding;
                break;
            case "TECHNICIAN" when actor is Construction lab && lab.Race == Player && lab.IsType("HARDWARE_LAB") && !ctrl && World.HasFreeBay(lab):
                return Aim.EnterBuilding;
            case "RESOURCE_CARRIER":
                if (actor is Construction stop && !machine.IsScavenger && !ctrl && ForCarrier(stop) is { } carrying)
                    return carrying;
                if (at.Wreck is not null && machine.IsScavenger)
                    return Aim.Scavenge;
                break;
        }
        return Standard(machine, at, ctrl, alt);
    }

    // doProcessConstructor: join in or repair a friend's, take an enemy's over or apart.
    private Aim? ForConstructor(Construction target, bool ctrl, bool alt)
    {
        if (World.AreEnemies(Player, target.Race))
            return !target.Complete || alt ? Aim.Deconstruct : Aim.Capture;
        if (alt)
            return ctrl ? null : Aim.Deconstruct;
        if (ctrl)
            return null;
        return !target.Complete ? Aim.JoinConstruct : target.HitPoints != target.MaxHitPoints ? Aim.Repair : null;
    }

    // doProcessResourceCarrier: fetch from what supplies, bring to what is the player's.
    private Aim? ForCarrier(Construction stop)
    {
        if (stop.IsType("MINE"))
            return GameWorld.WorthVisiting(stop) ? Aim.PickUp : Aim.Invalid;
        if (stop.Spec.Type is "SMELTER" or "POD")
            return stop.Race == Player ? Aim.Transport : Aim.PickUp;
        return null;
    }

    /// <summary>MachGuiIntelligentCursorOnActor::processStandardMachine.</summary>
    private Aim Standard(Machine machine, Pointed at, bool ctrl, bool alt)
    {
        if (at.Actor is Machine { Race: var race } apc && race == Player && apc.IsType("APC") && !ctrl && !alt && Movers().Any(m => !m.IsType("APC")))
            return Movers().Any(m => CanBoard(m, apc)) ? Aim.EnterApc : Aim.Invalid;
        if (at.Actor is Machine or Construction)
            return Selecting(machine, at, ctrl, alt);
        if (at.Loose)
            return machine.Spec.Type == "GEO_LOCATOR" ? Aim.Locate : Aim.Move;
        return Aim.Invalid;
    }

    /// <summary>MachGuiIntelligentCursorOnActor::doDefaultProcess: what is left over the actors the player can select.</summary>
    private Aim Selecting(Actor lead, Pointed at, bool ctrl, bool alt)
    {
        var own = at.Actor?.Race == Player;
        if (at.Actor is Machine or Construction)
        {
            if (!own && ctrl)
                return Aim.Invalid;
            var machine = lead is Machine;
            if (!ctrl && own && machine && at.Actor is Construction garrison && garrison.IsType("GARRISON"))
                return CanGoIn(garrison) ? Aim.EnterBuilding : Aim.Select;
            if (alt && !ctrl && machine)
            {
                if (own && at.Actor is Construction { Complete: true, Spec.Type: "SMELTER" or "POD" })
                    return Aim.Recycle;
                if (at.Actor is Machine)
                    return Aim.Follow;
            }
            if (alt && !ctrl && at.Actor is Construction { HasInterior: true } building)
                return CanGoIn(building) ? Aim.EnterBuilding : Aim.Select;
            return Aim.Select;
        }
        if (at.Wreck is not null)
            return lead is Machine { Spec.Type: "GEO_LOCATOR" } ? Aim.Locate : Aim.Move;
        return Aim.Menu;
    }

    /// <summary>A left click on the actor with nothing in hand: Ctrl adds it to the selection or takes it out (MachGuiDefaultCommand::pickOnActor).</summary>
    public void SelectByClick(Actor actor, bool ctrl, bool shift = false)
    {
        if (ctrl)
            Select(_selection.Contains(actor.Id) ? _selection.Where(id => id != actor.Id).ToList() : [.. _selection, actor.Id]);
        else if (shift)
            Select(_selection.Union([actor.Id]).ToList());
        else
            Select([actor.Id]);
    }

    /// <summary>
    /// Carries out what the cursor showed. It goes to the machines themselves even for a squadron: the original's
    /// default command had no admin version. With Shift a pick on the ground only clicks: it takes the Move command in
    /// hand to collect points (MachGuiDefaultCommand::pickOnTerrain).
    /// </summary>
    private void Carry(Aim aim, Pointed at, bool ctrl, bool shift)
    {
        if (aim == Aim.Select && at.Actor is { } picked)
        {
            SelectByClick(picked, ctrl);
            return;
        }
        if (RefusesOrders() || aim is Aim.Menu or Aim.Invalid)
            return;
        var squadron = _squadronSelected;
        _squadronSelected = null;
        try
        {
            CarryOut(aim, at, shift);
        }
        finally
        {
            _squadronSelected = squadron;
        }
    }

    private void CarryOut(Aim aim, Pointed at, bool shift)
    {
        var machines = Movers();
        List<int> Ids(Func<Machine, bool> which) => machines.Where(which).Select(m => m.Id).ToList();
        var target = at.Actor;
        var id = target?.Id ?? 0;
        var constructors = Ids(m => m.Spec.Type == "CONSTRUCTOR");
        switch (aim)
        {
            case Aim.Move or Aim.Locate when shift:
                _audio.PlayGui("gui/sounds/waypoint.wav");
                break;
            case Aim.Move when at.Point is { } point:
                Order(new MoveCommand(Player, Ids(_ => true), point));
                break;
            case Aim.Locate when at.Point is { } point:
                Order(new LocateCommand(Player, Ids(m => m.Spec.Type == "GEO_LOCATOR"), point));
                if (Ids(m => m.Spec.Type != "GEO_LOCATOR") is { Count: > 0 } others)
                    Order(new MoveCommand(Player, others, point));
                break;
            case Aim.Attack:
                Order(new AttackCommand(Player, [.. Ids(m => m.CanFight), .. SelectedEmplacements().Select(c => c.Id)], id));
                break;
            case Aim.Follow:
                Order(new FollowCommand(Player, Ids(m => m.Id != id), id));
                break;
            case Aim.JoinConstruct:
                Order(new ConstructCommand(Player, constructors, id));
                break;
            case Aim.Repair:
                Order(new RepairCommand(Player, constructors, id));
                break;
            case Aim.Capture:
                Order(new CaptureCommand(Player, constructors, id));
                break;
            case Aim.Deconstruct:
                Order(new DeconstructCommand(Player, constructors, id));
                break;
            case Aim.Recycle:
                Order(new RecycleCommand(Player, Ids(_ => true), id));
                break;
            case Aim.Heal:
                Order(new HealCommand(Player, Ids(m => m.CanHeal && m.Id != id), id));
                break;
            case Aim.Treachery:
                Order(new AttackCommand(Player, Ids(m => m.Weapons.Any(w => w.IsTreachery)), id), "VID_COMMANDER_TREACHERY_TARGET");
                break;
            case Aim.PickUp:
                Order(new TransportCommand(Player, Ids(IsCarrier), id, 0, Adding: shift));
                break;
            case Aim.Transport:
                BringTo(machines.Where(IsCarrier).ToList(), id);
                break;
            case Aim.Scavenge when at.Wreck is { } wreck:
                Order(new ScavengeCommand(Player, Ids(m => m.IsScavenger), [wreck.Id]));
                break;
            case Aim.EnterBuilding when target is Construction building:
                GoIn(machines, building);
                break;
            case Aim.EnterApc:
                Order(new BoardCommand(Player, Ids(m => !m.IsType("APC")), id));
                break;
            case Aim.Deploy when at.Point is { } point:
                Order(new DeployCommand(Player, Ids(m => m.IsType("APC")), point));
                break;
            case Aim.AssemblyPoint when at.Point is { } point:
                Order(new AssemblyPointCommand(Player, SelectedFactories(), point));
                break;
        }
    }

    private static bool IsCarrier(Machine machine) => machine.Spec.Type == "RESOURCE_CARRIER" && !machine.IsScavenger;

    private List<int> SelectedFactories() => _selection.Select(World.Find<Construction>).OfType<Construction>()
        .Where(c => c.Race == Player && c.Complete && c.IsType("FACTORY")).Select(c => c.Id).ToList();

    /// <summary>A smelter or pod for carriers that have no supplier picked: they keep their round and bring its ore there; one without a round drives there.</summary>
    private void BringTo(List<Machine> carriers, int store)
    {
        foreach (var carrier in carriers)
        {
            if (carrier.Order is TransportOrder { Mine: not 0 } round)
                Order(new TransportCommand(Player, [carrier.Id], round.Mine, store, round.Round));
            else if (World.Find(store) is { } building)
                Order(new MoveCommand(Player, [carrier.Id], building.Position));
        }
    }

    /// <summary>Into a building, each as it may: spies into labs to steal, or to a garrison for mines, technicians to research, the rest to a bay.</summary>
    private void GoIn(List<Machine> machines, Construction building)
    {
        List<int> Ids(Func<Machine, bool> which) => machines.Where(which).Select(m => m.Id).ToList();
        if (building.IsType("HARDWARE_LAB"))
        {
            if (Ids(m => m.Spec.Type == "TECHNICIAN" && building.Race == Player || m.IsSpy && building.Race != Player) is { Count: > 0 } workers)
                Order(new EnterLabCommand(Player, workers, building.Id));
        }
        if (building.IsType("GARRISON") && Ids(m => m.IsSpy && m.Mines < (m.Stats.MineCapacity ?? 0)) is { Count: > 0 } spies)
            Order(new RefillLandMinesCommand(Player, spies));
        if (machines.Where(m => World.CanEnter(m, building) && (m.Spec.Type != "TECHNICIAN" || !building.IsType("HARDWARE_LAB"))).Select(m => m.Id).ToList() is { Count: > 0 } entering)
            Order(new EnterGarrisonCommand(Player, entering, building.Id));
    }

    // ---- commands in hand -----------------------------------------------------------------------

    /// <summary>
    /// A command waiting for its target: what it asks for on the prompt line, the cursor it shows over what is pointed
    /// at, and what a pick does (with Shift held or not), true once it has its final pick and leaves the hand.
    /// </summary>
    private sealed record InHand(string Message, Func<Pointed, string> Cursor, Func<Pointed, bool, bool> Pick);

    private InHand? _inHand;

    /// <summary>Takes a command in hand; a right click puts it back.</summary>
    private void Hold(string message, Func<Pointed, Aim> aim, Func<Aim, Pointed, bool, bool> pick)
    {
        CancelPlacement();
        _inHand = new InHand(message, at => CursorOf(aim(at)), (at, shift) => pick(aim(at), at, shift));
    }

    private void Waypoint() => _audio.PlayGui("gui/sounds/waypoint.wav");

    /// <summary>The next pick goes to the command in hand; otherwise the intelligent cursor or the selecting has it.</summary>
    private void Pick(Pointed at, bool ctrl, bool shift, bool alt)
    {
        if (_inHand is { } hand)
        {
            if (!RefusesOrders() && hand.Pick(at, shift))
                _inHand = null;
        }
        else if (_placing is not null)
            PlaceBuilding(at, shift);
        else if (GameOptions.ClassicMouse)
            Carry(AimAt(at, ctrl, alt), at, ctrl, shift);
        else if (at.Actor is Machine or Construction)
            SelectByClick(at.Actor, ctrl, shift);
        else if (!ctrl && !shift)
            Select([]);
    }

    /// <summary>A pick on the continent map, as in the world (MachContinentMap::handleIntelligentCursor).</summary>
    public void PickOnMap(Pointed at) => Pick(at, Input.IsKeyPressed(Key.Ctrl), Input.IsKeyPressed(Key.Shift), Input.IsKeyPressed(Key.Alt));

    /// <summary>Whether a left click on the map goes to a command rather than moving the view.</summary>
    public bool Holding => _inHand is not null || _placing is not null;

    /// <summary>
    /// Move (MachGuiMoveCommand): to a point, through more picked with Shift; onto a machine to follow it, into a
    /// building or an APC.
    /// </summary>
    public void BeginMove()
    {
        var machines = Movers();
        var ids = machines.Select(m => m.Id).ToList();
        var via = new List<NVector2>();
        Aim On(Pointed at)
        {
            switch (at.Actor)
            {
                case Construction building:
                    return CanGoIn(building) ? Aim.EnterBuilding : Aim.Invalid;
                case Machine { Race: var race } apc when race == Player && apc.IsType("APC") && !Input.IsKeyPressed(Key.Alt):
                    return machines.Any(m => CanBoard(m, apc)) ? Aim.EnterApc : Aim.Invalid;
                case Machine other:
                    return machines is [var only] && only.Id == other.Id ? Aim.Menu : Aim.Follow;
                case not null:
                    return Aim.Invalid;
            }
            return at.Loose || Free(at, machines) ? Aim.Move : Aim.Invalid;
        }
        Hold("Click where to move, Shift for more points; right click cancels", On, (aim, at, shift) =>
        {
            switch (aim)
            {
                case Aim.Move when at.Point is { } point:
                    if (shift)
                    {
                        via.Add(point);
                        Waypoint();
                        return false;
                    }
                    Order(new MoveCommand(Player, ids, point, via.Count > 0 ? via : null));
                    return true;
                case Aim.Follow:
                    Order(new FollowCommand(Player, ids.Where(id => id != at.Actor!.Id).ToList(), at.Actor!.Id));
                    return true;
                case Aim.EnterApc:
                    Order(new BoardCommand(Player, machines.Where(m => CanBoard(m, (Machine)at.Actor!)).Select(m => m.Id).ToList(), at.Actor!.Id));
                    return true;
                case Aim.EnterBuilding:
                    GoIn(machines, (Construction)at.Actor!);
                    return true;
            }
            return false;
        });
    }

    /// <summary>
    /// Attack (MachGuiAttackCommand): what can fire at the target does, emplacements too; ground and wreckage are a move
    /// there.
    /// </summary>
    public void BeginAttack()
    {
        var machines = Movers().Where(m => m.CanFight).ToList();
        var emplacements = SelectedEmplacements();
        var ids = machines.Select(m => m.Id).Concat(emplacements.Select(c => c.Id)).ToList();
        Aim On(Pointed at)
        {
            if (at.Actor is { } actor)
                return ids is [var only] && only == actor.Id ? Aim.Menu : World.CanAttack(Player, actor) ? Aim.Attack : Aim.Invalid;
            return at.Loose || machines.Count > 0 && Free(at, machines) ? Aim.Move : Aim.Invalid;
        }
        Hold("Click what to attack; right click cancels", On, (aim, at, _) =>
        {
            if (aim == Aim.Attack)
                Order(new AttackCommand(Player, ids, at.Actor!.Id));
            else if (aim == Aim.Move && at.Point is { } point)
                Order(new MoveCommand(Player, machines.Select(m => m.Id).ToList(), point));
            return aim is Aim.Attack or Aim.Move;
        });
    }

    /// <summary>
    /// A constructor's work on buildings picked in turn, Shift for more: repair, capture, deconstruct. Ground does
    /// nothing, what does not fit refuses it (MachGuiRepairCommand, MachGuiCaptureCommand, MachGuiDeconstructCommand).
    /// </summary>
    private void BeginWork(string message, Aim work, Func<Construction, bool> fits, Func<IReadOnlyList<int>, int, Command> step)
    {
        var constructors = Movers().Where(m => m.Spec.Type == "CONSTRUCTOR").Select(m => m.Id).ToList();
        var picked = new List<int>();
        Hold(message, at => at.Actor is Construction building ? fits(building) ? work : Aim.Invalid : at.Actor is null && !at.Loose ? Aim.Menu : Aim.Invalid, (aim, at, shift) =>
        {
            if (aim != work)
                return false;
            if (!picked.Contains(at.Actor!.Id))
                picked.Add(at.Actor!.Id);
            if (shift)
            {
                Waypoint();
                return false;
            }
            Order(picked is [var one] ? step(constructors, one) : new SeriesCommand(Player, constructors, picked.Select(id => step([], id)).ToList()),
                "VID_DOZER_MOVE_TO_SITE");
            return true;
        });
    }

    public void BeginRepair() => BeginWork("Click the building to repair, Shift for more; right click cancels", Aim.Repair,
        building => building.Complete && building.HitPoints < building.MaxHitPoints, (ids, id) => new RepairCommand(Player, ids, id));

    public void BeginCapture() => BeginWork("Click the building to capture, Shift for more; right click cancels", Aim.Capture,
        building => building.Complete && World.AreEnemies(Player, building.Race), (ids, id) => new CaptureCommand(Player, ids, id));

    public void BeginDeconstruct() => BeginWork("Click the building to take apart, Shift for more; right click cancels", Aim.Deconstruct,
        _ => true, (ids, id) => new DeconstructCommand(Player, ids, id));

    /// <summary>Recycle (MachGuiRecycleCommand): the machines go to the finished smelter or pod clicked.</summary>
    public void BeginRecycle()
    {
        var ids = Movers().Select(m => m.Id).ToList();
        Hold("Click the smelter or pod to recycle at; right click cancels",
            at => at.Actor is Construction { Complete: true, Spec.Type: "SMELTER" or "POD" } ? Aim.Recycle : at.Actor is null && !at.Loose ? Aim.Menu : Aim.Invalid,
            (aim, at, _) =>
            {
                if (aim == Aim.Recycle)
                    Order(new RecycleCommand(Player, ids, at.Actor!.Id));
                return aim == Aim.Recycle;
            });
    }

    /// <summary>Locate (MachGuiLocateToCommand): locators search at each point in turn, more with Shift; actors refuse it.</summary>
    public void BeginLocate()
    {
        var locators = Movers().Where(m => m.Spec.Type == "GEO_LOCATOR").ToList();
        var ids = locators.Select(m => m.Id).ToList();
        var points = new List<NVector2>();
        Hold("Click where to search for minerals, Shift for more; right click cancels",
            at => at.Actor is null && Free(at, locators) ? Aim.Locate : Aim.Invalid, (aim, at, shift) =>
            {
                if (aim != Aim.Locate || at.Point is not { } point)
                    return false;
                points.Add(point);
                if (shift)
                {
                    Waypoint();
                    return false;
                }
                Order(points is [var one] ? new LocateCommand(Player, ids, one)
                    : new SeriesCommand(Player, ids, points.Select(p => new LocateCommand(Player, [], p)).ToList<Command>()), "VID_GEO_LOCATING");
                return true;
            });
    }

    /// <summary>Scavenge (MachGuiScavengeCommand): the wreckage clicked, more with Shift.</summary>
    public void BeginScavenge()
    {
        var ids = Movers().Where(m => m.IsScavenger).Select(m => m.Id).ToList();
        var wrecks = new List<int>();
        Hold("Click the wreckage to gather, Shift for more; right click cancels", at => at.Wreck is not null ? Aim.Scavenge : Aim.Invalid, (aim, at, shift) =>
        {
            if (aim != Aim.Scavenge)
                return false;
            wrecks.Add(at.Wreck!.Id);
            if (shift)
            {
                Waypoint();
                return false;
            }
            Order(new ScavengeCommand(Player, ids, wrecks.Distinct().ToList()));
            return true;
        });
    }

    /// <summary>
    /// Transport (MachGuiTransportCommand): a pick on a supplier takes it for the round, in place of the others without
    /// Shift, and the command stays in hand until the player's smelter or pod is picked.
    /// </summary>
    public void BeginTransport()
    {
        var carriers = Movers().Where(IsCarrier).ToList();
        var ids = carriers.Select(m => m.Id).ToList();
        var suppliers = new List<int>();
        Aim On(Pointed at) => at.Actor switch
        {
            Construction stop when Supplier(stop) => Aim.PickUp,
            Construction { Race: var race, Complete: true, Spec.Type: "SMELTER" or "POD" } when race == Player => Aim.Transport,
            null when !at.Loose => Free(at, carriers) ? Aim.Move : Aim.Menu,
            _ => Aim.Invalid,
        };
        Hold("Click the mines to fetch from, then the smelter or pod to bring it to; right click cancels", On, (aim, at, shift) =>
        {
            if (aim == Aim.PickUp)
            {
                if (!shift)
                    suppliers.Clear();
                if (!suppliers.Contains(at.Actor!.Id))
                    suppliers.Add(at.Actor!.Id);
                Waypoint();
                return false;
            }
            if (aim != Aim.Transport)
                return false;
            if (suppliers.Count > 0)
                Order(new TransportCommand(Player, ids, suppliers[0], at.Actor!.Id, suppliers.Count > 1 ? suppliers.Skip(1).ToList() : null));
            else
                BringTo(carriers, at.Actor!.Id);
            return true;
        });
    }

    /// <summary>Pick up (MachGuiPickUpCommand): the carriers fetch from there and find their own smelter; with Shift it joins their round.</summary>
    public void BeginPickUp()
    {
        var ids = Movers().Where(IsCarrier).Select(m => m.Id).ToList();
        Hold("Click where to fetch ore from, Shift adds it to the round; right click cancels",
            at => at.Actor is Construction stop && Supplier(stop) ? Aim.PickUp : at.Actor is null && !at.Loose ? Aim.Menu : Aim.Invalid, (aim, at, shift) =>
            {
                if (aim == Aim.PickUp)
                    Order(new TransportCommand(Player, ids, at.Actor!.Id, 0, Adding: shift));
                return aim == Aim.PickUp;
            });
    }

    /// <summary>Heal (MachGuiHealCommand): a machine short of hit points; ground is a move there.</summary>
    public void BeginHeal()
    {
        var healers = Movers().Where(m => m.CanHeal).ToList();
        Aim On(Pointed at)
        {
            if (at.Actor is not null)
                return at.Actor is Machine patient && patient.HitPoints != patient.MaxHitPoints && healers.Any(m => m.Id != patient.Id) ? Aim.Heal : Aim.Invalid;
            return Free(at, healers) ? Aim.Move : Aim.Invalid;
        }
        Hold("Click the machine to heal; right click cancels", On, (aim, at, _) =>
        {
            if (aim == Aim.Heal)
                Order(new HealCommand(Player, healers.Where(m => m.Id != at.Actor!.Id).Select(m => m.Id).ToList(), at.Actor!.Id));
            else if (aim == Aim.Move && at.Point is { } point)
                Order(new MoveCommand(Player, healers.Select(m => m.Id).ToList(), point));
            return aim is Aim.Heal or Aim.Move;
        });
    }

    /// <summary>Treachery (MachGuiTreacheryCommand): another race's machine; ground is a move there.</summary>
    public void BeginTreachery()
    {
        var traitors = Movers().Where(m => m.Weapons.Any(w => w.IsTreachery)).ToList();
        Aim On(Pointed at)
        {
            if (at.Actor is not null)
                return at.Actor is Machine && at.Actor.Race != Player ? Aim.Treachery : Aim.Invalid;
            return Free(at, traitors) ? Aim.Move : Aim.Invalid;
        }
        Hold("Click the enemy machine to turn; right click cancels", On, (aim, at, _) =>
        {
            if (aim == Aim.Treachery)
                Order(new AttackCommand(Player, traitors.Select(m => m.Id).ToList(), at.Actor!.Id), "VID_COMMANDER_TREACHERY_TARGET");
            else if (aim == Aim.Move && at.Point is { } point)
                Order(new MoveCommand(Player, traitors.Select(m => m.Id).ToList(), point));
            return aim is Aim.Treachery or Aim.Move;
        });
    }

    /// <summary>The next pick on free ground goes to <paramref name="onPick"/>; meanwhile the mouse shows <paramref name="cursor"/>.</summary>
    public void BeginGround(string message, string cursor, Action<NVector2> onPick)
    {
        var machines = Movers();
        CancelPlacement();
        _inHand = new InHand(message, at => at.Actor is null && Free(at, machines) ? cursor : "noentry", (at, _) =>
        {
            if (at.Actor is not null || !Free(at, machines) || at.Point is not { } point)
                return false;
            onPick(point);
            return true;
        });
    }

    /// <summary>
    /// Picks on free ground for <paramref name="onDone"/>: with Shift held each adds one more, with the waypoint click,
    /// and the first without Shift is the last (MachGuiPatrolCommand, MachGuiDropLandMineCommand).
    /// </summary>
    public void BeginWaypoints(string message, string cursor, Action<List<NVector2>> onDone)
    {
        var machines = Movers();
        var points = new List<NVector2>();
        CancelPlacement();
        _inHand = new InHand(message, at => at.Actor is null && Free(at, machines) ? cursor : "noentry", (at, shift) =>
        {
            if (at.Actor is not null || !Free(at, machines) || at.Point is not { } point)
                return false;
            points.Add(point);
            if (shift)
            {
                Waypoint();
                return false;
            }
            onDone(points);
            return true;
        });
    }

    /// <summary>The next pick sets the selected factories' assembly point (MachGuiAssemblyPointCommand).</summary>
    public void BeginAssemblyPoint()
    {
        if (SelectedFactories() is { Count: > 0 } factories)
            BeginGround("Click where new machines gather; right click cancels", "assembl", point => Order(new AssemblyPointCommand(Player, factories, point)));
    }

    /// <summary>Waits for a pick of the target of the building's ion cannon or nuclear missile, under that weapon's own cursor.</summary>
    public void BeginStrike(int launcher)
    {
        var cursor = World.Find<Construction>(launcher)?.Weapons.FirstOrDefault(w => w.IsStrike)?.Spec.TypeName == "NUCLEAR_MISSILE" ? "nukattk" : "ioncan";
        CancelPlacement();
        _inHand = new InHand("Left click the target; right click cancels", _ => cursor, (at, _) =>
        {
            if (at.Point is not { } point)
                return false;
            Order(new StrikeCommand(Player, launcher, point));
            return true;
        });
    }
}
