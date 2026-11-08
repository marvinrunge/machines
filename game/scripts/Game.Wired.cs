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
    }

    /// <summary>Called every frame of a run, after the world's ticks.</summary>
    private void FollowRun(double delta)
    {
        if (World.Wired is not { } run)
            return;
        BeginRun();
        FollowBody(run);
        FollowNight((float)delta);
        ActorView.Driver = _driven is not null || _heroDown;
        var hero = World.HeroOf(Player);
        _marks!.Show(delta, hero is not null && _views.TryGetValue(hero.Id, out var view) ? view.Centre : null);
        if (run.Heroes.TryGetValue(Player, out var state))
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
        _runBegun = true;
        if (KeyMap.GaveWay.Count > 0)
            _hud.Message(KeyMap.GaveWayNote());
        if (Watching || Cinematic.Asked is not null)
            return;
        // A save from the seconds the hero was down: the driver is back on the static, waiting for the new body.
        if (World.HeroOf(Player) is not { } hero)
        {
            if (World.Wired?.Heroes.GetValueOrDefault(Player) is { RespawnAt: not null })
                WaitForBody();
            return;
        }
        // A save from the ground stays there; one from the last moments of the down has the new body already, and it is driven.
        if (!hero.DrivenByPlayer && MatchRequest.Current.Saved is not null && _newBody != hero.Id)
            return;
        Drive(hero);
        if (World.WiredPod(Player) is { } pod && NVector2.DistanceSquared(hero.Position, pod.Position) > 1 && MatchRequest.Current.Saved is null)
        {
            var away = hero.Position - pod.Position;
            _aimYaw = MathF.Atan2(away.Y, away.X);
        }
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
        if (World.HeroOf(Player) is not { } hero)
        {
            RunRefused(HangarText.Reason("the hero is down"));
            return false;
        }
        if (_selection.Count != 1 || _selection[0] != hero.Id)
            SelectHero(hero);
        return true;
    }

    /// <summary>
    /// The driver's way when the machine is gone: in a run a new body may be coming, and until it does the view stays on
    /// the static with its countdown. Anything else, and the end of a run, leaves first person as ever.
    /// </summary>
    private void DriverLost()
    {
        if (World.Wired?.Heroes.GetValueOrDefault(Player) is not { RespawnAt: not null })
        {
            LeaveFirstPerson();
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
        if (World.WiredPod(Player) is { } pod)
            _firstPersonCamera.GlobalPosition = ActorView.World(pod.Position) + Vector3.Up * 4;
        Input.MouseMode = Input.MouseModeEnum.Captured;
        _hud.FirstPerson = true;
        _screen.Enter();
        DriverLost();
    }

    /// <summary>The new body has its view by now: a driver waiting on the static gets in, one up in the air gets it selected.</summary>
    private void FollowBody(WiredRun run)
    {
        if (_heroDown && run.Phase == WiredPhase.Ended)
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

    /// <summary>While the hero is down, the view has nothing to steer: it can only be left.</summary>
    private void DownInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false } key && (key.Keycode == Key.Escape || KeyMap.Is(key, KeyMap.Act.FirstPerson)))
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
                case GameEventKind.WiredChipChoiceReady when e.Other == (int)Player:
                    hud.ChipChoiceWaits();
                    break;
                case GameEventKind.WiredWreckCollected when e.Actor == World.HeroOf(Player)?.Id:
                    _audio.Play("SID_RESOURCE_PICKUP", at);
                    hud.Float(at + Vector3.Up * 2, $"+{e.Detail} BMU");
                    _marks!.Fly(at + Vector3.Up);
                    break;
                case GameEventKind.WiredCrateCollected when e.Actor == World.HeroOf(Player)?.Id && e.Detail is { } crate:
                    _audio.Play("SID_RESOURCE_PICKUP", at);
                    _audio.PlayGui("gui/sounds/stats.wav");
                    hud.Float(at + Vector3.Up * 2, HangarText.Name(crate), new Color(0.45f, 1, 0.7f));
                    break;
                case GameEventKind.WiredIonWarning:
                    _audio.PlayGui("gui/sounds/hpcritic.wav");
                    break;
                case GameEventKind.WiredHeroRespawned when e.Other == (int)Player:
                    _newBody = e.Actor;
                    break;
                case GameEventKind.Hit when e.Actor == _driven?.Id && e.Other is { } from && World.Find(from) is { } shooter:
                    hud.HitFrom(ActorView.World(shooter.Position));
                    break;
            }
        }
    }

    /// <summary>A wave that forces night has it fall, whatever the hour and the planet; the morning after it comes as fast.</summary>
    private void FollowNight(float delta) =>
        _planetLook.ForcedNight = Mathf.MoveToward(_planetLook.ForcedNight, World.ForcedDarkness ?? 0, delta / NightFalls);
}
