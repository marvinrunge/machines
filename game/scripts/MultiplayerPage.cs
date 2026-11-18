using Godot;
using Machines.Import.Model;
using Machines.Net;
using Machines.Sim.Survival;

namespace Machines.Game;

/// <summary>
/// The multiplayer page: connect to a relay or host one, then open a room (set up on the skirmish page) or join one,
/// pick a colour, get ready, and the host starts. The relay then hands every player the same match. The room lists its
/// seats and its settings; its host says what each free seat is for, takes players out and changes the settings
/// (MachGuiCtxImReady). The seats stand by race in a normal room, where two commanders may share a side, and by team in a
/// battle room, where a team may have five heroes; each shows its race's colour, and a free one can be taken. In a battle
/// room each player brings the hero last put together in their pod (Pod opens it and comes back here), the room shows every
/// player's chassis and tier, and after the battle the end screen's Room brings the players back into a room like it.
/// </summary>
public partial class MultiplayerPage(MenuScreens screens, Action? backTo = null) : MenuPage(backTo ?? screens.ShowMain)
{
    private static readonly Race[] Colours = [Race.Red, Race.Blue, Race.Green, Race.Yellow];

    // Kept while the game runs, so the page comes back as it was left; at first the public relay, if there is one.
    private static string? _address;

    // How long a web address may take before the page says the relay is waking up (Cloud Run starts it on the first call).
    private const double WakingAfter = 1.5;

    // Whether the line to the relay was last seen dropped, to say so once and once more when it is back.
    private bool _reconnecting;
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

    // The room of the match this game plays now: started from here, or come back into (rejoined or joined late).
    private static int? _playing;

    /// <summary>The relay's room of the match this game plays, which a world that lost its place asks to come back into.</summary>
    public static int? MatchRoomId => _playing;

    /// <summary>What the page says when it comes again: the match scene tells why it left the match (P2 design §5.5).</summary>
    public static void Tell(string text) => _statusText = text;

    // Coming back from the pod: the hero is sent again, ready or not as the pod's button said.
    private static bool? _fromPod;

    // The battle room (id, map and bracket) the pod's hero was last sent to by itself, so a refused one is not sent over and over.
    private static string? _heroSentFor;

    // MACHINES_MP's automated room is played once a run.
    private static bool _automated;

    // The seats' list scrolls once it is taller than this (fifteen heroes a team, P2), or than what the page has left once the
    // chat has a few lines; it shows at least a team's heading and a seat or two.
    private const float MostSeatsHeight = 420, LeastSeatsHeight = 110, LeastChatHeight = 80;

    // Where seats are many (a battle team's five or more one under another, or a side's two commanders) they are written and
    // chosen a size smaller than a room of one seat a side.
    private const int SeatFont = 15;

    private LineEdit _name = null!, _relay = null!, _say = null!;
    private Label _status = null!, _roomTitle = null!, _settings = null!;
    private RichTextLabel _chat = null!;
    private ItemList _rooms = null!;
    private VBoxContainer _seats = null!;
    private ScrollContainer _seatList = null!;
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
        _relay = new LineEdit
        {
            Text = _address ??= GameOptions.PublicRelay,
            PlaceholderText = "host:port, or wss://… for a relay online",
            ContextMenuEnabled = false,
        };
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
        _seats = Column(4);
        _seats.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _seatList = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _seatList.AddChild(_seats);
        // As high as its seats, up to what the page has room for.
        _seats.MinimumSizeChanged += () => Callable.From(FitSeats).CallDeferred();
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
        _room = Column(10, _roomTitle, _seatList, Line(16, _settings, _change), _chat, Line(16, _say, send),
            Line(20, MenuTheme.Label("Colour", 22, MenuTheme.Text), _colour, _ready, Spring(), _pod, leave, _start));
        _room.Resized += () => Callable.From(FitSeats).CallDeferred();
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
        // The relay names the player it found no seat for; the sentence is translated around the name.
        const string noPlayer = "no player called ", leftThat = " left that match";
        var text = reason.StartsWith(noPlayer, StringComparison.Ordinal) && reason.EndsWith(leftThat, StringComparison.Ordinal)
            ? HangarText.Reason(Lang.T(noPlayer + "{0}" + leftThat, reason[noPlayer.Length..^leftThat.Length]))
            : HangarText.Reason(reason);
        _status.Text = _statusText = text;
        if (_state is not null)
            AddChat(text, note: true);
    }

    /// <summary>Leaves for the match the relay started, or comes back into; what follows in the inbox is the match scene's to read.</summary>
    private void Leave(string detail = "MULTIPLAYER")
    {
        _leaving = true;
        LoadingScreen.Show(GetTree(), LoadingScreen.MapName(MatchRequest.Current), Lang.T(detail));
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
                    if (_rejoining is { } asking && TryNextWayBack(asking, refused.Reason))
                        break;
                    (_rejoining, _rejoinedByKey, _lateJoining) = (null, false, false);
                    Refuse(refused.Reason);
                    break;
                case MatchStarted started:
                    MatchRequest.Current = MatchRequest.FromMatch(started.Match, NetLink.ClientId) with { Network = started.Match };
                    (_matchRoom, _known, _playing, _rejoining) = (_state, null, _state?.Id, null);
                    ResyncCarry.Forget();
                    Leave();
                    return;
                // The key to take the seat back with comes right after the start, which the match scene reads; should it
                // still come here, it is kept here.
                case SeatKey key:
                    NetLink.KeepSeat(key.RoomId, key.Player, key.Key);
                    break;
                case MatchResumed resumed:
                    _known = null;
                    // A rejoin by name, or a late join, brings the seat's new key.
                    if (resumed.Key is { } fresh && _rejoining is { } asked)
                        NetLink.KeepSeat(asked, resumed.Player, fresh);
                    (_playing, _rejoining, _rejoinedByKey, _lateJoining) = (_rejoining ?? _playing, null, false, false);
                    // The match so far follows in the inbox; the match scene reads it from there, and plays it from where the
                    // relay rebuilds this world: the hero is the player's once they are back in the match (P2 design §5.4, §6.3).
                    MatchRequest.Current = MatchRequest.FromMatch(resumed.Match, NetLink.ClientId, resumed.Player)
                        with { Network = resumed.Match, ResumeFrom = resumed.FromTick, Rebuild = resumed.Reason, Resyncs = resumed.Resyncs };
                    // A game started anew or come back from the lobby has nothing in hand from before (ResyncCarry is the match scene's).
                    ResyncCarry.Forget();
                    GD.Print($"mp: {(resumed.Reason == ResumeReason.LateJoin ? "joining" : "back in")} room {_playing} as player {resumed.Player} from tick {resumed.FromTick}");
                    Leave(resumed.Reason == ResumeReason.LateJoin ? "JOINING A RUNNING BATTLE" : "REJOINING THE MATCH");
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
        else if (connection.Reconnecting != _reconnecting)
        {
            // A dropped line comes back by itself within a minute; meanwhile the page says so.
            _reconnecting = !_reconnecting;
            Say(_reconnecting ? "The line to the relay dropped; reconnecting…" : ConnectedText());
        }
    }

    private string PlayerName => string.IsNullOrWhiteSpace(_name.Text) ? "Player" : _name.Text.Trim();

    /// <summary>Who the player is on the relay, and the relay's version where it says it.</summary>
    private static string ConnectedText() => NetLink.RelayVersion is { } version
        ? Lang.T("Connected as {0} to relay {1}.", NetLink.Name, version)
        : Lang.T("Connected as {0}.", NetLink.Name);

    private async Task Connect()
    {
        if (_busy)
            return;
        var address = _relay.Text.Trim();
        if (address.Length == 0)
        {
            Say("Type the address of a relay first: host:port, or wss://… for a relay online.");
            return;
        }
        _busy = true;
        Say("Connecting…");
        var connecting = NetLink.ConnectAsync(address, PlayerName, keepName: !_automated);
        // A relay on Cloud Run sleeps between matches; the first call wakes it, which takes a few seconds.
        if (RelayAddress.IsWebSocket(address))
            GetTree().CreateTimer(WakingAfter).Timeout += () =>
            {
                if (!connecting.IsCompleted && IsInstanceValid(this))
                    Say("Server is waking up…");
            };
        var error = await connecting;
        _busy = false;
        if (!IsInstanceValid(this))
            return;
        if (error is not null)
        {
            Say(error);
            return;
        }
        Say(ConnectedText());
        ShowLobby();
    }

    private async Task Host()
    {
        if (_busy)
            return;
        _busy = true;
        Say("Starting the relay…");
        var port = _relay.Text.Split(':') is [_, var p] && int.TryParse(p, out var parsed) ? parsed : 7777;
        var error = await NetLink.HostAsync(port, PlayerName, keepName: !_automated);
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
        _settings.Text = SkirmishPage.Summary(state.Options, state.SeatsPerSlot);
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

    /// <summary>
    /// The room's seats (<see cref="RoomSeating"/>), each with its race's colour: in a normal room a line for each race, its
    /// seats side by side (two where commanders share a side); in a battle room a column for each team, its seats one under
    /// another (up to ten a team in the menus, fifteen for matchmaking, the list scrolling past its room). A seat says who sits
    /// there or what it is for, numbered where a race has several; a free one can be taken, and the host says what each
    /// seat not theirs is for.
    /// </summary>
    private void ShowSeats(RoomState state)
    {
        foreach (var row in _seats.GetChildren())
        {
            _seats.RemoveChild(row);
            row.QueueFree();
        }
        var numbered = RoomSeating.Numbered(state);
        if (state.Options.Battle is not null)
        {
            var teams = Line(28);
            foreach (var group in RoomSeating.Groups(state))
            {
                var column = Column(2, TeamHeading(state, group));
                column.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                foreach (var line in group.Seats)
                    column.AddChild(SeatRow(state, line, numbered, battle: true));
                teams.AddChild(column);
            }
            _seats.AddChild(teams);
            return;
        }
        foreach (var group in RoomSeating.Groups(state))
        {
            var row = Line(18, new SeatChip { Colour = RaceColour(state, group.Races[0]) });
            foreach (var line in group.Seats)
                row.AddChild(SeatRow(state, line, numbered, battle: false));
            _seats.AddChild(row);
        }
    }

    /// <summary>
    /// The seats' list as high as its seats, but no higher than the room's column leaves once its other lines and a few lines
    /// of chat are in (a larger UI scale leaves less), and at most <see cref="MostSeatsHeight"/>; beyond that it scrolls. While
    /// the column runs over the page it is as high as what it holds, and the list gives up a few lines' height a time until it
    /// fits.
    /// </summary>
    private void FitSeats()
    {
        if (!IsInstanceValid(this) || !IsInstanceValid(_seatList) || !_room.Visible)
            return;
        var others = _room.GetCombinedMinimumSize().Y - _seatList.CustomMinimumSize.Y - _chat.GetCombinedMinimumSize().Y;
        var room = Math.Clamp(_room.Size.Y - others - LeastChatHeight, LeastSeatsHeight, MostSeatsHeight);
        var height = Math.Min(_seats.GetCombinedMinimumSize().Y, room);
        if (Math.Abs(_seatList.CustomMinimumSize.Y - height) > 0.5f)
            _seatList.CustomMinimumSize = new Vector2(0, height);
    }

    /// <summary>A battle team's heading over its column: its number and the colours its races show.</summary>
    private static Control TeamHeading(RoomState state, SeatGroup group)
    {
        var name = MenuTheme.Label(group.Team >= 0 ? Lang.T("Team {0}", group.Team + 1).ToUpperInvariant() : Lang.T(group.Races[0].ToString()), 20, MenuTheme.Bright);
        name.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        var heading = Line(10, name);
        foreach (var race in group.Races)
            heading.AddChild(new SeatChip { Colour = RaceColour(state, race) });
        var people = group.Seats.Count(s => s.Player is not null);
        var count = MenuTheme.Label(Lang.T("{0} of {1} seats taken", people, group.Seats.Count), 16, MenuTheme.Dim);
        count.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        count.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        heading.AddChild(count);
        return heading;
    }

    /// <summary>
    /// A seat in the room's list: in a battle its race's colour (a team of today's layout has two), its number where its race
    /// has several, who sits there or what it is for, in a battle the hero there; "Take this seat" where it is free, and the
    /// host's choice for every seat but their own. Where seats are many (a battle, or numbered ones) they are written a size
    /// smaller.
    /// </summary>
    private HBoxContainer SeatRow(RoomState state, SeatLine line, bool numbered, bool battle)
    {
        var compact = battle || numbered;
        var row = Line(10);
        row.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        if (battle)
            row.AddChild(new SeatChip { Colour = RaceColour(state, line.Race) });
        if (numbered)
        {
            var number = MenuTheme.Label(line.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), compact ? 18 : 22, MenuTheme.Bright);
            number.CustomMinimumSize = new Vector2(22, 0);
            row.AddChild(number);
        }
        var player = line.Player;
        var text = player is null ? Lang.T(line.Seat.Use.ToString())
            : $"{player.Name}{(player.ClientId == state.HostId ? "   " + Lang.T("(host)") : "")}{(player.Ready ? "   " + Lang.T("ready") : "")}";
        // A battle seat has a hero: the player's, or a bot's if nobody sits there ("bot" from ten a side, where a team's
        // column is at its least width).
        if (battle)
            text += "   ·   " + (player is null ? Lang.T(RoomSeating.BotSeat(state)) : HeroText(player.Hero));
        var label = MenuTheme.Label(text, compact ? 18 : 22, player is null ? MenuTheme.Dim : MenuTheme.Text);
        label.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        label.ClipText = true;
        label.TooltipText = text;
        row.AddChild(label);
        if (RoomSeating.MayTake(state, NetLink.ClientId, line.Index))
        {
            var take = Button("Take this seat", () => NetLink.Send(new TakeSeat(line.Index)), 40, compact ? SeatFont : 18);
            take.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            row.AddChild(take);
        }
        if (state.HostId == NetLink.ClientId && player?.ClientId != NetLink.ClientId)
            row.AddChild(SeatChoice(state, line, compact));
        return row;
    }

    /// <summary>The colour a race shows in the room: its players' (they share it); null while nobody sits at its seats.</summary>
    private static Color? RaceColour(RoomState state, Race race)
    {
        var sitting = state.Seats.Where(s => s.Race == race && s.ClientId is not null).Select(s => s.ClientId!.Value).ToHashSet();
        if (state.Players.FirstOrDefault(p => sitting.Contains(p.ClientId)) is not { } player)
            return null;
        foreach (var (colour, shown) in HangarCreation.Colours)
        {
            if (colour == player.Colour)
                return shown;
        }
        return null;
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
        // A running battle with bots' seats a player may join late says how many (P2 design §6.2).
        return line + (!room.Started ? ""
            : room.Joinable > 0 ? "   ·   " + Lang.T(room.Joinable == 1 ? "playing · a seat to join" : "playing · {0} seats to join", room.Joinable)
            : "   ·   " + Lang.T("playing"));
    }

    /// <summary>
    /// The host's choice for a seat (MachGuiPlayerListItem): what <see cref="RoomSeating.Uses"/> allows there (Open or
    /// Computer in a battle, which has a hero at every seat; Open, Closed and at a race's first seat Computer otherwise) and
    /// what the seat is for now (<see cref="RoomSeating.Choices"/>), and at a player's seat their name first; choosing
    /// anything else takes them out of the room, once the host has confirmed it. A size smaller where the seats are many
    /// (<paramref name="compact"/>).
    /// </summary>
    private ChipChoice SeatChoice(RoomState state, SeatLine line, bool compact)
    {
        var (seat, use, player) = (line.Index, line.Seat.Use, line.Player);
        var uses = RoomSeating.Choices(state, seat).ToArray();
        var names = uses.Select(u => u.ToString());
        var choice = new ChipChoice(player is null ? names : names.Prepend(player.Name), player is null ? Math.Max(0, Array.IndexOf(uses, use)) : 0)
        {
            CustomMinimumSize = new Vector2(compact ? 160 : 220, 0),
        };
        if (compact)
            choice.FontSize = SeatFont;
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
        var room = _roomList.Rooms[selected[0]];
        if (room.Started)
            Rejoin(room.Id);
        else
            NetLink.Send(new JoinRoom(room.Id));
    }

    // The running match this page last asked to come back into, and whether it asked with the seat's key or to join it late.
    private static int? _rejoining;
    private static bool _rejoinedByKey, _lateJoining;

    /// <summary>
    /// Back into a running match (P2 design §6.1): with the key of the seat this game holds there, kept in the settings, which
    /// takes the seat back from any connection, also one the relay still holds for this player; else by the name connected
    /// with, as the player they were when it is the match this game last played, so that of two who connected under one name
    /// each comes back as themself. Refused, the next way is tried (<see cref="TryNextWayBack"/>).
    /// </summary>
    private void Rejoin(int room)
    {
        (_rejoining, _lateJoining) = (room, false);
        if (NetLink.SeatIn(room) is { } seat)
        {
            _rejoinedByKey = true;
            GD.Print($"mp: rejoining room {room} by key as player {seat.Player}");
            NetLink.Send(new RejoinMatch(room, seat.Player, seat.Key));
            return;
        }
        RejoinByName(room);
    }

    /// <summary>
    /// A way back into a running match was refused (P2 design §6.1, §6.2): a seat key the relay does not take (the match it was
    /// for is over, or it took this player out) is forgotten and the player asks by name; nobody of that name having left a
    /// running battle with bots' seats free, the player joins it late in one, with this game's ground. False where no way is
    /// left, and the page says the refusal.
    /// </summary>
    private bool TryNextWayBack(int room, string reason)
    {
        if (_rejoinedByKey)
        {
            NetLink.ForgetSeat();
            _rejoinedByKey = false;
            if (reason == "that match is not running")
                return false;
            GD.Print($"mp: the seat key was refused ({reason}): rejoining room {room} by name");
            RejoinByName(room);
            return true;
        }
        if (!_lateJoining && reason.StartsWith("no player called ", StringComparison.Ordinal)
                          && _roomList?.Rooms.FirstOrDefault(r => r.Id == room) is { Mode: RoomSummary.Battle, Joinable: > 0 } battle)
        {
            _lateJoining = true;
            GD.Print($"mp: nobody of this name left room {room}: joining its battle late");
            NetLink.Send(new JoinRoom(room, Terrain: TerrainPrint(battle.Planet)));
            return true;
        }
        return false;
    }

    private void RejoinByName(int room)
    {
        _rejoinedByKey = false;
        Machines.Sim.World.PlayerId? was = _matchRoom?.Id == room && MatchRequest.Current.Network is { } played
                                                                     && _roomList?.Rooms.FirstOrDefault(r => r.Id == room)?.Scenario == played.Scenario
            ? MatchRequest.Current.Me
            : null;
        GD.Print($"mp: rejoining room {room} by name");
        NetLink.Send(new RejoinMatch(room, was));
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
            NetLink.Send(new CreateRoom(was.Name, was.Scenario, was.Planet, was.Slots, was.Options, was.FillWithAi, was.SeatsPerSlot));
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
                Say(ConnectedText());
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
    /// instead with battle=&lt;map&gt;[,tier=1-8][,lives=1-5][,win=both|wipe|capture][,team=5|10|15] after the address (for
    /// example MACHINES_MP=serve,127.0.0.1:7777,battle=m_desert,tier=2): both bring the hero of their own pod, the other
    /// seats are bots; team=n seats n heroes a team (15, which the menus do not offer, too). A normal room has two commanders
    /// a side with seats=2. The joining side with seat=&lt;n&gt; moves to the room's seat n (counted from 0) once in, keeping
    /// the colour it is given there (seat=1 in a room of two a side sits beside the host); with pod=1 it goes to the pod from
    /// the room and comes back by its Ready (MACHINES_MP_POD_SHOT=&lt;png&gt; saves the pod on the way). Where the first
    /// room's match is running already, the joining side asks to come back into it (RejoinMatch): by the key of its seat
    /// there when its settings keep one from an earlier run (a game killed in the match and started again), else as the
    /// player of its name who left, else, a battle with bots' seats free, late into one of them; with late=1 the joining side
    /// waits until the first room's match runs. name=&lt;x&gt; connects under that name (else Host or Guest), and players=&lt;n&gt;
    /// makes the host wait for n players, itself included, instead of two. It all happens once a run: the page coming again
    /// (back from the pod or a match) does not connect anew.
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
                asked.TryGetValue("win", out var win) && Enum.TryParse<BattleWin>(win, ignoreCase: true, out var wins) ? wins : BattleWin.Both,
                Number("team", 0))
            : null;
        _name.Text = asked.TryGetValue("name", out var name) && name.Length > 0 ? name : host ? "Host" : "Guest";
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
                if ((BattleRoom() ?? SkirmishPage.LastRoom(Number("seats", 1))) is { } room)
                    NetLink.Send(room);
                var players = Math.Max(2, Number("players", 2));
                while (_state is null || _state.Players.Count < players)
                    await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
                Type("Welcome! Start when you are ready.");
                while (_state is null || _state.Players.Count < players || _state.Players.Any(p => p.ClientId != NetLink.ClientId && !p.Ready))
                    await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
                await Shoot();
                _ready.ButtonPressed = true;
                while (_state is null || _state.Players.Any(p => !p.Ready))
                    await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
                NetLink.Send(new StartMatch());
            }
            else
            {
                // late=1: the first room is joined once its match runs, late into a bot's seat (P2 design §6.2).
                var late = Number("late", 0) > 0;
                while (_roomList is not { Rooms.Count: > 0 } || late && !_roomList.Rooms[0].Started)
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
                // A match already running there takes the joining side back: by the key of its seat there, kept in the
                // settings from the run before, else as the player of its name who left it; else, a battle, into a bot's seat.
                if (_roomList.Rooms[0] is { Started: true } running)
                {
                    Rejoin(running.Id);
                    return;
                }
                NetLink.Send(new JoinRoom(_roomList.Rooms[0].Id));
                while (_state is null)
                    await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
                Type("Hello from the guest, all set here.");
                if (Number("seat", -1) is >= 0 and var seat)
                {
                    // Beside a teammate, say: the seat's race's colour is the one to keep.
                    NetLink.Send(new TakeSeat(seat));
                    for (var waited = 0.0; _state?.Seats.ElementAtOrDefault(seat)?.ClientId != NetLink.ClientId && waited < 10; waited += 0.2)
                        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
                    GD.Print(_state?.Seats.ElementAtOrDefault(seat)?.ClientId == NetLink.ClientId ? $"mp: at seat {seat}" : $"mp: not at seat {seat}");
                }
                else
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

/// <summary>
/// A race's colour beside its seats in the room's list: a lamp in the colour its players show (teammates share one), a
/// ring while nobody sits at its seats and the colour is not settled.
/// </summary>
public partial class SeatChip : Control
{
    public Color? Colour { get; init; }

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(24, 24);
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Draw()
    {
        var middle = Size / 2;
        var radius = Math.Min(Size.X, Size.Y) / 2 - 2;
        if (Colour is not { } colour)
        {
            DrawArc(middle, radius - 1, 0, Mathf.Tau, 32, MenuTheme.Dim, 2, true);
            return;
        }
        DrawCircle(middle, radius, colour);
        DrawCircle(middle - new Vector2(radius * 0.3f, radius * 0.3f), radius * 0.28f, new Color(1, 1, 1, 0.45f));
        DrawArc(middle, radius, 0, Mathf.Tau, 32, colour.Darkened(0.4f), 1.5f, true);
    }
}
