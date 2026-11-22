using Godot;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Game;

// The story's hacking on the client (plan WP-E): the board goes up over the world while the world has this player at a
// hack, and comes down when the world says it is over. The world decides everything; here it is only shown and asked.
public partial class Game
{
    private HackHost? _hack;
    private bool _hackKey;

    /// <summary>
    /// Whether the hack's board is up over the world, taking the mouse and the keys: while it is played, and while a won
    /// one stands lit a moment before it goes (<see cref="HackHost.Holding"/>).
    /// </summary>
    public bool HackShown => _hack is not null && IsInstanceValid(_hack) && (!_hack.Closing || _hack.Holding);

    /// <summary>The hack's board while it is up, for automated checks.</summary>
    public HackHost? HackBoardShown => HackShown ? _hack : null;

    /// <summary>
    /// Plugs the Locator into the nearest target in reach that is not hacked yet; false when there is none (or no story,
    /// or the board is already up). The world may still refuse it, and says why.
    /// </summary>
    public bool TryOpenHack()
    {
        if (World.Story is not { } story || Watching || HackShown || MatchOver || World.HeroOf(Me) is not { } hero)
            return false;
        var desk = story.Hacks;
        var near = desk.Targets(World.Deck)
            .Where(t => !desk.Done.Contains(t.Target) && !desk.Locked.Contains(t.Target))
            .Select(t => (t.Target, Distance: System.Numerics.Vector2.Distance(t.Position, hero.Position)))
            .Where(t => t.Distance <= GameWorld.HackReach)
            .OrderBy(t => t.Distance)
            .ThenBy(t => t.Target, StringComparer.Ordinal)
            .FirstOrDefault();
        if (near.Target is null)
            return false;
        Issue(new StoryHackOpenCommand(Player, near.Target));
        return true;
    }

    /// <summary>Each frame of a story: the board up while the world has this player at a hack, down when that ends.</summary>
    private void FollowHack()
    {
        if (World.Story is not { } story)
            return;
        if (!_hackKey)
        {
            _hackKey = true;
            AddChild(new HackKey(this) { Name = "HackKey" });
            // The board's chip is made now, hidden (in the warm-up's hidden frames when there is one), and put up for each hack.
            var layer = new CanvasLayer { Layer = 10, Name = "Hack" };
            AddChild(layer);
            _hack = new HackHost(this);
            _hack.Released += GiveBackFromHack;
            layer.AddChild(_hack);
        }
        // A chapter that has ended takes the board down with it.
        var session = Watching || MatchOver ? null : story.Hacks.OpenBy(Me);
        if (HackShown && !_hack!.Closing && session?.Target != _hack.Target)
            TakeDownHack();
        // A new hack while a won board still stands lit puts its own board up at once.
        if (session is not null && (!HackShown || _hack!.Holding))
            PutUpHack(session);
        ScriptHack();
    }

    private void PutUpHack(HackSession session)
    {
        // What the mouse was doing on the field ends here, as when the hangar opens.
        EndBand();
        _rightPress = null;
        _rig.Drag = Vector2I.Zero;
        var since = HitchLog.Start;
        _hack!.Open(session.Target, session.Kind);
        var made = HitchLog.Start;
        _rig.Active = false;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        if (HitchLog.On)
            HitchLog.Mark($"hack board up ({System.Diagnostics.Stopwatch.GetElapsedTime(since, made).TotalMilliseconds:F1} ms, mouse {System.Diagnostics.Stopwatch.GetElapsedTime(made).TotalMilliseconds:F1} ms)");
    }

    /// <summary>
    /// The board plays out how the hack ended and goes; the mouse goes back to driving at once, or after a won board has
    /// stood lit its moment (<see cref="GiveBackFromHack"/>).
    /// </summary>
    private void TakeDownHack() => _hack!.Finish();

    /// <summary>The chip let go of the mouse and the keys: back to driving, unless another board or a cutscene has them now.</summary>
    private void GiveBackFromHack()
    {
        if (HackShown || InCutscene)
            return;
        _rig.Active = _driven is null;
        if (_driven is not null)
            Input.MouseMode = Input.MouseModeEnum.Captured;
        GetViewport().GuiReleaseFocus();
    }

    // ---- MACHINES_HACK_SCRIPT ------------------------------------------------------------------------

    private enum HackStep { Waiting, Opening, Playing, Done }

    private HackStep _hackStep;
    private double _hackNext, _hackShotAt = -1;
    private int _hackClicks;
    private string? _hackShot;
    private (int Stage, HackPuzzle? Board) _hackLast;
    private readonly List<(double At, string Path)> _hackFrames = [];

    /// <summary>
    /// For automated checks in a story match: MACHINES_HACK_SCRIPT=&lt;target&gt;[,&lt;png&gt;] sets the Locator down at that
    /// target's marker (clamp_l7, terminal_ike, hack_s, hack_core, pod_3_hatch …) after two seconds, plugs in, and plays the
    /// board's own solution with real mouse clicks on the board (the right button for a turn back) until the hack is won,
    /// traced or broken off; it prints how it went and the world's checksum. With a picture it saves the window half way
    /// through the first board, and around each board's last turn &lt;png&gt;-b&lt;n&gt;-1-before, -2-turning, -3-held, -4-held-late
    /// and -5-closed (the last board) or -5-next, and quits at the end. The match must start without the menus, e.g. with
    /// MACHINES_STORY=prolog MACHINES_SCREENSHOT=&lt;png&gt; MACHINES_SCREENSHOT_AFTER=90.
    /// </summary>
    private void ScriptHack()
    {
        if (_hackStep == HackStep.Done || System.Environment.GetEnvironmentVariable("MACHINES_HACK_SCRIPT")?.Split(',') is not [var target, .. var rest])
            return;
        var now = Time.GetTicksMsec() / 1000.0;
        var desk = World.Story!.Hacks;
        switch (_hackStep)
        {
            case HackStep.Waiting:
                if (World.Tick < 2 * GameWorld.TicksPerSecond || World.HeroOf(Me) is not { } hero)
                    return;
                if (desk.Find(target, World.Deck) is not { } found)
                {
                    EndHackScript($"hack script: no target {target}");
                    return;
                }
                _hackShot = rest.Length > 0 ? rest[0] : null;
                hero.Position = found.Position;
                SyncViews();
                Issue(new StoryHackOpenCommand(Player, target));
                (_hackStep, _hackNext) = (HackStep.Opening, now + 10);
                return;
            case HackStep.Opening:
                if (HackShown)
                    (_hackStep, _hackNext) = (HackStep.Playing, now + 0.8);
                else if (now > _hackNext)
                    EndHackScript($"hack script: the hack at {target} did not open");
                return;
        }
        // The pictures around a board's last turn, each when its time comes.
        foreach (var frame in _hackFrames.Where(f => now >= f.At).ToList())
        {
            GetViewport().GetTexture().GetImage().SavePng(frame.Path);
            GD.Print($"hack script: picture saved to {frame.Path} (board {(HackShown ? _hack!.Holding ? "held" : "up" : "gone")}, tick {World.Tick})");
            _hackFrames.Remove(frame);
        }
        var session = desk.Session(target);
        if (session is null || !session.Open || session.Broken)
        {
            if (now < _hackNext || _hackFrames.Count > 0)
                return;
            var how = desk.Done.Contains(target) ? "won" : session?.Broken == true ? "broken off" : session?.Alarms > 0 ? "traced" : "left";
            EndHackScript($"hack script: {target} {how} after {_hackClicks} clicks, trace {session?.Puzzle?.Trace}, checksum {World.Checksum():x16}");
            return;
        }
        if (_hackShot is not null && _hackShotAt > 0 && now >= _hackShotAt)
        {
            GetViewport().GetTexture().GetImage().SavePng(_hackShot);
            GD.Print($"hack script: picture saved to {_hackShot}");
            _hackShotAt = -2;
        }
        if (now < _hackNext || session.Puzzle is not { } puzzle || _hack is not { } host)
            return;
        var turns = puzzle.SolutionTurns().ToList();
        if (turns.Count == 0)
            return;
        // Half way through the first board: as many turns made as are left.
        if (_hackShotAt == -1 && _hackShot is not null && session.Stage == 0 && puzzle.TurnsMade >= turns.Count)
        {
            _hackShotAt = now + 0.35;
            _hackNext = now + 0.8;
            return;
        }
        // A board's last turn with a picture asked for: the board before it, then as the tile turns, as the won board
        // stands lit, and after the chip has gone (the next board, or the world).
        if (_hackShot is not null && turns.Count == 1 && _hackLast != (session.Stage, puzzle))
        {
            _hackLast = (session.Stage, puzzle);
            var name = $"{System.IO.Path.ChangeExtension(_hackShot, null)}-b{session.Stage + 1}";
            GetViewport().GetTexture().GetImage().SavePng($"{name}-1-before.png");
            GD.Print($"hack script: picture saved to {name}-1-before.png");
            var last = session.Stage == session.Boards.Count - 1;
            foreach (var (after, what) in new[] { (0.15, "2-turning"), (0.9, "3-held"), (1.8, "4-held-late"), (last ? 3.2 : 1.6, last ? "5-closed" : "5-next") })
                _hackFrames.Add((now + 0.05 + after, $"{name}-{what}.png"));
            _hackNext = now + 0.05;
            return;
        }
        var (cell, clockwise) = turns[0];
        var board = host.Board;
        var at = board.GetGlobalTransformWithCanvas() * board.CellRect(cell).GetCenter();
        var button = clockwise ? MouseButton.Left : MouseButton.Right;
        GetViewport().PushInput(new InputEventMouseMotion { Position = at, GlobalPosition = at });
        GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = button, Pressed = true, Position = at, GlobalPosition = at });
        GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = button, Pressed = false, Position = at, GlobalPosition = at });
        _hackClicks++;
        // A step a little over the world's turn and the tile's ease, so each click is on the board as it lies.
        _hackNext = now + 0.4;
    }

    private void EndHackScript(string line)
    {
        GD.Print(line);
        _hackStep = HackStep.Done;
        if (_hackShot is null)
            return;
        if (_hackShotAt == -1)
        {
            GetViewport().GetTexture().GetImage().SavePng(_hackShot);
            GD.Print($"hack script: picture saved to {_hackShot}");
        }
        GetTree().CreateTimer(1.0).Timeout += () => GetTree().Quit();
    }
}
