using Godot;

namespace Machines.Game;

/// <summary>
/// For automated checks of a battle's screens, in the real game: MACHINES_BATTLE_SHOTS=&lt;folder&gt;[,&lt;name&gt;][,end=look|pod|menu|again|room]
/// comes along with a battle (MACHINES_BATTLE starts one, or a battle room of MACHINES_MP), prints how it stands as "battle: ..." lines every ten seconds,
/// and keeps pictures of its moments as &lt;name&gt;-&lt;moment&gt;.png: the opening's countdown, the first capture that gets
/// under way, the hero down waiting for a body, the view on a team-mate once it is out, and the end screen once its show is
/// over. With <c>pod</c>, <c>menu</c> or <c>again</c> it then presses that button and keeps what comes; then the game quits.
/// </summary>
public partial class BattleShots(string folder, string name, string end) : Node
{
    private static bool _attending;

    private readonly HashSet<string> _taken = [];

    /// <summary>Comes along with a battle if the switch says so; it outlives the scene that called it.</summary>
    public static void Attend(SceneTree tree)
    {
        if (_attending || System.Environment.GetEnvironmentVariable("MACHINES_BATTLE_SHOTS") is not { Length: > 0 } setting)
            return;
        _attending = true;
        var words = setting.Split(',');
        var end = words.Skip(1).FirstOrDefault(w => w.StartsWith("end=", StringComparison.Ordinal))?[4..] ?? "look";
        var name = words.Skip(1).FirstOrDefault(w => !w.Contains('=')) ?? "battle";
        Directory.CreateDirectory(words[0]);
        tree.Root.CallDeferred(Node.MethodName.AddChild, new BattleShots(words[0], name, end));
    }

    private static void Say(string text) => GD.Print($"battle: {text}");

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private void Picture(string moment)
    {
        if (!_taken.Add(moment))
            return;
        GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(folder, $"{name}-{moment}.png"));
        Say($"picture {name}-{moment}");
    }

    private static Game? Match(SceneTree tree) => tree.CurrentScene as Game;

    private static string State(Game game)
    {
        var world = game.World;
        var now = game.BattleHud?.Battle;
        if (world.Battle is not { } run || now is null)
            return $"tick {world.Tick}, no battle";
        string Team(BattleTeamReadout team) => string.Join(" ", team.Heroes.Select(h =>
            $"{h.Name}({h.Chassis} {(h.Out ? "out" : h.BackIn is { } back ? $"back in {back:0}" : $"{h.Health * 100:0}%")} lives {h.LivesLeft}/{h.Lives} kills {h.Kills})"));
        return $"tick {world.Tick}, checksum {world.Checksum():x16}, clock {now.Seconds}, opening {now.Opening?.ToString() ?? "-"}, " +
               $"ours [{Team(now.Ours)}] taking {now.Ours.Progress:0}, theirs [{Team(now.Theirs)}] taking {now.Theirs.Progress:0}, " +
               $"watching {game.WatchedName ?? "-"}, hero down {game.HeroDown}, outcome {now.Outcome?.ToString() ?? "-"} {now.Reason?.ToString() ?? ""}";
    }

    /// <summary>Online, a match scene being loaded anew: true once the new one is up (three minutes at most), false offline or without one.</summary>
    private async Task<bool> Reloaded(SceneTree tree)
    {
        if (MatchRequest.Current.Network is null)
            return false;
        for (var waited = 0.0; waited < 180; waited += 0.25)
        {
            if (Match(tree) is { World.Tick: > 0, Hidden: false, Warming: false, BattleHud.Battle: not null })
            {
                Say("the match scene was loaded again");
                return true;
            }
            await Wait(0.25);
        }
        return false;
    }

    public override async void _Ready()
    {
        var tree = GetTree();
        // The match comes up behind the loading screen and the warm-up.
        for (var waited = 0.0; Match(tree) is not { World.Tick: > 0, Hidden: false, Warming: false }; waited += 0.25)
        {
            if (waited > 180)
            {
                Say("no battle came");
                tree.Quit();
                return;
            }
            await Wait(0.25);
        }
        var nextReport = 0.0;
        var downSince = -1.0;
        var watchSince = -1.0;
        for (var time = 0.0; time < 1200; time += 0.1)
        {
            if (Match(tree) is not { } game || game.BattleHud?.Battle is not { } now)
            {
                // Online the match scene is loaded anew when the world is rebuilt or the match rejoined (P2 design §5.4): the
                // pictures go on in the new one once it is up.
                if (!await Reloaded(tree))
                    break;
                continue;
            }
            if (time >= nextReport)
            {
                Say(State(game));
                nextReport = time + 10;
            }
            if (now.Opening is <= 6 and > 0)
                Picture("opening");
            if (now.Opening is null && now.Outcome is null && (now.Ours.Progress >= 15 || now.Theirs.Progress >= 15))
                Picture("capture");
            if (now.Opening is null && now.Outcome is null && !game.HeroDown && !now.Out && time > 45)
                Picture("fight");
            downSince = game.HeroDown && now.DownSeconds is not null ? downSince < 0 ? time : downSince : -1;
            if (downSince >= 0 && time - downSince > 1.5)
                Picture("down");
            watchSince = game.WatchedName is { } watched && watched != now.You.Name && now.Outcome is null ? watchSince < 0 ? time : watchSince : -1;
            if (watchSince >= 0 && time - watchSince > 2.5)
                Picture("watching");
            if (now.Outcome is not null)
            {
                Say($"ended: {State(game)}");
                await Wait(1.2);
                Picture("ended");
                break;
            }
            await Wait(0.1);
        }
        for (var waited = 0.0; BattleEndScreen.Current is not { Done: true }; waited += 0.25)
        {
            if (waited > 60)
            {
                Say("no end screen came");
                tree.Quit();
                return;
            }
            await Wait(0.25);
        }
        var page = BattleEndScreen.Current!;
        await Wait(1.5);
        Say($"end screen: {page.Figures}");
        Picture("end");
        if (end != "look")
        {
            var word = end switch { "pod" => "Pod", "again" => "Again", "room" => "Room", _ => "Menu" };
            // The menus would start the battle of the switch again at once, or the multiplayer page its automated room.
            if (word != "Again")
            {
                System.Environment.SetEnvironmentVariable("MACHINES_BATTLE", null);
                System.Environment.SetEnvironmentVariable("MACHINES_MP", null);
            }
            if (page.ButtonOf(Lang.T(word).ToUpperInvariant()) is { } button)
            {
                button.EmitSignal(BaseButton.SignalName.Pressed);
                Say($"pressed {word}");
                // Online, the room has to be opened again by its host and joined by the others.
                await Wait(word == "Again" ? 25 : word == "Room" ? 15 : 4);
                Picture($"after-{end}");
            }
            else
                Say($"no button {word}");
        }
        tree.Quit();
    }
}
