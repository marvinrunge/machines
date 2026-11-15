using Godot;
using Machines.Import.Model;
using Machines.Net;
using Machines.Sim.Survival;

namespace Machines.Game;

/// <summary>
/// The multiplayer page: connect to a relay or host one, then open a room (set up on the skirmish page) or join one,
/// pick a colour, get ready, and the host starts. The relay then hands every player the same match. The room lists its
/// start positions and its settings; its host says what each free position is for, takes players out and changes the
/// settings (MachGuiCtxImReady). In a battle room each player brings the hero last put together in their pod (Pod opens
/// it and comes back here), the room shows every player's chassis and tier, and after the battle the end screen's Room
/// brings the players back into a room like it.
/// </summary>
public partial class MultiplayerPage(MenuScreens screens, Action? backTo = null) : MenuPage(backTo ?? screens.ShowMain)
{
    private static readonly Race[] Colours = [Race.Red, Race.Blue, Race.Green, Race.Yellow];

    // Kept while the game runs, so the page comes back as it was left.
    private static string _address = "127.0.0.1:7777";
    private static string _statusText = "Host runs the relay in this game (port 7777) and asks the router to let the others in; " +
                                        "they connect to the address it shows. Or connect to a relay someone else runs.";

    // Whether hosting asks the router to open the port (UPnP, see PortMapping).
    private static bool _openPort = true;

    // The address to give the others, once hosting; Copy puts it on the clipboard.
    private static string? _shareAddress;

    // The room's chat (MachGuiCtxImReady's chat window), kept while the game runs as the page is.
    private static readonly List<(string Text, bool Note)> ChatLines = [];
    private static int? _chatRoom;

    // The room the player is in, so that the page comes back to it from the pod or the settings without waiting for the relay.
    private static RoomState? _known;

    // The room the last match was started from, and whether the end screen's Room asked to go back to one like it.
    private static RoomState? _matchRoom;
    private static bool _returning;

    // Coming back from the pod: the hero is sent again, ready or not as the pod's button said.
    private static bool? _fromPod;

    // The battle room (id, map and bracket) the pod's hero was last sent to by itself, so a refused one is not sent over and over.
    private static string? _heroSentFor;

    // MACHINES_MP's automated room is played once a run.
    private static bool _automated;

    private LineEdit _name = null!, _relay = null!, _say = null!;
    private Label _status = null!, _roomTitle = null!, _settings = null!;
    private RichTextLabel _chat = null!;
    private ItemList _rooms = null!;
    private VBoxContainer _seats = null!;
    private ChipChoice _colour = null!;
    private CheckButton _ready = null!;
    private ChipButton _connect = null!, _host = null!, _start = null!, _change = null!, _join = null!, _copy = null!, _pod = null!;
    private Control _offline = null!, _lobby = null!, _room = null!;
    private RoomList? _roomList;
    private RoomState? _state;
    private bool _busy, _leaving;

    protected override void Build()
    {
        var page = Column(Gap);
        AddChild(page);
        page.AddChild(Heading("Multiplayer"));
        var columns = Line(Gap);
        columns.SizeFlagsVertical = SizeFlags.ExpandFill;
        page.AddChild(columns);

        var login = new ChipPanel { CustomMinimumSize = new Vector2(480, 0), Padding = 22 };
        _name = new LineEdit { Text = NetLink.Name, PlaceholderText = "Your name", ContextMenuEnabled = false };
        _relay = new LineEdit { Text = _address, PlaceholderText = "Relay address", ContextMenuEnabled = false };
        _relay.TextChanged += text => _address = text;
        _connect = Button("Connect", () => _ = Connect(), 70, 26);
        _host = Button("Host", () => _ = Host(), 70, 26);
        _connect.SizeFlagsHorizontal = _host.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var openPort = new CheckButton { Text = "Open the port on the router", ButtonPressed = _openPort };
        openPort.AddThemeFontSizeOverride("font_size", 21);
        openPort.Toggled += on => _openPort = on;
        _status = MenuTheme.Paragraph(_statusText, 21, MenuTheme.Dim);
        _copy = Button("Copy address", () =>
        {
            DisplayServer.ClipboardSet(_shareAddress ?? "");
            _status.Text = _statusText + "\n" + Lang.T("Copied {0}.", _shareAddress);
        }, 60, 22);
        _copy.Visible = _shareAddress is not null && NetLink.HostedRelay is not null;
        login.AddChild(Column(14,
            MenuTheme.Label("YOUR NAME", 20, MenuTheme.Dim), _name,
            MenuTheme.Label("RELAY", 20, MenuTheme.Dim), _relay,
            Line(16, _connect, _host),
            openPort,
            Spring(across: false),
            _status,
            _copy));
        columns.AddChild(login);

        var rooms = new ChipPanel { SizeFlagsHorizontal = SizeFlags.ExpandFill, Padding = 22 };
        var views = new Control();
        rooms.AddChild(views);
        columns.AddChild(rooms);

        _offline = new CenterContainer();
        _offline.AddChild(MenuTheme.Label("Connect to a relay, or host one,\nto see its rooms.", 26, MenuTheme.Dim, HorizontalAlignment.Center));

        _rooms = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill };
        _rooms.ItemActivated += _ => Join();
        _join = Button("Join", Join, 70, 26);
        _join.TooltipText = "Join a room, or rejoin a match you dropped out of";
        _lobby = Column(16,
            MenuTheme.Label("ROOMS", 26, MenuTheme.Bright),
            _rooms,
            Line(16, Button("Refresh", () => NetLink.Send(new ListRooms()), 70, 26), _join, Spring(),
                Button("Open a room", () => screens.ShowSkirmish(forRoom: true), 70, 26)));

        _roomTitle = MenuTheme.Paragraph("", 26, MenuTheme.Bright);
        _seats = Column(6);
        _settings = MenuTheme.Paragraph("", 20, MenuTheme.Dim);
        // Below the players, the chat: the lines said so far, set into the board as the fields are, and one to type in.
        _chat = new RichTextLabel { SizeFlagsVertical = SizeFlags.ExpandFill, ScrollFollowing = true, FocusMode = FocusModeEnum.None };
        _chat.AddThemeStyleboxOverride("normal", GetThemeStylebox("read_only", "LineEdit"));
        _chat.AddThemeFontSizeOverride("normal_font_size", 22);
        foreach (var line in ChatLines)
            ShowChat(line);
        _say = new LineEdit
        {
            PlaceholderText = "Chat message", MaxLength = Chat.MaxText, ContextMenuEnabled = false,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, KeepEditingOnTextSubmit = true,
        };
        _say.TextSubmitted += _ => SendChat();
        var send = Button("Send", SendChat, 70, 26);
        _colour = new ChipChoice(Colours.Select(c => c.ToString())) { CustomMinimumSize = new Vector2(220, 0) };
        _colour.ItemSelected += _ => SendPlayer();
        _ready = new CheckButton { Text = "Ready" };
        _ready.Toggled += _ => SendPlayer();
        _pod = Button("Pod", OpenPod, 70, 26);
        _pod.TooltipText = "Put your hero together for this battle";
        _start = Button("Start match", () => NetLink.Send(new StartMatch()), 70, 26);
        _change = Button("Settings", () => screens.ShowSkirmish(forRoom: true, room: _state), 70, 26);
        var leave = Button("Leave room", () =>
        {
            NetLink.Send(new LeaveRoom());
            ShowLobby();
        }, 70, 26);
        _settings.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _room = Column(16, _roomTitle, _seats, Line(16, _settings, _change), _chat, Line(16, _say, send),
            Line(20, MenuTheme.Label("Colour", 22, MenuTheme.Text), _colour, _ready, Spring(), _pod, leave, _start));
        foreach (var view in new[] { _offline, _lobby, _room })
        {
            view.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            views.AddChild(view);
        }

        var back = Button("Back", Back);
        back.CustomMinimumSize = new Vector2(400, 76);
        page.AddChild(Line(Gap, back));

        if (NetLink.Connected && _known is { } known)
        {
            // Back from the pod or the settings: the room as it was, until the relay tells more.
            ShowRoom(known);
            if (_fromPod is { } ready && _state is not null)
            {
                _ready.SetBlockSignals(true);
                _ready.ButtonPressed = ready;
                _ready.SetBlockSignals(false);
                SendPlayer();
            }
        }
        else if (NetLink.Connected)
            ShowLobby();
        else
            ShowOffline();
        _fromPod = null;
        SwitchOn();
        Focus(NetLink.Connected ? _rooms : _connect);
        if (_returning)
        {
            _returning = false;
            if (NetLink.Connected && _matchRoom is { } was)
                _ = Return(was);
        }
        Automate();
    }

    private void Say(string text) => _status.Text = _statusText = Lang.T(text);

    /// <summary>A refusal of the relay or the sim, in the player's language and with a capital, in the status and the room's chat.</summary>
    private void Refuse(string reason)
    {
        var text = HangarText.Reason(reason);
        _status.Text = _statusText = text;
        if (_state is not null)
            AddChat(text, note: true);
    }

    /// <summary>Leaves for the match the relay started; what follows in the inbox is the match scene's to read.</summary>
    private void Leave()
    {
        _leaving = true;
        LoadingScreen.Show(GetTree(), LoadingScreen.MapName(MatchRequest.Current), Lang.T("MULTIPLAYER"));
    }

    public override void _Process(double delta)
    {
        if (_leaving || NetLink.Connection is not { } connection)
            return;
        while (connection.Inbox.TryRead(out var message))
        {
            switch (message)
            {
                case RoomList list:
                    _roomList = list;
                    _rooms.Clear();
                    foreach (var room in list.Rooms)
                        _rooms.AddItem(RoomLine(room));
                    _join.Disabled = list.Rooms.Count == 0;
                    break;
                case RoomState state:
                    ShowRoom(state);
                    break;
                case Refused refused:
                    Refuse(refused.Reason);
                    break;
                case MatchStarted started:
                    MatchRequest.Current = MatchRequest.FromMatch(started.Match, NetLink.ClientId) with { Network = started.Match };
                    (_matchRoom, _known) = (_state, null);
                    Leave();
                    return;
                case MatchResumed resumed:
                    _known = null;
                    // The match so far follows in the inbox; the match scene reads it from there.
                    MatchRequest.Current = MatchRequest.FromMatch(resumed.Match, NetLink.ClientId, resumed.Slot)
                        with { Network = resumed.Match, ResumeFrom = resumed.FromTick };
                    Leave();
                    return;
                case Chat chat:
                    AddChat($"{chat.From} : {chat.Text}");
                    MenuSounds.Chat();
                    break;
            }
        }
        if (connection.Closed)
        {
            Say("Disconnected from the relay.");
            ShowOffline();
            _ = NetLink.DisconnectAsync();
        }
    }

    private string PlayerName => string.IsNullOrWhiteSpace(_name.Text) ? "Player" : _name.Text;

    private async Task Connect()
    {
        if (_busy)
            return;
        _busy = true;
        Say("Connecting…");
        var error = await NetLink.ConnectAsync(_relay.Text, PlayerName);
        _busy = false;
        if (error is not null)
        {
            Say(error);
            return;
        }
        Say(Lang.T("Connected as {0}.", NetLink.Name));
        ShowLobby();
    }

    private async Task Host()
    {
        if (_busy)
            return;
        _busy = true;
        Say("Starting the relay…");
        var port = _relay.Text.Split(':') is [_, var p] && int.TryParse(p, out var parsed) ? parsed : 7777;
        var error = await NetLink.HostAsync(port, PlayerName);
        _busy = false;
        if (error is not null)
        {
            Say(error);
            return;
        }
        var relay = NetLink.HostedRelay!.Port;
        var near = string.Join(Lang.T(" or "), System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
            .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a))
            .Select(a => NetLink.Join(a, relay)));
        _shareAddress = near.Split(' ')[0];
        ShowLobby();
        if (!_openPort)
        {
            Say(Lang.T("Hosting. On this network the others connect to {0}; over the internet to your public address, with port {1} forwarded on the router.", near, relay));
            _copy.Visible = near.Length > 0;
            return;
        }
        Say(Lang.T("Hosting. Asking the router to open port {0}…", relay));
        var mapping = await NetLink.OpenPortAsync(GetTree());
        if (!IsInstanceValid(this) || mapping is null)
            return;
        // The internet's address first: IPv4 where the router has a public one, else this computer's IPv6.
        var far = new List<string>();
        if (mapping.PublicV4 is { } v4)
            far.Add(NetLink.Join(v4, relay));
        if (mapping.PublicV6 is { } v6)
            far.Add(NetLink.Join(v6, relay) + (mapping.V6Open ? " (IPv6)" : Lang.T(" (IPv6, if the router's firewall lets it in)")));
        _shareAddress = far.Count > 0 ? far[0].Split(' ')[0] : _shareAddress;
        Say(Lang.T("Hosting.") +
            (near.Length > 0 ? Lang.T(" On this network: {0}.", near) : "") +
            (far.Count > 0 ? Lang.T(" Over the internet: {0}.", string.Join(Lang.T(" or "), far)) : "") +
            (mapping.ProblemText is { } problem ? " " + Lang.T(problem, mapping.ProblemValues) : ""));
        _copy.Visible = _shareAddress is { Length: > 0 };
    }

    private void ShowOffline()
    {
        (_state, _known) = (null, null);
        screens.At(MenuTransition.Multiplayer);
        (_offline.Visible, _lobby.Visible, _room.Visible) = (true, false, false);
        _connect.Disabled = _host.Disabled = false;
    }

    private void ShowLobby()
    {
        (_state, _known) = (null, null);
        screens.At(MenuTransition.Multiplayer);
        (_offline.Visible, _lobby.Visible, _room.Visible) = (false, true, false);
        _connect.Disabled = _host.Disabled = true;
        _join.Disabled = _roomList is not { Rooms.Count: > 0 };
        NetLink.Send(new ListRooms());
    }

    private void ShowRoom(RoomState state)
    {
        var me = state.Players.FirstOrDefault(p => p.ClientId == NetLink.ClientId);
        if (me is null)
        {
            ShowLobby();
            return;
        }
        // A new room starts a new chat, with the keyboard on its line; in the same one, comings and goings are noted.
        if (state.Id != _chatRoom)
        {
            _chatRoom = state.Id;
            ChatLines.Clear();
            _chat.Clear();
            Focus(_say);
        }
        else if (_state is { } before)
        {
            foreach (var player in state.Players.Where(p => before.Players.All(b => b.ClientId != p.ClientId)))
                AddChat(Lang.T("{0} joined the room", player.Name), note: true);
            foreach (var player in before.Players.Where(b => state.Players.All(p => p.ClientId != b.ClientId)))
                AddChat(Lang.T("{0} left the room", player.Name), note: true);
        }
        (_state, _known) = (state, state);
        screens.At(MenuTransition.Room);
        (_offline.Visible, _lobby.Visible, _room.Visible) = (false, false, true);
        var battle = state.Options.Battle;
        _roomTitle.Text = battle is not null && BattleCatalogue.Map(state.Scenario) is { } map
            ? Lang.T("{0}   ·   Battle on {1}   ·   {2}   ·   {3}/{4} players", state.Name, BattlePod.MapName(map), SkirmishPage.BracketName(battle.Tier),
                state.Players.Count, state.Seats.Count) + Lang.T(", the rest bots")
            : Lang.T("{0}   ·   {1} on {2}   ·   {3}/{4} players", state.Name, state.Scenario, state.Planet, state.Players.Count,
                  state.Seats.Count(s => s.ClientId is not null || s.Use == SeatUse.Open)) +
              (state.FillWithAi ? Lang.T(", the rest AI") : "");
        _pod.Visible = battle is not null;
        ShowSeats(state);
        _settings.Text = SkirmishPage.Summary(state.Options);
        // The ground counts only when everyone has the same export of it (MatchInfo.Terrain).
        if (state.Players.Where(p => p.Ready).Select(p => p.Terrain).Distinct().Count() > 1)
            _roomTitle.Text += "\n" + Lang.T("The players' classic terrain exports differ: hills will not stop shots in this match");
        _colour.Selected = Array.IndexOf(Colours, me.Colour);
        _ready.SetBlockSignals(true);
        _ready.ButtonPressed = me.Ready;
        _ready.SetBlockSignals(false);
        _start.Visible = _change.Visible = state.HostId == NetLink.ClientId;
        _start.Disabled = state.Players.Any(p => !p.Ready);
        // In a battle room the hero comes from the pod by itself, once for each map and bracket the room has.
        if (battle is not null && me.Hero is null && $"{state.Id}/{state.Scenario}/{battle.Tier}" is var key && key != _heroSentFor)
        {
            _heroSentFor = key;
            SendPlayer();
        }
    }

    /// <summary>A line for each start position: who sits there or what it is for, and the host's choice for it.</summary>
    private void ShowSeats(RoomState state)
    {
        foreach (var row in _seats.GetChildren())
        {
            _seats.RemoveChild(row);
            row.QueueFree();
        }
        var map = state.Options.Battle is not null ? BattleCatalogue.Map(state.Scenario) : null;
        var slots = state.Slots.Order().ToList();
        for (var i = 0; i < state.Seats.Count; i++)
        {
            var seat = state.Seats[i];
            var player = state.Players.FirstOrDefault(p => p.ClientId == seat.ClientId);
            var text = player is null ? Lang.T(seat.Use.ToString())
                : $"{player.Name}   {Lang.T(player.Colour.ToString())}{(player.ClientId == state.HostId ? "   " + Lang.T("(host)") : "")}{(player.Ready ? "   " + Lang.T("ready") : "")}";
            if (map is not null)
            {
                // A battle seat is a race of one team: its hero is the player's, or a bot's if nobody sits there.
                var team = i < slots.Count && map.TeamB.Contains(slots[i]) ? 2 : 1;
                text = $"{Lang.T("Team {0}", team)}   ·   {text}   ·   {(player is null ? Lang.T("a bot hero") : HeroText(player.Hero))}";
            }
            var line = MenuTheme.Label(text, 22, player is null ? MenuTheme.Dim : MenuTheme.Text);
            line.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
            line.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            line.ClipText = true;
            var row = Line(16, line);
            if (state.HostId == NetLink.ClientId && player?.ClientId != NetLink.ClientId)
                row.AddChild(SeatChoice(i, seat.Use, player, battle: map is not null));
            _seats.AddChild(row);
        }
    }

    /// <summary>A player's hero in the room's list: chassis and tier, or that none is chosen yet.</summary>
    private static string HeroText(BattleHero? hero) => hero is null
        ? Lang.T("no hero yet")
        : $"{HangarText.Name(hero.Chassis)}, {Lang.T("Tier {0}", BattleDraft.Roman(BattleCatalogue.Tier(hero.Chassis)))}";

    /// <summary>A room in the lobby's list; a battle room with its map and bracket.</summary>
    private static string RoomLine(RoomSummary room)
    {
        var line = room.Mode == RoomSummary.Battle && BattleCatalogue.Map(room.Scenario) is { } map
            ? Lang.T("{0}   ·   Battle on {1}   ·   {2}   ·   {3}/{4}", room.Name, BattlePod.MapName(map), SkirmishPage.BracketName(room.Tier), room.Players, room.MaxPlayers)
            : Lang.T("{0}   ·   {1} on {2}   ·   {3}/{4}", room.Name, room.Scenario, room.Planet, room.Players, room.MaxPlayers);
        return line + (room.Started ? "   ·   " + Lang.T("playing") : "");
    }

    /// <summary>
    /// The host's choice for a start position (MachGuiPlayerListItem): Open, Closed or Computer, and at a player's seat
    /// their name first; choosing anything else takes them out of the room, once the host has confirmed it.
    /// </summary>
    private ChipChoice SeatChoice(int seat, SeatUse use, LobbyPlayer? player, bool battle = false)
    {
        // A battle has a hero at every seat: a seat cannot be closed there.
        var uses = Enum.GetValues<SeatUse>().Where(u => !battle || u != SeatUse.Closed).ToArray();
        var names = uses.Select(u => u.ToString());
        var choice = new ChipChoice(player is null ? names : names.Prepend(player.Name), player is null ? Math.Max(0, Array.IndexOf(uses, use)) : 0) { CustomMinimumSize = new Vector2(220, 0) };
        choice.ItemSelected += i =>
        {
            if (player is null)
                NetLink.Send(new SetSeat(seat, uses[i]));
            else if (i > 0)
                screens.Message(Lang.T("Take {0} out of the room?", player.Name),
                    ("Take out", () => NetLink.Send(new SetSeat(seat, uses[i - 1]))),
                    ("Keep", () =>
                    {
                        if (IsInstanceValid(choice))
                            choice.Selected = 0;
                    }));
        };
        return choice;
    }

    private void Join()
    {
        var selected = _rooms.GetSelectedItems();
        if (_roomList is null || selected.Length == 0)
            return;
        // A match already running takes back a player who dropped out of it.
        var room = _roomList.Rooms[selected[0]];
        NetLink.Send(room.Started ? new RejoinMatch(room.Id) : new JoinRoom(room.Id));
    }

    /// <summary>
    /// Sends the line typed to the others in the room and shows it here too: the relay does not hand it back, as the
    /// original's broker did not (MachGuiCtxImReady::doHandleKeyEvent).
    /// </summary>
    private void SendChat()
    {
        var text = _say.Text.Trim();
        if (text.Length == 0)
            return;
        NetLink.Send(new Chat(NetLink.Name, text));
        AddChat($"{NetLink.Name} : {text}");
        MenuSounds.Chat();
        _say.Clear();
    }

    /// <summary>A line in the room's chat; a note of the room's own (who came, who went) is dimmer.</summary>
    private void AddChat(string text, bool note = false)
    {
        ChatLines.Add((text, note));
        if (ChatLines.Count > 200)
        {
            ChatLines.RemoveRange(0, 100);
            _chat.Clear();
            foreach (var line in ChatLines)
                ShowChat(line);
        }
        else
            ShowChat((text, note));
    }

    private void ShowChat((string Text, bool Note) line)
    {
        if (_chat.GetParsedText().Length > 0)
            _chat.Newline();
        _chat.PushColor(line.Note ? MenuTheme.Dim : MenuTheme.Text);
        _chat.AddText(line.Text);
        _chat.Pop();
    }

    /// <summary>
    /// The player's colour, readiness and ground; in a battle room also the hero last put together in the pod. A hero the
    /// room's bracket does not take is not sent, and the player cannot be ready without one: the page says why, translated.
    /// </summary>
    private void SendPlayer()
    {
        BattleHero? hero = null;
        if (_state?.Options.Battle is { } battle)
        {
            hero = PodHero(Colours[_colour.Selected]);
            if (RoomOptions.WhyNotHere(hero, battle.Tier) is { } why)
            {
                Refuse(why);
                hero = null;
                _ready.SetBlockSignals(true);
                _ready.ButtonPressed = false;
                _ready.SetBlockSignals(false);
            }
        }
        NetLink.Send(new SetPlayer(Colours[_colour.Selected], _ready.ButtonPressed, TerrainPrint(_state?.Planet), hero));
    }

    /// <summary>The hero last put together in the pod, as far as the account still owns it.</summary>
    private static BattleHero PodHero(Race race) => new BattleDraft(Paths.Data, BattleAccounts.Load()).Hero(race);

    /// <summary>To the pod, not ready meanwhile; it comes back to this room.</summary>
    private void OpenPod()
    {
        if (_state is not { } state)
            return;
        _ready.SetBlockSignals(true);
        _ready.ButtonPressed = false;
        _ready.SetBlockSignals(false);
        SendPlayer();
        screens.ShowPod(state);
    }

    /// <summary>The pod goes back to the room, the player <paramref name="ready"/> with its hero or not.</summary>
    public static void FromPod(bool ready) => _fromPod = ready;

    /// <summary>The battle's end screen goes back to a room like the one the battle was started from.</summary>
    public static void BackToRoom() => _returning = true;

    /// <summary>
    /// After a match, into a room like the one it was started from: its host opens it again with the same name and settings,
    /// the others wait for it to show in the list (two minutes at most) and join it. The relay keeps no room past its match.
    /// </summary>
    private async Task Return(RoomState was)
    {
        if (was.HostId == NetLink.ClientId)
        {
            NetLink.Send(new CreateRoom(was.Name, was.Scenario, was.Planet, was.Slots, was.Options, was.FillWithAi));
            return;
        }
        var host = was.Players.FirstOrDefault(p => p.ClientId == was.HostId)?.Name ?? "";
        Say(Lang.T("Waiting for {0} to open the room again…", host));
        for (var waited = 0.0; waited < 120; waited += 1)
        {
            if (!IsInstanceValid(this) || !NetLink.Connected || _state is not null)
                return;
            if (_roomList?.Rooms.FirstOrDefault(r => r.Name == was.Name && r.Scenario == was.Scenario && !r.Started) is { } again)
            {
                NetLink.Send(new JoinRoom(again.Id));
                Say(Lang.T("Connected as {0}.", NetLink.Name));
                return;
            }
            NetLink.Send(new ListRooms());
            await ToSignal(GetTree().CreateTimer(1), SceneTreeTimer.SignalName.Timeout);
        }
        if (IsInstanceValid(this) && _state is null)
            Say(Lang.T("{0} has not opened the room again.", host));
    }

    private static readonly Dictionary<string, long?> TerrainPrints = [];

    /// <summary>The fingerprint of the ground this game copy's export gives a planet; null without one.</summary>
    private static long? TerrainPrint(string? planet)
    {
        if (planet is null)
            return null;
        if (!TerrainPrints.TryGetValue(planet, out var print))
            TerrainPrints[planet] = print = new ClassicAssets(Paths.Root).PlanetHeights(planet) is { } heights
                ? Machines.Sim.Navigation.TerrainHeights.FromExport(heights).Fingerprint()
                : null;
        return print;
    }

    /// <summary>
    /// For automated checks: MACHINES_MP=host,&lt;address&gt; opens a room on the map last picked (or
    /// MACHINES_MENU_SELECT's), readies and starts once a second player is ready; MACHINES_MP=serve,&lt;address&gt;
    /// does the same on a relay it hosts itself; MACHINES_MP=join,&lt;address&gt; joins the first room and readies.
    /// Each says a line in the room's chat first; MACHINES_MP_SHOT=&lt;png&gt; saves the room once both are ready, and
    /// MACHINES_MP_LOBBY_SHOT=&lt;png&gt; the list of rooms the joining side sees. The host or server opens a battle room
    /// instead with battle=&lt;map&gt;[,tier=1-8][,lives=1-5][,win=both|wipe|capture] after the address (for example
    /// MACHINES_MP=serve,127.0.0.1:7777,battle=m_desert,tier=2): both bring the hero of their own pod, the two other
    /// seats are bots. The joining side with pod=1 goes to the pod from the room and comes back by its Ready
    /// (MACHINES_MP_POD_SHOT=&lt;png&gt; saves the pod on the way). It all happens once a run: the page coming again (back
    /// from the pod or a match) does not connect anew.
    /// </summary>
    private void Automate()
    {
        if (_automated || System.Environment.GetEnvironmentVariable("MACHINES_MP") is not { Length: > 0 } setting)
            return;
        _automated = true;
        var parts = setting.Split(',');
        var serve = parts[0] == "serve";
        var host = serve || parts[0] == "host";
        _relay.Text = _address = parts.Length > 1 ? parts[1] : "127.0.0.1:7777";
        var asked = parts.Skip(2).Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
        int Number(string key, int otherwise) => asked.TryGetValue(key, out var text) && int.TryParse(text, out var n) ? n : otherwise;
        // Made once connected, so that the room has the player's name.
        CreateRoom? BattleRoom() => asked.TryGetValue("battle", out var battleMap)
            ? SkirmishPage.BattleRoom(battleMap, Number("tier", 1), Number("lives", 1),
                asked.TryGetValue("win", out var win) && Enum.TryParse<BattleWin>(win, ignoreCase: true, out var wins) ? wins : BattleWin.Both)
            : null;
        _name.Text = host ? "Host" : "Guest";
        // Automated checks leave the router alone.
        _openPort = false;
        _ = Run();

        async Task Run()
        {
            await (serve ? Host() : Connect());
            if (!NetLink.Connected)
                return;
            if (host)
            {
                if ((BattleRoom() ?? SkirmishPage.LastRoom()) is { } room)
                    NetLink.Send(room);
                while (_state is not { Players.Count: >= 2 })
                    await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
                Type("Welcome! Start when you are ready.");
                while (_state is not { Players.Count: >= 2 } || _state.Players.Any(p => p.ClientId != NetLink.ClientId && !p.Ready))
                    await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
                await Shoot();
                _ready.ButtonPressed = true;
                while (_state is null || _state.Players.Any(p => !p.Ready))
                    await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
                NetLink.Send(new StartMatch());
            }
            else
            {
                while (_roomList is not { Rooms.Count: > 0 })
                {
                    NetLink.Send(new ListRooms());
                    await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
                }
                if (System.Environment.GetEnvironmentVariable("MACHINES_MP_LOBBY_SHOT") is { Length: > 0 } lobbyShot)
                {
                    _rooms.Select(0);
                    await ToSignal(GetTree().CreateTimer(1), SceneTreeTimer.SignalName.Timeout);
                    GetViewport().GetTexture().GetImage().SavePng(lobbyShot);
                }
                NetLink.Send(new JoinRoom(_roomList.Rooms[0].Id));
                while (_state is null)
                    await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
                Type("Hello from the guest, all set here.");
                _colour.Selected = 1;
                if (asked.ContainsKey("pod"))
                {
                    await ThroughThePod();
                    return;
                }
                _ready.ButtonPressed = true;
                await Shoot();
            }
        }

        // To the pod and back by its Ready, as a player would; this page is gone meanwhile, the tree stays.
        async Task ThroughThePod()
        {
            var tree = GetTree();
            OpenPod();
            await tree.ToSignal(tree.CreateTimer(3), SceneTreeTimer.SignalName.Timeout);
            if (System.Environment.GetEnvironmentVariable("MACHINES_MP_POD_SHOT") is { Length: > 0 } podShot)
                tree.Root.GetViewport().GetTexture().GetImage().SavePng(podShot);
            var ready = Lang.T("Ready").ToUpperInvariant();
            var button = Buttons(tree.Root).FirstOrDefault(b => b.Text == ready && b.IsVisibleInTree());
            GD.Print(button is null ? "mp: no Ready in the pod" : "mp: Ready in the pod");
            button?.EmitSignal(BaseButton.SignalName.Pressed);
        }

        static IEnumerable<ChipButton> Buttons(Node node) => node.GetChildren().SelectMany(child =>
            child is ChipButton button ? [button] : Buttons(child));

        void Type(string text)
        {
            _say.Text = text;
            SendChat();
        }

        async Task Shoot()
        {
            if (System.Environment.GetEnvironmentVariable("MACHINES_MP_SHOT") is not { Length: > 0 } shot)
                return;
            await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
            GetViewport().GetTexture().GetImage().SavePng(shot);
        }
    }
}
