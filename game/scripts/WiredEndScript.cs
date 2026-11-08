using Godot;

namespace Machines.Game;

/// <summary>
/// For automated checks of how a run ends, in the real game with real input events: MACHINES_END_SCRIPT=&lt;folder&gt;,&lt;step&gt;+&lt;step&gt;
/// answers the end screens that come up, one step each, and stays on over the changes of scene, printing what it sees as
/// "end: ...". A step is &lt;what&gt;[:&lt;picture&gt;[:skip]]: <c>again</c>, <c>hangar</c> and <c>menu</c> press that button, <c>esc</c>
/// presses Escape, <c>look</c> presses nothing and quits, <c>hold</c> stays on a while first for a file that was held, and <c>skip</c>
/// first cuts the show short with a click. <c>hangar</c>, <c>esc</c> and <c>menu</c> go on to show what arrived there. After the last
/// step the game quits.
/// </summary>
public partial class WiredEndScript(string folder, string[] steps) : Node
{
    private static bool _attending;

    private Vector2 _mouse = new(4, 4);

    /// <summary>Comes along with a run if the switch says so; it outlives the scene that called it.</summary>
    public static void Attend(SceneTree tree)
    {
        if (_attending || System.Environment.GetEnvironmentVariable("MACHINES_END_SCRIPT") is not { Length: > 0 } script)
            return;
        _attending = true;
        var words = script.Split(',');
        tree.Root.CallDeferred(Node.MethodName.AddChild, new WiredEndScript(words[0], words[1].Split('+')));
    }

    private static void Say(string text) => GD.Print($"end: {text}");

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private async Task Frames(int count = 2)
    {
        for (var i = 0; i < count; i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private void Picture(string name)
    {
        GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(folder, name + ".png"));
        Say($"picture {name}");
    }

    private static Vector2 Middle(Control control) => control.GetGlobalTransform() * (control.Size / 2);

    private async Task Click(Vector2 at)
    {
        GetViewport().PushInput(new InputEventMouseMotion { Position = at, GlobalPosition = at, Relative = at - _mouse });
        _mouse = at;
        await Frames(4);
        GetViewport().PushInput(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left });
        await Frames();
        GetViewport().PushInput(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left, Pressed = false });
        await Frames();
    }

    private T? Find<T>() where T : class => GetTree().Root.FindChildren("*", "", true, false).OfType<T>().FirstOrDefault();

    private async Task<T?> Await<T>(double seconds) where T : class
    {
        for (var waited = 0.0; waited < seconds; waited += 0.1)
        {
            if (Find<T>() is { } found)
                return found;
            await Wait(0.1);
        }
        return null;
    }

    public override async void _Ready()
    {
        foreach (var step in steps)
        {
            var words = step.Split(':');
            if (!await Answer(words[0], words.Length > 1 ? words[1] : "end", words.Length > 2 && words[2] == "skip"))
                break;
        }
        GetTree().Quit();
    }

    private async Task<bool> Answer(string what, string picture, bool skip)
    {
        for (var waited = 0.0; WiredEndScreen.Current is null; waited += 0.25)
        {
            if (waited > 240)
            {
                Say("no end screen came");
                return false;
            }
            await Wait(0.25);
        }
        var page = WiredEndScreen.Current!;
        var from = Time.GetTicksMsec();
        Say($"screen up: {page.Figures}");
        if (GetTree().CurrentScene is Game { } game)
            Say($"the world: {game.RunReport}");
        // A press in the first moment must do nothing: the hands are at the fight.
        await Wait(0.3);
        await Click(new Vector2(GetViewport().GetVisibleRect().Size.X / 2, GetViewport().GetVisibleRect().Size.Y - 60));
        foreach (var key in new[] { Key.Space, Key.Enter })
        {
            GetViewport().PushInput(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true });
            await Frames();
            GetViewport().PushInput(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false });
            await Frames();
        }
        Say($"a click on the buttons, Space and Enter in the first moment: screen {(WiredEndScreen.Current == page ? "stays" : "went")}, done {page.Done}");
        await Wait(1.1);
        Picture(picture + "-show");
        if (skip)
        {
            await Click(new Vector2(200, 200));
            Say($"clicked into the show after {(Time.GetTicksMsec() - from) / 1000.0:0.0} s: done {page.Done}");
            await Wait(0.15);
            Picture(picture + "-skipped");
        }
        var cheered = false;
        for (var waited = 0.0; !page.Done && waited < 30; waited += 0.05)
        {
            if (page.Cheered && !cheered)
            {
                cheered = true;
                await Wait(0.14);
                Picture(picture + "-cheer");
            }
            await Wait(0.05);
        }
        Say($"the show took {(Time.GetTicksMsec() - from) / 1000.0:0.0} s");
        await Wait(0.9);
        Picture(picture);
        Say($"now: {page.Figures}");
        Overflow(page);
        if (what == "look")
            return false;
        if (what == "hold")
        {
            await Wait(25);
            Picture(picture + "-later");
            Say($"later: {page.Figures}");
            return false;
        }
        if (what == "esc")
        {
            GetViewport().PushInput(new InputEventKey { Keycode = Key.Escape, PhysicalKeycode = Key.Escape, Pressed = true });
            await Frames();
            GetViewport().PushInput(new InputEventKey { Keycode = Key.Escape, PhysicalKeycode = Key.Escape, Pressed = false });
            Say("pressed Esc");
            return await After("hangar", picture);
        }
        var word = what switch { "again" => "Again", "hangar" => "Hangar", _ => "Menu" };
        var button = page.ButtonOf(Lang.T(word).ToUpperInvariant());
        if (button is null)
        {
            Say($"no button {word}");
            return false;
        }
        await Click(Middle(button));
        Say($"pressed {word}");
        return await After(what, picture);
    }

    private async Task<bool> After(string what, string picture)
    {
        switch (what)
        {
            case "again":
                await Wait(1);
                for (var waited = 0.0; GetTree().CurrentScene is not Game { World.Tick: > 0 } && waited < 40; waited += 0.25)
                    await Wait(0.25);
                await Wait(1.5);
                if (GetTree().CurrentScene is Game { } game)
                {
                    var options = MatchRequest.Current.Options.Wired!;
                    Say($"a new run: hero '{options.HeroName}', {options.Arena}, {options.Difficulty}, pool {options.ChipPool.Count}, saved {MatchRequest.Current.Saved is not null}");
                    Say($"the world: {game.RunReport}");
                }
                return true;
            case "hangar":
                return await Hangar(await Await<HangarPage>(20), picture);
            default:
                var menu = await Await<MainMenuPage>(20);
                await Wait(1.5);
                Say($"the menu is up: {menu is not null}, new things {WiredProfile.Load().Fresh.Count}");
                Picture(picture + "-menu");
                if (menu?.FindChildren("*", "", true, false).OfType<ChipButton>().FirstOrDefault(b => b.Text == Lang.T("Wired").ToUpperInvariant()) is not { } wired)
                    return false;
                await Click(Middle(wired));
                return await Hangar(await Await<HangarPage>(20), picture) && await BackToMenu(picture);
        }
    }

    private async Task<bool> Hangar(HangarPage? page, string picture)
    {
        if (page is null)
        {
            Say("no hangar came");
            return false;
        }
        await Wait(2.5);
        var fresh = page.Cards.Where(c => c.Good.Fresh).Select(c => c.Good.Item.Catalogue);
        Say($"the hangar is up: new on the shelf [{string.Join(", ", fresh)}], says '{page.Arrival}', in the file {WiredProfile.Load().Fresh.Count}");
        Picture(picture + "-hangar");
        foreach (var shelf in page.Shelves.ToList())
        {
            await Click(Middle(page.TabOf(shelf)));
            await Wait(0.4);
            Say($"shelf {shelf}: new [{string.Join(", ", page.Cards.Where(c => c.Good.Fresh).Select(c => c.Good.Item.Catalogue))}]");
            if (page.Cards.Any(c => c.Good.Fresh))
                Picture($"{picture}-hangar-{shelf.ToString().ToLowerInvariant()}");
        }
        if (page.FindChildren("*", "", true, false).OfType<ChipChoice>().ToList() is { Count: >= 2 } choices)
        {
            foreach (var choice in choices)
                Say($"choice: [{string.Join(", ", choice.Items)}], on {choice.Selected}");
            var start = page.FindChildren("*", "", true, false).OfType<ChipButton>().First(b => b.Text == Lang.T("Start the mission").ToUpperInvariant());
            foreach (var choice in choices)
                for (var i = 0; i < choice.Items.Count; i++)
                {
                    await Click(Middle(choice) + new Vector2(30, 0));
                    await Wait(0.25);
                    Say($"stepped to {choice.Items[choice.Selected]}: start {(start.Disabled ? "closed" : "open")}, says '{page.Bar}'");
                    if (start.Disabled)
                        Picture($"{picture}-locked-{choice.Selected}");
                }
        }
        return true;
    }

    private async Task<bool> BackToMenu(string picture)
    {
        if (Find<HangarPage>() is not { } page)
            return false;
        page.Back();
        await Wait(1.5);
        Say($"back on the menu: new things {WiredProfile.Load().Fresh.Count}");
        Picture(picture + "-menu-after");
        return true;
    }

    /// <summary>Everything on the page has to be inside the window, what scrolls apart. A control's size does not scale with its parents, its transform does.</summary>
    private void Overflow(WiredEndScreen page)
    {
        var window = GetViewport().GetVisibleRect().Grow(1);
        Rect2 Rect(Control control)
        {
            var transform = control.GetGlobalTransform();
            return new Rect2(transform.Origin, control.Size * transform.Scale);
        }
        bool Scrolled(Node node)
        {
            for (var at = node.GetParent(); at is not null; at = at.GetParent())
                if (at is ScrollContainer)
                    return true;
            return false;
        }
        var outside = page.FindChildren("*", nameof(Control), true, false).OfType<Control>().Where(c => c.IsVisibleInTree() && !Scrolled(c) && !window.Encloses(Rect(c)))
            .Select(c => $"{c.GetType().Name} {Rect(c)}").ToList();
        Say($"window {window.Size}, stage {page.StageSize}: {outside.Count} controls outside{(outside.Count > 0 ? ": " + string.Join("; ", outside.Take(8)) : "")}");
        foreach (var scroll in page.FindChildren("*", nameof(ScrollContainer), true, false).OfType<ScrollContainer>())
            Say($"a scroll of {scroll.Size.Y:0} holds {scroll.GetChild<Control>(0).Size.Y:0}");
    }
}
