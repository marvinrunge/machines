using System.Diagnostics.CodeAnalysis;
using Godot;
using Machines.Sim.Survival;
using Machines.Sim.World;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

// A constructor hero's tower mode in a Wired run, taken up and put away with the special's key while it is driven: the
// tower chosen stands as a ghost where the sight meets the ground, within the hero's reach, turned as the hero faces by
// quarter turns, green where the world lets it stand and red where not, and gone over a tower of the player's own, which
// the HUD then names as what stands in the way. The wheel goes through the towers the chassis
// builds and pulling one down; the left trigger places the tower or pulls down the player's tower in the sight, the right
// trigger, the special's key or Esc put the towers away, and no weapon fires meanwhile. Nothing here changes the world but
// through WiredTowerCommand and WiredScrapTowerCommand. In any other match none of this does anything. Wired is the
// remake's own addition.
public partial class Game
{
    /// <summary>A choice on the tower mode's strip: a tower of the catalogue, or pulling one down when <see cref="Tower"/> is null.</summary>
    /// <param name="Price">What the tower costs the hero now.</param>
    /// <param name="Builds">Whether the hero's chassis builds it: the wheel goes only through those and pulling down.</param>
    /// <param name="Why">Why it cannot be placed now wherever it goes, as the world says; null if only the place could be wrong.</param>
    /// <param name="OpensOn">The name of the first chassis of the catalogue that builds a tower the hero's does not.</param>
    public sealed record TowerChoice(WiredTower? Tower, int Price, bool Builds, string? Why, string? OpensOn);

    /// <summary>What the tower mode shows this frame.</summary>
    /// <param name="Choices">Every tower of the catalogue, smallest first, then pulling one down.</param>
    /// <param name="Chosen">The choice the wheel has come to.</param>
    /// <param name="Sight">Where the sight is on the screen.</param>
    /// <param name="Why">
    /// Why the tower chosen cannot stand where its ghost stands, or why the tower in the sight cannot be pulled down; null if
    /// it can.
    /// </param>
    /// <param name="Target">The player's tower in the sight, while pulling down is chosen.</param>
    /// <param name="Refund">What pulling that tower down brings back.</param>
    public sealed record TowerSight(IReadOnlyList<TowerChoice> Choices, int Chosen, Vector2 Sight, string? Why, int? Target, int Refund);

    /// <summary>What the tower mode shows while the towers are up; null while they are not.</summary>
    public TowerSight? Towers { get; private set; }

    /// <summary>How near the sight's point on the ground a tower stands to be pulled down when the sight does not meet its model.</summary>
    private const float ScrapNear = 2.5f;

    private bool _towersUp;

    // The choice the wheel has come to, kept for the next time the towers are taken up; the ghost of the tower chosen and
    // where it stands, turned by how many degrees.
    private int _towerChosen;
    private Ghost? _towerGhost;
    private string? _towerGhostOf;
    private NVector2 _towerSpot;
    private int _towerTurn;

    /// <summary>The special's key takes the towers up where the world lets the hero deal with them, and puts them away again.</summary>
    partial void ToggleTowerMode()
    {
        if (_towersUp)
        {
            LeaveTowerMode();
            return;
        }
        if (World.SpecialView(Me) is { Ready: false, Why: { } why })
        {
            RunRefused(HangarText.Reason(why));
            return;
        }
        if (!TowerModeHolds(out _, out _))
        {
            RunRefused(HangarText.Reason(World.HeroOf(Me) is null ? "the hero is down" : "only while driving the hero"));
            return;
        }
        _towersUp = true;
        // A trigger held as they come up fires no more, and fires again only once it is pressed anew after they go.
        _hands.LetGo();
        _audio.PlayGui("gui/sounds/igclick.wav");
        UpdateTowerMode();
    }

    /// <summary>The towers go away, and with them their ghost and strip.</summary>
    private void LeaveTowerMode()
    {
        if (!_towersUp)
            return;
        _towersUp = false;
        Towers = null;
        RemoveTowerGhost();
        _audio.PlayGui("gui/sounds/igclick.wav");
    }

    private void RemoveTowerGhost()
    {
        _towerGhost?.Remove();
        _towerGhost = null;
        _towerGhostOf = null;
    }

    /// <summary>
    /// Whether the towers can stay up: the player's hero of a run, not a battle's, driven out on the map in a chassis that
    /// builds towers, with no hangar open, while the match goes on and is not a replay.
    /// </summary>
    private bool TowerModeHolds([NotNullWhen(true)] out WiredState? state, [NotNullWhen(true)] out Machine? hero)
    {
        state = World.Wired?.Heroes.GetValueOrDefault(Me);
        hero = World.HeroOf(Me);
        return state is not null && hero is not null && World.Battle is null && !Watching && !MatchOver && !_heroDown && !HangarOpen
            && _driven?.Id == hero.Id && hero.Inside is null && (state.Build(World.Data).Chassis.Special & ChassisSpecial.Towers) != 0;
    }

    /// <summary>
    /// Every frame while the towers are up: the ghost of the tower chosen where the sight meets the ground, or the player's
    /// tower in the sight while pulling down is chosen, and what the HUD shows of it. The towers go away by themselves when
    /// the hero can no longer deal with them from where it is.
    /// </summary>
    private void UpdateTowerMode()
    {
        if (!_towersUp)
            return;
        if (!TowerModeHolds(out var state, out var hero))
        {
            LeaveTowerMode();
            return;
        }
        var choices = TowerChoices(state);
        if (_towerChosen >= choices.Count || !choices[_towerChosen].Builds)
            _towerChosen = Math.Max(0, choices.FindIndex(c => c.Builds));
        var sight = GetViewport().GetVisibleRect().Size / 2;
        var (ground, spot) = TowerSpot(hero, sight);
        if (choices[_towerChosen].Tower is { } tower)
        {
            var turn = QuarterTurn(hero.Facing);
            var why = World.WhyNoTower(state, tower.Id, spot, turn);
            if (_towerGhostOf != tower.Id)
            {
                RemoveTowerGhost();
                _towerGhost = new Ghost(this, WiredTower.Type, tower.SubType, tower.Level, TowerAllowed, TowerRefused);
                _towerGhostOf = tower.Id;
            }
            // Over a tower of the player's own, the one just set down or one standing, the ghost would stand inside it: it
            // goes, and where the world finds no room the HUD names that tower as what stands in the way.
            var (onOwn, againstOwn) = why is null ? (false, false) : (NearOwnTower(state, tower, spot, turn, 0), NearOwnTower(state, tower, spot, turn, OwnTowerNear));
            if (againstOwn && why == "obstacles or uneven terrain")
                why = "your tower stands there";
            if (onOwn)
                _towerGhost!.Hide();
            else
                _towerGhost!.Show(spot, turn, why is null);
            (_towerSpot, _towerTurn) = (spot, turn);
            Towers = new TowerSight(choices, _towerChosen, sight, why, null, 0);
            return;
        }
        RemoveTowerGhost();
        var target = ScrapTarget(state, sight, ground);
        Towers = new TowerSight(choices, _towerChosen, sight, target is null ? "no tower of yours there" : World.WhyNoScrap(state, target.Id),
            target?.Id, target is null ? 0 : World.ScrapRefund(state, target));
    }

    /// <summary>
    /// The strip's choices: every tower of the catalogue with its price and whether the chassis builds it, else the chassis
    /// that first does, and last pulling one down, which every constructor can.
    /// </summary>
    private List<TowerChoice> TowerChoices(WiredState state)
    {
        var builds = World.TowersFor(state.Build(World.Data).Chassis);
        var choices = World.TowerKinds(state).Select(kind => builds.Contains(kind.Tower)
            ? new TowerChoice(kind.Tower, kind.Price, true, kind.Why, null)
            : new TowerChoice(kind.Tower, kind.Price, false, kind.Why,
                WiredCatalogue.Chassis.FirstOrDefault(chassis => World.TowersFor(chassis).Contains(kind.Tower)) is { } opens ? HangarText.Name(opens.Id) : null)).ToList();
        choices.Add(new TowerChoice(null, 0, true, null, null));
        return choices;
    }

    /// <summary>
    /// Where the line of sight meets the ground, or with none straight ahead of the view at the hero's reach; and where the
    /// tower would stand, the same point brought within reach of the hero.
    /// </summary>
    private (NVector2 Ground, NVector2 Spot) TowerSpot(Machine hero, Vector2 sight)
    {
        var ahead = -GetViewport().GetCamera3D().GlobalBasis.Z;
        var flat = new NVector2(ahead.X, ahead.Z);
        flat = flat.LengthSquared() > 1e-6f ? NVector2.Normalize(flat) : new NVector2(MathF.Cos(hero.Facing), MathF.Sin(hero.Facing));
        var ground = GroundAt(sight) ?? hero.Position + flat * WiredBalance.TowerReach;
        var away = ground - hero.Position;
        // A little inside the reach, so that the place sent is not refused for lying on its edge.
        var most = WiredBalance.TowerReach - 0.25f;
        return (ground, away.Length() > most ? hero.Position + away / away.Length() * most : ground);
    }

    /// <summary>
    /// The tower ghost's tints: a deeper red than the placing's, and stronger, so that it reads as refused on sand and over
    /// a site as well as on grass.
    /// </summary>
    private static readonly Color TowerAllowed = new(0.3f, 1, 0.35f, 0.45f), TowerRefused = new(1, 0.1f, 0.08f, 0.6f);

    /// <summary>How near another footprint, in metres, a tower's stands against it: the world's grid takes in what lies that close.</summary>
    private const float OwnTowerNear = 1;

    /// <summary>
    /// Whether the tower set down there, turned by <paramref name="turn"/> degrees, would stand on one of the player's own,
    /// finished or not, or within <paramref name="near"/> metres of it. Only what the view shows: the world decides. The
    /// player's own are their hero's (<see cref="GameWorld.TowersOf(WiredState)"/>): in a run of several heroes a
    /// teammate's tower is in the way as anything else is.
    /// </summary>
    private bool NearOwnTower(WiredState state, WiredTower tower, NVector2 spot, int turn, float near)
    {
        var spec = World.Data.Parm.Construction($"{WiredTower.Type} {tower.SubType}");
        var (min, max) = new Construction(0, Player, spot, spec, spec.Level(tower.Level), turn).Footprint;
        return World.TowersOf(state).Any(own => min.X < own.Footprint.Max.X + near && own.Footprint.Min.X - near < max.X
            && min.Y < own.Footprint.Max.Y + near && own.Footprint.Min.Y - near < max.Y);
    }

    /// <summary>A heading in radians as a tower's turn: degrees by quarter turns, 0, 90, 180 or 270.</summary>
    private static int QuarterTurn(float facing) => ((int)MathF.Round(facing / (MathF.PI / 2)) % 4 + 4) % 4 * 90;

    /// <summary>
    /// The player's tower the sight meets, or else the nearest standing close to where it meets the ground: one of their
    /// hero's own, the only ones it may pull down (<see cref="GameWorld.WhyNoScrap"/>), never a teammate's beside it.
    /// </summary>
    private Construction? ScrapTarget(WiredState state, Vector2 sight, NVector2 ground)
    {
        var (_, met) = SightAt(sight, OrderSight);
        if (met is Construction { IsDead: false } seen && seen.Race == state.Race && seen.Owner == state.Player && seen.IsType(WiredTower.Type))
            return seen;
        return World.TowersOf(state).Where(t => t.DistanceFrom(ground) < ScrapNear).MinBy(t => t.DistanceFrom(ground));
    }

    /// <summary>
    /// While the towers are up the wheel, the triggers and Esc are theirs, before any panel hears of them: the wheel goes
    /// through the choices, the left trigger places or pulls down, the right one or Esc puts the towers away and the top
    /// one does nothing. True if the event was theirs.
    /// </summary>
    private bool TowerInput(InputEvent @event)
    {
        if (!_towersUp)
            return false;
        switch (@event)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel:
                ChooseTower(wheel.ButtonIndex == MouseButton.WheelDown ? 1 : -1);
                return true;
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }:
                LeaveTowerMode();
                return true;
        }
        if (KeyMap.Is(@event, KeyMap.Act.FireLeft))
        {
            PlaceOrScrap();
            return true;
        }
        if (KeyMap.Is(@event, KeyMap.Act.FireRight))
        {
            LeaveTowerMode();
            return true;
        }
        return KeyMap.Is(@event, KeyMap.Act.FireTop);
    }

    /// <summary>The wheel goes on to the next choice the chassis can take that way, round from the last to the first.</summary>
    private void ChooseTower(int step)
    {
        if (Towers is not { } shown)
            return;
        var count = shown.Choices.Count;
        for (var i = 1; i <= count; i++)
        {
            var next = ((_towerChosen + step * i) % count + count) % count;
            if (!shown.Choices[next].Builds)
                continue;
            if (next != _towerChosen)
                _audio.PlayGui("gui/sounds/navkey.wav");
            _towerChosen = next;
            break;
        }
        UpdateTowerMode();
    }

    /// <summary>
    /// The left trigger: the tower chosen goes where its ghost stands, or the tower in the sight comes down, as the world
    /// allows. Where the HUD already says why not under the sight, the click is refused with the original's sound.
    /// </summary>
    private void PlaceOrScrap()
    {
        if (RefusesOrders() || Towers is not { } shown)
            return;
        var choice = shown.Choices[shown.Chosen];
        if (shown.Why is not null)
            _audio.PlayGui("gui/sounds/clickbad.wav");
        else if (choice.Tower is { } tower)
            Issue(new WiredTowerCommand(Player, tower.Id, _towerSpot, _towerTurn));
        else if (shown.Target is { } target)
        {
            Issue(new WiredScrapTowerCommand(Player, target));
            _audio.PlayGui("gui/sounds/taskmach.wav");
        }
    }
}
