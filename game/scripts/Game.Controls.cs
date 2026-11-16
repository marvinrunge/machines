using Godot;
using Machines.Sim.Combat;
using Machines.Sim.Survival;
using Machines.Sim.World;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

// The remake's own controls in first person: a trigger for each weapon, always in a run and elsewhere with the options'
// Mouse button per weapon, and the keys a run's driver orders the squad and uses the abilities and the chassis' special
// with. In any other match, with the option off, none of this does anything.
public partial class Game
{
    /// <summary>How far the sight looks for what the squad is to attack, metres: further than any gun reaches.</summary>
    private const float OrderSight = 800;

    /// <summary>Whether each weapon has its own trigger in first person.</summary>
    public bool WeaponButtons => World.Wired is not null || GameOptions.WeaponButtons;

    /// <summary>Whether an action has the key in first person as things stand.</summary>
    private bool TakenInFirstPerson(Key key) => KeyMap.Taken(key, firstPerson: true, World.Wired is not null, WeaponButtons);

    // Space fires everything, held or not, as long as no action has it: in a run that is the dash.
    private bool SpaceFires => Input.IsKeyPressed(Key.Space) && !TakenInFirstPerson(Key.Space);

    private readonly TriggerHands _hands = new();

    private static readonly Mounting[] Triggers = [Mounting.Left, Mounting.Right, Mounting.Top];

    private static KeyMap.Act Trigger(Mounting mounting) => mounting switch
    {
        Mounting.Left => KeyMap.Act.FireLeft,
        Mounting.Right => KeyMap.Act.FireRight,
        _ => KeyMap.Act.FireTop,
    };

    /// <summary>
    /// The places a machine has for guns, by its mountings, with what fires each: a hero's chassis has them whether it wears
    /// a gun there or not, any other machine has those of its weapons. A battery is one place, shown by its first copy.
    /// </summary>
    private IReadOnlyList<WeaponDisplay.GunPlace> GunPlaces(Machine machine)
    {
        var mountings = HeroOf(machine)?.ChassisEntry.Slots.Select(slot => slot.Mounting).ToList() ?? machine.Weapons.Select(w => w.Mounting).Distinct().ToList();
        var places = WeaponTriggers.Places(machine.Weapons);
        return mountings.Select(mounting => machine.Weapons.FirstOrDefault(w => w.Mounting == mounting) is { } weapon
            ? new WeaponDisplay.GunPlace(mounting, weapon, WeaponTriggers.Of(mounting, places).Select(Trigger).ToList())
            : new WeaponDisplay.GunPlace(mounting, null, [])).ToList();
    }

    /// <summary>
    /// The weapons whose triggers are held or were just pressed, one bit each in mounting order, as <see cref="DriveCommand.Weapons"/>
    /// wants them; none while a constructor hero's towers are up (Game.Towers).
    /// </summary>
    private int HeldWeapons(Machine machine) =>
        _towersUp ? 0 : SpaceFires ? -1 : _hands.Mask(machine.Weapons, TriggerHeld, Time.GetTicksMsec() / 1000.0);

    /// <summary>
    /// Whether a trigger is held: pressed where the driver could mean it, and not come up since. A press seen anywhere else, or
    /// a button the system only says is down because the window lost the focus or went down before it came up, does not count.
    /// </summary>
    private bool TriggerHeld(Mounting trigger)
    {
        if (DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Minimized)
            _hands.LetGo();
        else if (_hands.Down(trigger) && !KeyMap.Held(Trigger(trigger)))
            _hands.Release(trigger);
        return _hands.Down(trigger);
    }

    /// <summary>Whether the action is a trigger held.</summary>
    private bool ActHeld(KeyMap.Act act) => Triggers.Any(trigger => Trigger(trigger) == act && TriggerHeld(trigger));

    /// <summary>
    /// The triggers are watched before anything else hears of them, so that no panel or menu can swallow a release. While a
    /// constructor hero's towers are up, the wheel, the triggers and Esc are theirs.
    /// </summary>
    public override void _Input(InputEvent @event)
    {
        if (TowerInput(@event))
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        if (@event is not (InputEventMouseButton or InputEventKey) || !WeaponButtons)
            return;
        foreach (var trigger in Triggers)
        {
            if (KeyMap.IsUp(@event, Trigger(trigger)))
                _hands.Release(trigger);
            else if (KeyMap.Is(@event, Trigger(trigger)) && (_driven is not null || _heroDown) && !HangarOpen)
                _hands.Press(trigger, Time.GetTicksMsec() / 1000.0);
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationApplicationFocusOut || what == NotificationWMWindowFocusOut)
            _hands.LetGo();
    }

    /// <summary>The keys of a run, driving or down: the squad's orders, the abilities, the dash, the chassis' special and the hangar.</summary>
    private void RunInput(InputEvent @event)
    {
        if (World.Wired is null || Watching || MatchOver)
            return;
        // A battle has no squad and no hangar: only the abilities, the dash and the special.
        if (World.Battle is not null)
        {
            if (KeyMap.Is(@event, KeyMap.Act.Ability1))
                UseAbility(0);
            else if (KeyMap.Is(@event, KeyMap.Act.Ability2))
                UseAbility(1);
            else if (KeyMap.Is(@event, KeyMap.Act.Dash))
                Dash();
            else if (KeyMap.Is(@event, KeyMap.Act.Special))
                UseSpecial();
            return;
        }
        if (KeyMap.Is(@event, KeyMap.Act.SquadFollow))
            SquadFollows();
        else if (KeyMap.Is(@event, KeyMap.Act.SquadAttack))
            SquadAttacks();
        else if (KeyMap.Is(@event, KeyMap.Act.SquadHold))
            SquadOrder(KeyMap.Act.SquadHold, squad => new StandGroundCommand(Player, squad));
        else if (KeyMap.Is(@event, KeyMap.Act.SquadBack))
            SquadBack();
        else if (KeyMap.Is(@event, KeyMap.Act.Ability1))
            UseAbility(0);
        else if (KeyMap.Is(@event, KeyMap.Act.Ability2))
            UseAbility(1);
        else if (KeyMap.Is(@event, KeyMap.Act.Dash))
            Dash();
        else if (KeyMap.Is(@event, KeyMap.Act.Special))
            UseSpecial();
        else if (KeyMap.Is(@event, KeyMap.Act.Hangar))
            OpenHangar();
    }

    /// <summary>
    /// The key of the hero's chassis: a Colossus anchors when it is free (or coming free, which the world refuses with its
    /// reason) and lets go when it is locking or anchored; a Marshal calls its squad, a spy lays a mine, a geo locator
    /// sends out its pulse, all as the world allows; a constructor takes up its towers or puts them away, in a run only.
    /// Old Iron's ram and the Hornet's afterburner are the thrusters' key's, which it names; the Widow's lurk and the
    /// Harvester's harvest work by themselves, and it says so. Any other chassis has nothing to use, and says that.
    /// </summary>
    private void UseSpecial()
    {
        if (RefusesOrders() || World.Wired?.Heroes.GetValueOrDefault(Player) is not { } state)
            return;
        var special = state.Build(World.Data).Chassis.Special;
        if ((special & ChassisSpecial.Towers) != 0)
        {
            if (World.Battle is not null)
                RunRefused(HangarText.Reason("no towers in a battle"));
            else
                ToggleTowerMode();
        }
        else if ((special & ChassisSpecial.Anchor) != 0)
            Issue(new WiredSpecialCommand(Player, World.HeroOf(Player) is not { } hero || World.AnchorPhaseOf(hero) is not (AnchorPhase.Locking or AnchorPhase.Anchored)));
        else if ((special & (ChassisSpecial.Field | ChassisSpecial.Mines | ChassisSpecial.Locate)) != 0)
            Issue(new WiredSpecialCommand(Player));
        else if ((special & (ChassisSpecial.Plough | ChassisSpecial.Afterburner)) != 0)
            RunRefused(Lang.T("{0} is on {1}", Lang.T((special & ChassisSpecial.Plough) != 0 ? "Ram" : "Afterburner"), KeyMap.Name(KeyMap.Act.Dash)));
        else if ((special & (ChassisSpecial.Lurk | ChassisSpecial.Harvest)) != 0)
            RunRefused(Lang.T("{0} works by itself", Lang.T((special & ChassisSpecial.Lurk) != 0 ? "Lurk" : "Harvest")));
        else
            RunRefused(HangarText.Reason(World.WhyNoSpecial(state) ?? "this chassis has nothing to use"));
    }

    /// <summary>A constructor's key takes up its towers, or puts them away while they are up (Game.Towers).</summary>
    partial void ToggleTowerMode();

    private void SquadFollows()
    {
        if (World.HeroOf(Player) is { } hero)
            SquadOrder(KeyMap.Act.SquadFollow, squad => new FollowCommand(Player, squad, hero.Id));
        else
            RunRefused(HangarText.Reason("the hero is down"));
    }

    /// <summary>What the sight rests on is attacked; the squad goes back to following once it has fallen.</summary>
    private void SquadAttacks()
    {
        if (World.HeroOf(Player) is not { } hero)
        {
            RunRefused(HangarText.Reason("the hero is down"));
            return;
        }
        var (_, target) = SightAt(GetViewport().GetVisibleRect().Size / 2, OrderSight);
        if (target is null || !World.CanAttack(Player, target))
        {
            RunRefused(HangarText.Reason("nothing in the sight to attack"));
            return;
        }
        SquadOrder(KeyMap.Act.SquadAttack, squad => new SeriesCommand(Player, squad, [new AttackCommand(Player, [], target.Id), new FollowCommand(Player, [], hero.Id)]),
            squad => new AttackCommand(Player, squad, target.Id));
    }

    private void SquadBack()
    {
        if (World.WiredPod(Player) is { } pod)
            SquadOrder(KeyMap.Act.SquadBack, squad => new MoveCommand(Player, squad, pod.EntranceOutside ?? pod.Position));
        else
            RunRefused(HangarText.Reason("the pod is gone"));
    }

    /// <summary>An order to all the squad, with the click and the first machine's answer of any other order.</summary>
    /// <param name="answered">The order as the machines answer it, where that is not the one they get.</param>
    private void SquadOrder(KeyMap.Act act, Func<IReadOnlyList<int>, Command> order, Func<IReadOnlyList<int>, Command>? answered = null)
    {
        var squad = World.Escorts(Player).Select(escort => escort.Id).ToList();
        if (squad.Count == 0)
        {
            RunRefused(HangarText.Reason("there is no squad to order"));
            return;
        }
        if (RefusesOrders())
            return;
        Issue(order(squad));
        _audio.PlayGui("gui/sounds/taskmach.wav");
        Answer((answered ?? order)(squad));
        _hud.Wired?.OrderGiven(act);
        if (_sceneScripted)
            GD.Print($"scene: order {act} to {squad.Count}");
    }

    /// <summary>An ability goes off where the sight rests; the world says what is wrong with that.</summary>
    private void UseAbility(int slot)
    {
        if (RefusesOrders())
            return;
        var (sight, _) = SightAt(GetViewport().GetVisibleRect().Size / 2, OrderSight);
        Issue(new WiredAbilityCommand(Player, slot, new NVector2(sight.X, sight.Z)));
    }

    /// <summary>The thrusters throw the hero the way the moves point, seen from the view, and straight ahead with none held.</summary>
    private void Dash()
    {
        if (RefusesOrders())
            return;
        if (_driven is not { } machine)
        {
            Issue(new WiredDashCommand(Player));
            return;
        }
        var yaw = FreeLook ? _aimYaw : machine.Facing + HeadNow(machine);
        var ahead = new NVector2(MathF.Cos(yaw), MathF.Sin(yaw));
        // The client shows the world mirrored: left on screen is the simulation's right.
        var left = new NVector2(ahead.Y, -ahead.X);
        // Sideways only where Left and Right step sideways while driving; where they turn, they turn and do not dash.
        var steps = !GameOptions.ClassicDriving && !GameOptions.ClassicKeys && machine.CanStrafe;
        var sideways = steps ? (KeyMap.Held(KeyMap.Act.Left) ? 1f : 0f) - (KeyMap.Held(KeyMap.Act.Right) ? 1f : 0f) : 0;
        var way = ahead * ForwardKeys() + left * sideways;
        Issue(new WiredDashCommand(Player, way == NVector2.Zero ? ahead : way));
    }
}
