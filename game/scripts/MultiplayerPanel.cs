using Godot;
using Machines.Import.Model;
using Machines.Net;

namespace Machines.Game;

/// <summary>
/// The multiplayer lobby, over the skirmish menu: connect to a relay, open a room on the map selected in the menu
/// or join one, pick a colour, get ready, and the host starts. The relay then hands every player the same match.
/// </summary>
public partial class MultiplayerPanel : PanelContainer
{
    private static readonly Race[] Colours = [Race.Red, Race.Blue, Race.Green, Race.Yellow];

    /// <summary>The room to open: the map and settings currently chosen in the skirmish menu.</summary>
    public required Func<CreateRoom?> RoomFromMenu { get; init; }

    private LineEdit _name = null!, _address = null!;
    private Label _status = null!, _roomTitle = null!;
    private ItemList _rooms = null!, _players = null!;
    private OptionButton _colour = null!;
    private CheckButton _ready = null!, _fillWithAi = null!;
    private Button _connect = null!, _start = null!;
    private VBoxContainer _lobby = null!, _room = null!;
    private RoomList? _roomList;
    private RoomState? _state;
    private bool _busy;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(560, 520);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
        AddChild(column);

        var heading = new Label { Text = "Multiplayer" };
        heading.AddThemeFontSizeOverride("font_size", 22);
        column.AddChild(heading);

        var login = new HBoxContainer();
        _name = new LineEdit { Text = NetLink.Name, PlaceholderText = "Your name", CustomMinimumSize = new Vector2(150, 0) };
        _address = new LineEdit { Text = "127.0.0.1:7777", PlaceholderText = "relay address", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _connect = new Button { Text = "Connect" };
        _connect.Pressed += () => _ = Connect();
        var hostButton = new Button { Text = "Host" };
        hostButton.Pressed += () => _ = Host();
        login.AddChild(_name);
        login.AddChild(_address);
        login.AddChild(_connect);
        login.AddChild(hostButton);
        column.AddChild(login);

        _status = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Text = "Host runs the relay in this game (port 7777); the others connect to your computer's address. " +
                   "Or connect to a relay someone else runs.",
        };
        column.AddChild(_status);

        _lobby = new VBoxContainer { Visible = false, SizeFlagsVertical = SizeFlags.ExpandFill };
        _rooms = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill };
        var lobbyButtons = new HBoxContainer();
        var refresh = new Button { Text = "Refresh" };
        refresh.Pressed += () => NetLink.Send(new ListRooms());
        var join = new Button { Text = "Join", TooltipText = "Join a room, or rejoin a match you dropped out of" };
        join.Pressed += Join;
        _fillWithAi = new CheckButton { Text = "Free slots play the AI", ButtonPressed = true };
        var create = new Button { Text = "Open a room on the selected map" };
        create.Pressed += Create;
        lobbyButtons.AddChild(refresh);
        lobbyButtons.AddChild(join);
        lobbyButtons.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        lobbyButtons.AddChild(_fillWithAi);
        lobbyButtons.AddChild(create);
        _lobby.AddChild(new Label { Text = "Rooms" });
        _lobby.AddChild(_rooms);
        _lobby.AddChild(lobbyButtons);
        column.AddChild(_lobby);

        _room = new VBoxContainer { Visible = false, SizeFlagsVertical = SizeFlags.ExpandFill };
        _roomTitle = new Label();
        _players = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill };
        var roomButtons = new HBoxContainer();
        _colour = new OptionButton();
        foreach (var colour in Colours)
            _colour.AddItem(colour.ToString());
        _colour.ItemSelected += _ => SendPlayer();
        _ready = new CheckButton { Text = "Ready" };
        _ready.Toggled += _ => SendPlayer();
        _start = new Button { Text = "Start match" };
        _start.Pressed += () => NetLink.Send(new StartMatch());
        var leave = new Button { Text = "Leave room" };
        leave.Pressed += () =>
        {
            NetLink.Send(new LeaveRoom());
            ShowLobby();
        };
        roomButtons.AddChild(new Label { Text = "Colour" });
        roomButtons.AddChild(_colour);
        roomButtons.AddChild(_ready);
        roomButtons.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        roomButtons.AddChild(leave);
        roomButtons.AddChild(_start);
        _room.AddChild(_roomTitle);
        _room.AddChild(_players);
        _room.AddChild(roomButtons);
        column.AddChild(_room);

        var close = new Button { Text = "Close" };
        close.Pressed += () => Visible = false;
        column.AddChild(close);

        if (NetLink.Connected)
            ShowLobby();
        Automate();
    }

    public override void _Process(double delta)
    {
        if (NetLink.Connection is not { } connection)
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
                    break;
                case RoomState state:
                    ShowRoom(state);
                    break;
                case Refused refused:
                    _status.Text = refused.Reason;
                    break;
                case MatchStarted started:
                    MatchRequest.Current = MatchRequest.FromMatch(started.Match, NetLink.ClientId) with { Network = started.Match };
                    GetTree().ChangeSceneToFile("res://scenes/Main.tscn");
                    return;
                case MatchResumed resumed:
                    // The match so far follows in the inbox; the match scene reads it from there.
                    MatchRequest.Current = MatchRequest.FromMatch(resumed.Match, NetLink.ClientId, resumed.Slot)
                        with { Network = resumed.Match, ResumeFrom = resumed.FromTick };
                    GetTree().ChangeSceneToFile("res://scenes/Main.tscn");
                    return;
                case Chat chat:
                    _status.Text = $"{chat.From}: {chat.Text}";
                    MenuSounds.Chat();
                    break;
            }
        }
        if (connection.Closed)
        {
            _status.Text = "Disconnected from the relay.";
            _lobby.Visible = _room.Visible = false;
            _ = NetLink.DisconnectAsync();
        }
    }

    private async Task Connect()
    {
        if (_busy)
            return;
        _busy = true;
        _status.Text = "Connecting…";
        var error = await NetLink.ConnectAsync(_address.Text, string.IsNullOrWhiteSpace(_name.Text) ? "Player" : _name.Text);
        _busy = false;
        if (error is not null)
        {
            _status.Text = error;
            return;
        }
        _status.Text = $"Connected as {NetLink.Name}.";
        ShowLobby();
    }

    private async Task Host()
    {
        if (_busy)
            return;
        _busy = true;
        _status.Text = "Starting the relay…";
        var port = _address.Text.Split(':') is [_, var p] && int.TryParse(p, out var parsed) ? parsed : 7777;
        var error = await NetLink.HostAsync(port, string.IsNullOrWhiteSpace(_name.Text) ? "Player" : _name.Text);
        _busy = false;
        if (error is not null)
        {
            _status.Text = error;
            return;
        }
        var addresses = System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
            .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a))
            .Select(a => $"{a}:{NetLink.HostedRelay!.Port}");
        _status.Text = $"Hosting. The others connect to {string.Join(" or ", addresses)} " +
                       "(over the internet: your public address, with the port forwarded).";
        ShowLobby();
    }

    private void ShowLobby()
    {
        _state = null;
        _lobby.Visible = true;
        _room.Visible = false;
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
        _lobby.Visible = false;
        _room.Visible = true;
        _roomTitle.Text = $"{state.Name}   ·   {state.Scenario} on {state.Planet}   ·   {state.Players.Count}/{state.Slots.Count} players" +
                          (state.FillWithAi ? ", the rest AI" : "");
        _players.Clear();
        foreach (var player in state.Players)
            _players.AddItem($"{player.Name}   {player.Colour}{(player.ClientId == state.HostId ? "   (host)" : "")}{(player.Ready ? "   ready" : "")}");
        _colour.SetBlockSignals(true);
        _colour.Selected = Array.IndexOf(Colours, me.Colour);
        _colour.SetBlockSignals(false);
        _ready.SetBlockSignals(true);
        _ready.ButtonPressed = me.Ready;
        _ready.SetBlockSignals(false);
        _start.Visible = state.HostId == NetLink.ClientId;
        _start.Disabled = state.Players.Any(p => !p.Ready);
    }

    private void Create()
    {
        if (RoomFromMenu() is not { } room)
        {
            _status.Text = "Pick a map in the skirmish menu first.";
            return;
        }
        NetLink.Send(room with { Name = $"{NetLink.Name}'s game", FillWithAi = _fillWithAi.ButtonPressed });
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

    private void SendPlayer() => NetLink.Send(new SetPlayer(Colours[_colour.Selected], _ready.ButtonPressed));

    /// <summary>
    /// For automated checks: MACHINES_MP=host,&lt;address&gt; opens a room on the selected map, readies and starts once
    /// a second player is ready; MACHINES_MP=serve,&lt;address&gt; does the same on a relay it hosts itself;
    /// MACHINES_MP=join,&lt;address&gt; joins the first room and readies.
    /// </summary>
    private void Automate()
    {
        if (System.Environment.GetEnvironmentVariable("MACHINES_MP") is not { Length: > 0 } setting)
            return;
        var parts = setting.Split(',');
        var serve = parts[0] == "serve";
        var host = serve || parts[0] == "host";
        _address.Text = parts.Length > 1 ? parts[1] : "127.0.0.1:7777";
        _name.Text = host ? "Host" : "Guest";
        _ = Run();

        async Task Run()
        {
            await (serve ? Host() : Connect());
            if (!NetLink.Connected)
                return;
            if (host)
            {
                Create();
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
