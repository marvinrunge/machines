using Godot;
using Machines.Sim.Survival;
using Machines.Sim.World;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

// A Wired run in the match: it starts in the hero, follows its deaths and new bodies, and has the view say what the
// world does. In any other match World.Wired is null and none of this does anything. Wired is the remake's own addition.
public partial class Game
{
    /// <summary>Seconds a forced night takes to fall, and the morning after it to come back.</summary>
    private const float NightFalls = 6;

    /// <summary>Seconds from the end of a run to its end screen: the world stands still for the last of them.</summary>
    private const int WiredHold = 3;

    private WiredMarks? _marks;
    private WiredPortrait? _portrait;
    private bool _runBegun, _heroDown;
    private int? _newBody;

    /// <summary>The picture of a run's hero, for what has no bitmap for its made-up combination of machine and weapons.</summary>
    public Texture2D? HeroPicture(Actor actor) => HeroOf(actor) is not null ? _portrait?.Picture : null;

    /// <summary>The view of an actor, while it has one.</summary>
    public ActorView? ViewOf(int actor) => _views.GetValueOrDefault(actor);

    /// <summary>What the player looks out of: the machine driven, the team-mate looked on through, or else the hero.</summary>
    private Machine? Eyes => _driven ?? (WatchedHero is { } watched ? World.Find<Machine>(watched) : null) ?? World.HeroOf(Me);

    /// <summary>
    /// Whether an enemy hero hides in its shadow from the player (GameWorld.Shadowed): quiet for long enough, not located,
    /// and farther from what the player looks out of than the shadow lets it be seen. The radar and the map show no blip of
    /// it, the sight neither its bars nor a lock, a battle's marks leave it out; its model stands in the view as ever, for a
    /// player to find by eye.
    /// </summary>
    public bool InShadow(Actor actor) => World.Wired is not null && actor is Machine && Eyes is { } eyes && World.Shadowed(eyes, actor);

    /// <summary>Whether an enemy of the player stands located by a geo locator's pulse (GameWorld.LocatedFor): the view marks it.</summary>
    public bool LocatedFoe(Actor actor) => World.Wired is not null && actor is Machine && World.AreEnemies(Player, actor.Race) && World.LocatedFor(actor.Id) > 0;

    /// <summary>What goes wrong in a run says so over the plates; in first person the message line is far from the eye.</summary>
    private void RunRefused(string why)
    {
        if (_sceneScripted)
            GD.Print($"scene: refused: {why}");
        if (_hud.Wired is { } hud)
            hud.Refuse(why);
        else
            _hud.Message(why);
    }

    /// <summary>
    /// The same hero on the same arena once more, from a new seed, with the chips the profile has opened by now. The run that
    /// ended keeps its recording.
    /// </summary>
    public void RunAgain()
    {
        SaveReplay();
        var options = MatchRequest.Current.Options;
        MatchRequest.Current = MatchRequest.Current with
        {
            Saved = null,
            Seed = null,
            ResumeFrom = null,
            Options = options with { Wired = options.Wired! with { ChipPool = WiredProfile.Load().ChipPool } },
        };
        GetTree().ChangeSceneToFile("res://scenes/Main.tscn");
    }

    private void SetUpRun()
    {
        ActorView.Driver = false;
        ActorView.Run = World.Wired is not null;
        if (World.Wired is null)
            return;
        AddChild(_marks = new WiredMarks(this));
        AddChild(_portrait = new WiredPortrait(_assets));
        WiredEndScript.Attend(GetTree());
        if (World.Battle is not null)
            BattleShots.Attend(GetTree());
    }

    /// <summary>Called every frame of a run, after the world's ticks.</summary>
    private void FollowRun(double delta)
    {
        if (World.Wired is not { } run)
            return;
        BeginRun();
        FollowBody(run);
        FollowNight((float)delta);
        FollowWatch(delta);
        ActorView.Driver = _driven is not null || _heroDown || _watched is not null;
        var hero = World.HeroOf(Me);
        _marks!.Show(delta, hero is not null && _views.TryGetValue(hero.Id, out var view) ? view.Centre : null);
        if (run.Heroes.TryGetValue(Me, out var state))
            _portrait!.Follow(Look(Player), state.Loadout);
        ScriptRun();
    }

    /// <summary>
    /// A run starts in the hero, looking out from the pod. A save made in first person does too: what it recorded leaves the
    /// hero driven by a driver who has to take over again.
    /// </summary>
    private void BeginRun()
    {
        if (_runBegun)
            return;
        // Online, a hero that is not the player's yet (the world rebuilt, joining late, catching up or fallen behind) is not
        // got into: in a battle the view looks on from behind it while its bot drives; the run begins once it is the player's
        // (P2 design §5.4, §6.3).
        if (Online && HeroHeld && Cinematic.Asked is null)
        {
            if (World.Battle is not null && _watched is null && World.HeroOf(Me) is { } held)
                StartWatching(held.Id);
            return;
        }
        _runBegun = true;
        if (KeyMap.GaveWay.Count > 0)
            _hud.Message(KeyMap.GaveWayNote());
        if (Watching || Cinematic.Asked is not null)
            return;
        // A battle a bot plays for the player is looked on from behind its hero.
        if (BattleWatch)
        {
            StartWatching();
            return;
        }
        // A save from the seconds the hero was down (or, in a run of several heroes, waiting for the wave's end): the
        // driver is back on the static, waiting for the new body.
        if (World.HeroOf(Me) is not { } hero)
        {
            if (World.Wired?.Heroes.GetValueOrDefault(Me) is { RespawnAt: not null } or { WaitsForWave: true })
            {
                LookOnNoLonger();
                WaitForBody();
            }
            return;
        }
        // A save from the ground stays there; one from the last moments of the down has the new body already, and it is driven.
        if (!hero.DrivenByPlayer && MatchRequest.Current.Saved is not null && _newBody != hero.Id)
            return;
        LookOnNoLonger();
        Drive(hero);
        if (World.WiredPod(Me) is { } pod && NVector2.DistanceSquared(hero.Position, pod.Position) > 1 && MatchRequest.Current.Saved is null)
        {
            var away = hero.Position - pod.Position;
            _aimYaw = MathF.Atan2(away.Y, away.X);
        }
    }

    /// <summary>
    /// Online, the view that looked on from behind the hero while it was not the player's (<see cref="HeroHeld"/>) ends where
    /// the run begins: driven while still looking on, the keys and the mouse would go on to the onlooker (WatchInput) and its
    /// camera would follow the hero from behind over the driver's.
    /// </summary>
    private void LookOnNoLonger()
    {
        if (Online && _watched is not null)
            StopWatching();
    }

    /// <summary>Gets into a hero with no more ado than a new body needs: no voice, no list of keys.</summary>
    private void Drive(Machine hero)
    {
        SelectHero(hero);
        EnterFirstPerson();
    }

    private void SelectHero(Machine hero)
    {
        foreach (var id in _selection)
            if (_views.TryGetValue(id, out var was))
                was.Selected = false;
        _selection.Clear();
        _selection.Add(hero.Id);
        _squadronSelected = null;
    }

    /// <summary>First person in a run goes into the hero, whatever is selected; it may be down, and then says so.</summary>
    private bool IntoTheHero()
    {
        if (World.HeroOf(Me) is not { } hero)
        {
            // Out of a battle for good, the view goes to a team-mate instead.
            if (World.Battle is { Over: false } && World.Wired?.Heroes.GetValueOrDefault(Me) is { RespawnAt: null } && StartWatching())
                return false;
            RunRefused(HangarText.Reason("the hero is down"));
            return false;
        }
        if (_selection.Count != 1 || _selection[0] != hero.Id)
            SelectHero(hero);
        return true;
    }

    /// <summary>
    /// The driver's way when the machine is gone: in a run a new body may be coming, and until it does the view stays on
    /// the static with its countdown, or, in a run of several heroes, until the wave is over. Anything else, and the end of a
    /// run, leaves first person as ever.
    /// </summary>
    private void DriverLost()
    {
        if (World.Wired?.Heroes.GetValueOrDefault(Me) is not ({ RespawnAt: not null } or { WaitsForWave: true }))
        {
            LeaveFirstPerson();
            // The last body of a battle is gone: the player looks on through a team-mate still fighting, or at its end through a hero still standing.
            if (World.Battle is not null)
                StartWatching();
            return;
        }
        _driven = null;
        _heroDown = true;
        _screen.Hold();
        _hud.HeroDown();
    }

    /// <summary>A driver who comes into a run with the hero down: the same static as if it had just fallen, looked at from the pod.</summary>
    private void WaitForBody()
    {
        _firstPersonFromGround = !ZenithCamera;
        _rig.Active = false;
        _firstPersonCamera.Current = true;
        if (World.WiredPod(Me) is { } pod)
            _firstPersonCamera.GlobalPosition = ActorView.World(pod.Position) + Vector3.Up * 4;
        Input.MouseMode = Input.MouseModeEnum.Captured;
        _hud.FirstPerson = true;
        _screen.Enter();
        DriverLost();
    }

    /// <summary>The new body has its view by now: a driver waiting on the static gets in, one up in the air gets it selected.</summary>
    private void FollowBody(WiredRun run)
    {
        if (_heroDown && (run.Phase == WiredPhase.Ended || World.Battle is { Over: true }))
        {
            _heroDown = false;
            LeaveFirstPerson();
        }
        if (_newBody is not { } id || !_views.ContainsKey(id) || World.Find<Machine>(id) is not { } body)
            return;
        _newBody = null;
        if (_heroDown)
        {
            _heroDown = false;
            Drive(body);
        }
        else if (_driven is null)
            Select([body.Id]);
    }

    /// <summary>
    /// A run or battle going on keeps its driver in the hero, on the static while it is down and looking on through a
    /// team-mate once a battle has no body left for them: no view from above, which in a battle would look over the
    /// whole field. Esc opens the menus over it; the end of the run, and a match that is not a run, leave as ever.
    /// </summary>
    private bool KeptInFirstPerson => World.Wired is { Phase: not WiredPhase.Ended } && World.Battle is not { Over: true } && !MatchOver && !Watching;

    /// <summary>A screen over first person, the hangar's or the menus': behind it the hero stands, and the mouse does not look.</summary>
    private bool ScreenOver => HangarOpen || MenuShown is not null;

    /// <summary>While the hero is down, the view has nothing to steer: Esc opens the menus (in a run) or leaves.</summary>
    private void DownInput(InputEvent @event)
    {
        if (KeptInFirstPerson && @event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            OpenMenu();
            return;
        }
        if (!KeptInFirstPerson && @event is InputEventKey { Pressed: true, Echo: false } key && (key.Keycode == Key.Escape || KeyMap.Is(key, KeyMap.Act.FirstPerson)))
        {
            _heroDown = false;
            LeaveFirstPerson();
        }
        else
            RunInput(@event);
    }

    /// <summary>What the world did for a run to show: called out, floated up, spoken.</summary>
    private void ShowRunEvents()
    {
        if (World.Wired is null || _hud.Wired is not { } hud)
            return;
        ShowBattleEvents(hud);
        foreach (var e in World.Events)
        {
            var at = ActorView.World(e.Position);
            switch (e.Kind)
            {
                case GameEventKind.WiredWaveStarted:
                    hud.WaveBegins(e.Other ?? 0, e.Detail);
                    _audio.PlayGui("gui/sounds/ctrlpon.wav");
                    _audio.Say("VID_POD_REINFORCEMENTS_ONLINE", queue: true);
                    break;
                case GameEventKind.WiredSpecialWave when Enum.TryParse<WiredSpecial>(e.Detail, out var special):
                    hud.WaveIsSpecial(special);
                    if (special == WiredSpecial.Night)
                        _audio.Say("VID_POD_NIGHTFALL_APPROACHING", queue: true);
                    else if (special == WiredSpecial.IonStorm)
                        _audio.Say("VID_POD_ENEMY_ION_CANNON_ONLINE", queue: true);
                    break;
                case GameEventKind.WiredWaveCleared:
                    hud.WaveCleared(e.Other ?? 0, e.Detail ?? "0");
                    _audio.PlayGui("gui/sounds/taskmach.wav");
                    if (_planetLook.ForcedNight > 0)
                        _audio.Say("VID_POD_DAYBREAK_APPROACHING", queue: true);
                    break;
                case GameEventKind.WiredRunEnded when Enum.TryParse<WiredEnding>(e.Detail, out var ending):
                    hud.RunEnds(ending);
                    break;
                case GameEventKind.WiredBossAppeared when e.Detail is { } boss:
                    hud.BossAppears(boss);
                    _audio.PlayGui("gui/sounds/hpcritic.wav");
                    break;
                case GameEventKind.WiredBossBelowHalf when e.Detail is { } boss:
                    hud.BossCallsForHelp(boss);
                    break;
                case GameEventKind.WiredChipChoiceReady when Mine(e):
                    hud.ChipChoiceWaits();
                    break;
                case GameEventKind.WiredWreckCollected when e.Actor == World.HeroOf(Me)?.Id:
                    _audio.Play("SID_RESOURCE_PICKUP", at);
                    hud.Float(at + Vector3.Up * 2, $"+{e.Detail} BMU");
                    _marks!.Fly(at + Vector3.Up);
                    break;
                case GameEventKind.WiredWrecksSwept when Mine(e):
                    _audio.PlayGui("gui/sounds/stats.wav");
                    _hud.Message(Lang.T("Wrecks left lying swept up at half: +{0} BMU", e.Detail ?? "0"));
                    break;
                case GameEventKind.WiredCrateCollected when e.Actor == World.HeroOf(Me)?.Id && e.Detail is { } crate:
                    _audio.Play("SID_RESOURCE_PICKUP", at);
                    _audio.PlayGui("gui/sounds/stats.wav");
                    hud.Float(at + Vector3.Up * 2, HangarText.Name(crate), new Color(0.45f, 1, 0.7f));
                    break;
                case GameEventKind.WiredIonWarning:
                    _audio.PlayGui("gui/sounds/hpcritic.wav");
                    break;
                // A team-mate's new body is theirs: the view jumps into the player's own only.
                case GameEventKind.WiredHeroRespawned when Mine(e):
                    _newBody = e.Actor;
                    break;
                case GameEventKind.Hit when e.Actor == _driven?.Id && e.Other is { } from && World.Find(from) is { } shooter:
                    hud.HitFrom(ActorView.World(shooter.Position));
                    break;
                case GameEventKind.WiredHarvest:
                    // The armour coming back sounds as a supercharger's mending; the player's own block flashes with it.
                    _audio.Play("SID_SUPERHEAL", at);
                    if (e.Actor == World.HeroOf(Me)?.Id)
                        hud.Harvested(e.Detail ?? "0");
                    break;
                default:
                    ShowSpecialEvent(e, at);
                    break;
            }
        }
    }

    /// <summary>
    /// What the chassis' specials do in the view: a ram's blow, a Colossus' tracks locking and coming free, a squad called
    /// in and a foe fetched nearer the pod going out where they stood and in where they stand, a constructor's towers set
    /// down, ready and pulled down, a geo locator's pulse going out. They come before the views follow the world, so a
    /// view still stands where its actor was.
    /// </summary>
    private void ShowSpecialEvent(GameEvent e, Vector3 at)
    {
        var view = _views.GetValueOrDefault(e.Actor);
        switch (e.Kind)
        {
            case GameEventKind.WiredRammed:
                _audio.Play("SID_PUNCH", at);
                view?.Shake();
                if (_driven is { } driven && (driven.Id == e.Actor || driven.Id == e.Other))
                    CameraRig.Shake(at, 4, 0.4f);
                break;
            case GameEventKind.WiredAnchoring or GameEventKind.WiredReleasing:
                _audio.Play("SID_DOOR", at);
                break;
            case GameEventKind.WiredAnchored:
                // The tracks bite: the clamps shut and dust comes up along them.
                _audio.Play("SID_DOOR", at);
                if (view is not null)
                    _effects.KickUp(view.GlobalPosition, new Vector2(view.Bounds.Size.X, view.Bounds.Size.Z) * 0.55f);
                break;
            case GameEventKind.WiredReleased when view is not null:
                _effects.KickUp(view.GlobalPosition, new Vector2(view.Bounds.Size.X, view.Bounds.Size.Z) * 0.45f);
                break;
            case GameEventKind.WiredSquadRallied or GameEventKind.WiredFoePulled when view is not null:
                view.TeleportAway();
                _audio.Play("SID_ENTERED_APC", World.Find(e.Actor) is { } moved ? ActorView.World(moved.Position) : view.GlobalPosition);
                break;
            case GameEventKind.WiredTowerPlaced:
                _effects.KickUp(at, new Vector2(3, 3));
                if (World.Find(e.Actor) is { } placed && placed.Owner == Me)
                    _audio.PlayGui("gui/sounds/taskmach.wav");
                break;
            case GameEventKind.ConstructionComplete when World.Find(e.Actor) is Construction tower && tower.IsType(WiredTower.Type) && tower.Owner == Me:
                _audio.Say("VID_DOZER_BUILDING_COMPLETE");
                _hud.Message(Lang.T("Tower ready"));
                _hud.Wired?.Float(at + Vector3.Up * 4, Lang.T("Tower ready"), new Color(0.45f, 1, 0.7f));
                break;
            case GameEventKind.WiredTowerScrapped:
                _vaporised.Add(e.Actor);
                _audio.Play("SID_RECYCLE", at);
                // The scrap goes to the tower's owner (its constructor's hero), so only they see it come in.
                if (view?.Race == Player && view.OwnedBy == Me)
                    _hud.Wired?.Float(at + Vector3.Up * 3, $"+{e.Detail} BMU");
                break;
            case GameEventKind.WiredLocated:
                _pulsedAt[e.Actor] = World.Time;
                // Beside the original's locating ring the pulse runs out over the ground as far as it reaches, and the
                // player's own sweeps round the edge of the view, which in first person shows little of the ground.
                if (World.Find<Machine>(e.Actor) is { } locator)
                    _effects.LocatePulse(at, World.ScannerReach(locator), LocateGlow);
                if (e.Actor == World.HeroOf(Me)?.Id)
                {
                    _hud.Message(Lang.T("{0} located", e.Other ?? 0));
                    _hud.Wired?.Pulsed();
                }
                break;
        }
    }

    /// <summary>When each geo locator hero last sent out its pulse, by the world's clock: its ring plays for a while after.</summary>
    private readonly Dictionary<int, float> _pulsedAt = [];

    /// <summary>The amber of a pulse running out over the ground, as the HUD's brackets round what it has located.</summary>
    private static readonly Color LocateGlow = new(1f, 0.62f, 0.12f);

    /// <summary>How long a geo locator hero's ring plays after its pulse, seconds.</summary>
    private const float PulseShown = 1.5f;

    /// <summary>Whether a geo locator's pulse went out only just now, which its view plays as the locating ring.</summary>
    private bool Pulsing(Machine locator) =>
        _pulsedAt.TryGetValue(locator.Id, out var at) && World.Time - at < PulseShown && World.Time >= at;

    /// <summary>A wave that forces night has it fall, whatever the hour and the planet; the morning after it comes as fast.</summary>
    private void FollowNight(float delta) =>
        _planetLook.ForcedNight = Mathf.MoveToward(_planetLook.ForcedNight, World.ForcedDarkness ?? 0, delta / NightFalls);
}
