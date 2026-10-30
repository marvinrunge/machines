using Godot;
using Machines.Import.Model;
using Machines.Net;

namespace Machines.Game;

/// <summary>
/// The multiplayer page: connect to a relay or host one, then open a room (set up on the skirmish page) or join one,
/// pick a colour, get ready, and the host starts. The relay then hands every player the same match.
/// </summary>
public partial class MultiplayerPage(MenuScreens screens) : MenuPage(screens.ShowMain)
{
    private static readonly Race[] Colours = [Race.Red, Race.Blue, Race.Green, Race.Yellow];

    // Kept while the game runs, so the page comes back as it was left.
    private static string _address = "127.0.0.1:7777";
    private static string _statusText = "Host runs the relay in this game (port 7777); the others connect to your computer's address. " +
                                        "Or connect to a relay someone else runs.";

    private LineEdit _name = null!, _relay = null!;
    private Label _status = null!, _roomTitle = null!;
    private ItemList _rooms = null!, _players = null!;
    private ChipChoice _colour = null!;
    private CheckButton _ready = null!;
    private ChipButton _connect = null!, _host = null!, _start = null!, _join = null!;
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
        _status = MenuTheme.Paragraph(_statusText, 21, MenuTheme.Dim);
        login.AddChild(Column(14,
            MenuTheme.Label("YOUR NAME", 20, MenuTheme.Dim), _name,
            MenuTheme.Label("RELAY", 20, MenuTheme.Dim), _relay,
            Line(16, _connect, _host),
            Spring(across: false),
            _status));
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
        _players = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.None };
        _colour = new ChipChoice(Colours.Select(c => c.ToString())) { CustomMinimumSize = new Vector2(220, 0) };
        _colour.ItemSelected += _ => SendPlayer();
        _ready = new CheckButton { Text = "Ready" };
        _ready.Toggled += _ => SendPlayer();
        _start = Button("Start match", () => NetLink.Send(new StartMatch()), 70, 26);
        var leave = Button("Leave room", () =>
        {
            NetLink.Send(new LeaveRoom());
            ShowLobby();
        }, 70, 26);
        _room = Column(16, _roomTitle, _players,
            Line(20, MenuTheme.Label("Colour", 22, MenuTheme.Text), _colour, _ready, Spring(), leave, _start));
        foreach (var view in new[] { _offline, _lobby, _room })
        {
            view.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            views.AddChild(view);
        }

        var back = Button("Back", Back);
        back.CustomMinimumSize = new Vector2(400, 76);
        page.AddChild(Line(Gap, back));

        if (NetLink.Connected)
            ShowLobby();
        else
            ShowOffline();
        SwitchOn();
        Focus(NetLink.Connected ? _rooms : _connect);
        Automate();
    }

    private void Say(string text) => _status.Text = _statusText = text;

    /// <summary>Leaves for the match the relay started; what follows in the inbox is the match scene's to read.</summary>
    private void Leave()
    {
        _leaving = true;
        LoadingScreen.Show(GetTree(), LoadingScreen.MapName(MatchRequest.Current), "MULTIPLAYER");
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
                        _rooms.AddItem($"{room.Name}   ·   {room.Scenario} on {room.Planet}   ·   {room.Players}/{room.MaxPlayers}{(room.Started ? "   ·   playing" : "")}");
                    _join.Disabled = list.Rooms.Count == 0;
                    break;
                case RoomState state:
                    ShowRoom(state);
                    break;
                case Refused refused:
                    Say(refused.Reason);
                    break;
                case MatchStarted started:
                    MatchRequest.Current = MatchRequest.FromMatch(started.Match, NetLink.ClientId) with { Network = started.Match };
                    Leave();
                    return;
                case MatchResumed resumed:
                    // The match so far follows in the inbox; the match scene reads it from there.
                    MatchRequest.Current = MatchRequest.FromMatch(resumed.Match, NetLink.ClientId, resumed.Slot)
                        with { Network = resumed.Match, ResumeFrom = resumed.FromTick };
                    Leave();
                    return;
                case Chat chat:
                    Say($"{chat.From}: {chat.Text}");
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
        Say($"Connected as {NetLink.Name}.");
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
        var addresses = System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
            .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a))
            .Select(a => $"{a}:{NetLink.HostedRelay!.Port}");
        Say($"Hosting. The others connect to {string.Join(" or ", addresses)} " +
            "(over the internet: your public address, with the port forwarded).");
        ShowLobby();
    }

    private void ShowOffline()
    {
        _state = null;
        (_offline.Visible, _lobby.Visible, _room.Visible) = (true, false, false);
        _connect.Disabled = _host.Disabled = false;
    }

    private void ShowLobby()
    {
        _state = null;
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
        _state = state;
        (_offline.Visible, _lobby.Visible, _room.Visible) = (false, false, true);
        _roomTitle.Text = $"{state.Name}   ·   {state.Scenario} on {state.Planet}   ·   {state.Players.Count}/{state.Slots.Count} players" +
                          (state.FillWithAi ? ", the rest AI" : "");
        _players.Clear();
        foreach (var player in state.Players)
            _players.AddItem($"{player.Name}   {player.Colour}{(player.ClientId == state.HostId ? "   (host)" : "")}{(player.Ready ? "   ready" : "")}");
        // The ground counts only when everyone has the same export of it (MatchInfo.Terrain).
        if (state.Players.Where(p => p.Ready).Select(p => p.Terrain).Distinct().Count() > 1)
            _roomTitle.Text += "\nThe players' classic terrain exports differ: hills will not stop shots in this match";
        _colour.Selected = Array.IndexOf(Colours, me.Colour);
        _ready.SetBlockSignals(true);
        _ready.ButtonPressed = me.Ready;
        _ready.SetBlockSignals(false);
        _start.Visible = state.HostId == NetLink.ClientId;
        _start.Disabled = state.Players.Any(p => !p.Ready);
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

    private void SendPlayer() => NetLink.Send(new SetPlayer(Colours[_colour.Selected], _ready.ButtonPressed, TerrainPrint(_state?.Planet)));

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
    /// </summary>
    private void Automate()
    {
        if (System.Environment.GetEnvironmentVariable("MACHINES_MP") is not { Length: > 0 } setting)
            return;
        var parts = setting.Split(',');
        var serve = parts[0] == "serve";
        var host = serve || parts[0] == "host";
        _relay.Text = parts.Length > 1 ? parts[1] : "127.0.0.1:7777";
        _name.Text = host ? "Host" : "Guest";
        _ = Run();

        async Task Run()
        {
            await (serve ? Host() : Connect());
            if (!NetLink.Connected)
                return;
            if (host)
            {
                if (SkirmishPage.LastRoom() is { } room)
                    NetLink.Send(room);
                while (_state is not { Players.Count: >= 2 } || _state.Players.Any(p => p.ClientId != NetLink.ClientId && !p.Ready))
                    await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
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
                NetLink.Send(new JoinRoom(_roomList.Rooms[0].Id));
                while (_state is null)
                    await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
                _colour.Selected = 1;
                _ready.ButtonPressed = true;
            }
        }
    }
}
