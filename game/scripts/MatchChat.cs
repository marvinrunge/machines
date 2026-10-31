using Godot;
using Machines.Import.Model;
using Machines.Net;

namespace Machines.Game;

/// <summary>
/// Chat in a multiplayer match, as the original's (MachPromptText, MachGuiInGameChatMessages): F1, F2 or F3 start a
/// message to the first, second or third other player, F4 one to everyone, typed in the prompt line beside the
/// control panel; the same key again puts the next of the game copy's standard messages (data/chatmsgs.txt) in it.
/// Enter sends, Esc or F10 drops it, and while the line is open every key is its own. Messages show at the top
/// beside the panel, five lines at most in the prompt font, and the oldest goes every eight seconds.
/// </summary>
public partial class MatchChat(Game game, ClassicPanel panel) : Control
{
    // MachGuiInGameChatMessages: five lines 430 pixels wide, the oldest dropped every eight seconds.
    private const int MaxLines = 5, ListWidth = 430;
    private const double LineLife = 8;

    /// <summary>True while a message is being typed: the keys are not the camera's then.</summary>
    public static bool Typing { get; private set; }

    private static List<string>? _standardMessages;

    private readonly List<string> _lines = [];
    private double _lastChange = Now;

    // The message being typed: the F key it was started with (0 to 3; null while none is), for whom (null for
    // everyone) and how the line says so, the text so far, the caret, and the next standard message.
    private int? _key;
    private Race? _to;
    private string _prefix = "", _text = "";
    private int _caret, _standard;

    /// <summary>Where the messages' first line goes; the HUD sets it beside the panel.</summary>
    public Vector2 ListAt { get; set; }

    /// <summary>How much of the screen the messages take below <see cref="ListAt"/>.</summary>
    public float ListHeight => _lines.Count * LineHeight * PixelScale;

    private static double Now => Time.GetTicksMsec() / 1000.0;

    private float PixelScale => panel.PixelScale;

    private static ClassicFont? PromptFont => ClassicFont.Get("menu/promtfnt.bmp");

    private static ClassicFont? PromptShadow => ClassicFont.Get("menu/promdfnt.bmp");

    private static int LineHeight => (PromptShadow?.Height ?? 8) + 1;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _ExitTree() => Typing = false;

    /// <summary>
    /// F1 to F4 (MachPromptText::doHandleKeyEvent): a message to that other player, or with F4 to everyone; pressed
    /// again while typing to the same, the next standard message. F1 to F3 do nothing for a player who is not there.
    /// </summary>
    public void Begin(int key)
    {
        if (key == _key)
        {
            var standard = StandardMessages();
            if (standard.Count > 0)
            {
                _text = standard[_standard++ % standard.Count];
                _caret = _text.Length;
            }
            return;
        }
        if (key < 3)
        {
            if (Opponents().ElementAtOrDefault(key) is not { } opponent)
                return;
            (_to, _prefix) = (opponent.Slot, $"Send to {opponent.Name}: ");
        }
        else
            (_to, _prefix) = (null, "Send to Everyone: ");
        (_key, _standard) = (key, 0);
        Typing = true;
    }

    /// <summary>A message from another player, unless it was for someone else.</summary>
    public void Receive(Chat chat)
    {
        // A relay from before chat could be addressed passes every message to everyone.
        if (chat.To is { } to && to != Game.Player)
            return;
        Add($"{chat.From}: {chat.Text}");
    }

    /// <summary>For automated checks: says a line to everyone, then, given another, opens the line with it typed in.</summary>
    public void Automate(string said, string typing)
    {
        Begin(3);
        _text = said;
        Send();
        if (typing.Length == 0)
            return;
        Begin(3);
        Insert(typing);
    }

    /// <summary>The other human players, in the match's order, for F1 to F3 (MachGuiInGameChatMessages::opponentName).</summary>
    private static IEnumerable<MatchPlayer> Opponents() =>
        MatchRequest.Current.Network?.Players.Where(p => !p.Ai && p.Slot != Game.Player) ?? [];

    private static List<string> StandardMessages()
    {
        if (_standardMessages is null)
        {
            var file = new ClassicAssets(Paths.Root).GameFolder is { } folder ? System.IO.Path.Combine(folder, "data", "chatmsgs.txt") : null;
            _standardMessages = file is not null && System.IO.File.Exists(file)
                ? System.IO.File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0).ToList()
                : [];
        }
        return _standardMessages;
    }

    private void Send()
    {
        var text = _text.Trim();
        if (text.Length > 0)
        {
            NetLink.Send(new Chat(NetLink.Name, text, _to));
            Add(_prefix + text);
        }
        Close();
    }

    private void Close()
    {
        (_key, _text, _caret) = (null, "", 0);
        Typing = false;
    }

    /// <summary>A message in the list, broken into lines that fit, with chatmsg.wav (MachGuiInGameChatMessages::addMessage).</summary>
    private void Add(string message)
    {
        game.Audio.PlayGui("gui/sounds/chatmsg.wav");
        _lines.AddRange(PromptFont?.Chop(message, ListWidth) ?? [message]);
        if (_lines.Count > MaxLines)
            _lines.RemoveRange(0, _lines.Count - MaxLines);
        _lastChange = Now;
    }

    private void Insert(string text)
    {
        text = new string(text.Where(c => !char.IsControl(c)).ToArray());
        text = text[..Math.Clamp(Chat.MaxMatchText - _text.Length, 0, text.Length)];
        _text = _text.Insert(_caret, text);
        _caret += text.Length;
    }

    public override void _Input(InputEvent @event)
    {
        if (_key is null || @event is not InputEventKey key)
            return;
        GetViewport().SetInputAsHandled();
        if (!key.Pressed)
            return;
        switch (key.Keycode)
        {
            case Key.Enter or Key.KpEnter when !key.Echo:
                Send();
                break;
            case Key.Escape or Key.F10 when !key.Echo:
                Close();
                break;
            case >= Key.F1 and <= Key.F4 when !key.Echo && !key.ShiftPressed && !key.CtrlPressed:
                Begin((int)(key.Keycode - Key.F1));
                break;
            case Key.Backspace when _caret > 0:
                _text = _text.Remove(--_caret, 1);
                break;
            case Key.Delete when _caret < _text.Length:
                _text = _text.Remove(_caret, 1);
                break;
            case Key.Left:
                _caret = Math.Max(0, _caret - 1);
                break;
            case Key.Right:
                _caret = Math.Min(_text.Length, _caret + 1);
                break;
            case Key.Home:
                _caret = 0;
                break;
            case Key.End:
                _caret = _text.Length;
                break;
            case Key.V when key.CtrlPressed && !key.AltPressed:
                Insert(DisplayServer.ClipboardGet());
                break;
            default:
                if (key.Unicode >= 32)
                    Insert(char.ConvertFromUtf32((int)key.Unicode));
                break;
        }
    }

    public override void _Process(double delta)
    {
        // Driving in first person there is no prompt line to type in.
        if (_key is not null && !panel.Visible)
            Close();
        if (Now - _lastChange > LineLife)
        {
            if (_lines.Count > 0)
                _lines.RemoveAt(0);
            _lastChange = Now;
        }
        TextureFilter = PixelScale % 1 == 0 ? TextureFilterEnum.Nearest : TextureFilterEnum.Linear;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var lineHeight = LineHeight * PixelScale;
        for (var i = 0; i < _lines.Count; i++)
            DrawShadowed(ListAt + new Vector2(0, i * lineHeight), _lines[i], ListWidth);
        if (_key is not null)
            DrawTyping();
    }

    /// <summary>
    /// The message being typed, in the prompt line with its lamp lit (MachPromptText::displayChatMessage): to whom,
    /// then the text, scrolled along so that the caret stays in sight.
    /// </summary>
    private void DrawTyping()
    {
        var s = PixelScale;
        var origin = panel.Position + new Vector2((ClassicPanel.PanelWidth - 7) * s, panel.Size.Y - 24 * s);
        var lampWidth = 6;
        if (ClassicGui.Bitmap("misc/tplight2.bmp") is { } lamp)
        {
            DrawTextureRect(lamp, new Rect2(origin, new Vector2(lamp.GetWidth(), lamp.GetHeight()) * s), false);
            lampWidth = lamp.GetWidth();
        }
        var at = origin + new Vector2(lampWidth + 1, 7 + (PromptShadow?.Height ?? 8) / 2) * s;
        var width = (int)((GetViewportRect().Size.X - at.X) / s) - 4;
        var prefixWidth = Measure(_prefix);
        DrawShadowed(at, _prefix, width);

        var room = width - prefixWidth - 2;
        var first = 0;
        while (first < _caret && Measure(_text[first.._caret]) > room)
            first++;
        var caretX = prefixWidth + Measure(_text[first.._caret]);
        DrawShadowed(at + new Vector2(prefixWidth * s, 0), _text[first..], room);
        if (Time.GetTicksMsec() / 500 % 2 == 0)
            DrawRect(new Rect2(at + new Vector2(caretX * s, 0), new Vector2(s, (PromptFont?.Height ?? 8) * s)), Colors.White);
    }

    private static int Measure(string text) =>
        PromptFont?.Measure(text) ?? (int)ThemeDB.FallbackFont.GetStringSize(text, HorizontalAlignment.Left, -1, 7).X;

    /// <summary>A line in the prompt font over its shadow, at most <paramref name="width"/> of the original's pixels long.</summary>
    private void DrawShadowed(Vector2 at, string text, int width)
    {
        var s = PixelScale;
        if (PromptFont is { } font && PromptShadow is { } shadow)
        {
            shadow.Draw(this, at + Vector2.One * s, text, s, width);
            font.Draw(this, at, text, s, width);
            return;
        }
        var size = (int)(7 * s);
        var baseline = at + new Vector2(0, ThemeDB.FallbackFont.GetAscent(size));
        DrawString(ThemeDB.FallbackFont, baseline + Vector2.One * s, text, HorizontalAlignment.Left, width * s, size, Colors.Black);
        DrawString(ThemeDB.FallbackFont, baseline, text, HorizontalAlignment.Left, width * s, size, Colors.White);
    }
}
