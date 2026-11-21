using Machines.Import.Model;
using Machines.Net;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// The match the menu asked for; the match scene reads it when it starts. A multiplayer match brings the relay's
/// <see cref="MatchInfo"/> (seed and players included); a replay brings its recording.
/// </summary>
public sealed record MatchRequest(string Scenario, string Planet, StartPositions Starts, MatchOptions Options)
{
    private readonly PlayerId? _me;
    private readonly PlayerTable? _players;

    public static MatchRequest Current { get; set; } = FromEnvironment() ?? Default;

    /// <summary>The world's random seed; a new one each time when not given.</summary>
    public int? Seed { get; init; }

    /// <summary>A multiplayer match started by the relay.</summary>
    public MatchInfo? Network { get; init; }

    /// <summary>A recorded match to watch.</summary>
    public Replay? Replay { get; init; }

    /// <summary>Rejoining a running match: the tick this player plays again from (the world catches up to it first).</summary>
    public long? ResumeFrom { get; init; }

    /// <summary>
    /// Online, why the relay rebuilds this player's world from its log (P2 design §5.4): a rejoin, a resync after going out of
    /// step, or a late join into a bot's seat; null for a match started as usual.
    /// </summary>
    public ResumeReason? Rebuild { get; init; }

    /// <summary>Online, how often this player's world was resynced in the match so far (the relay takes them out after three).</summary>
    public int Resyncs { get; init; }

    /// <summary>A saved game to carry on: its recording brings the world back to where it was saved.</summary>
    public Replay? Saved { get; init; }

    /// <summary>
    /// The player at this computer; when not given, the first player of the race <see cref="Starts"/> plays
    /// (<see cref="StartPositions.PlayerSlot"/>), as every match one person a race is played.
    /// </summary>
    public PlayerId Me { get => _me ?? PlayerId.Of(Starts.PlayerSlot); init => _me = value; }

    /// <summary>
    /// The players of the match, which its world is built from (in the rows' order); when not given, one a race of
    /// <see cref="Starts"/> (<see cref="PlayerTable.OnePerRace"/>), as every match one person a race is played.
    /// </summary>
    public PlayerTable Players { get => _players ?? PlayerTable.OnePerRace(Starts.Players, Options.IsBattle); init => _players = value; }

    /// <summary>The M1 match: Red against Blue on m_desert.</summary>
    public static MatchRequest Default => new("m_desert", "m_desert",
        StartPositions.Assign([Race.Red, Race.Blue], 2, Race.Red, random: false, new Random(0)), new MatchOptions());

    /// <summary>
    /// A relay's match, from the point of view of the local client: the player <paramref name="me"/> names (a rejoin), else
    /// the row of <paramref name="clientId"/>, else the first person (replays and saved games), else the first row (only
    /// computers played). Its players are the table the match records; the start positions are per race, each race
    /// played and coloured as its first row says.
    /// </summary>
    public static MatchRequest FromMatch(MatchInfo match, int clientId, PlayerId? me = null)
    {
        var table = match.Table();
        var rows = table.Rows;
        var index = me is { } named ? rows.ToList().FindIndex(r => r.Id == named) : -1;
        if (index < 0)
            index = match.Players.ToList().FindIndex(p => !p.Ai && p.ClientId == clientId);
        if (index < 0)
            index = match.Players.ToList().FindIndex(p => !p.Ai);
        index = Math.Max(0, index);
        var firsts = match.Players.GroupBy(p => p.Slot).Select(race => race.First()).ToList();
        var starts = new StartPositions(rows[index].Race,
            firsts.ToDictionary(p => p.Slot, p => p.Ai ? Controller.Ai : Controller.Player),
            firsts.ToDictionary(p => p.Slot, p => p.Colour));
        return new MatchRequest(match.Scenario, match.Planet, starts, match.Options) { Seed = match.Seed, Players = table, Me = rows[index].Id };
    }

    /// <summary>
    /// This match as the relay would describe it, for recording a replay or saving a single-player game, with the
    /// ground it is played on (<paramref name="terrain"/>, its fingerprint): a row for every player of <see cref="Players"/>
    /// in its order, each naming its player; the person at this computer under their name.
    /// </summary>
    public MatchInfo ToMatchInfo(int seed, int inputDelay, long? terrain) => Network ?? Saved?.Match ?? Replay?.Match
        ?? new MatchInfo(Scenario, Planet, Options, seed, inputDelay,
            Players.Rows.Select(row => new MatchPlayer(0,
                row.Kind == PlayerKind.Human && (row.Id == Me || _players is null) ? NetLink.Name : row.Name, row.Race,
                Starts.Colours.GetValueOrDefault(row.Race, row.Race), row.Kind != PlayerKind.Human, row.Id)).ToList(), terrain);

    /// <summary>
    /// A battle against the computer on a map: <paramref name="me"/> plays their seat (<see cref="BattleSeats.Of"/>; with
    /// <paramref name="watch"/> a bot plays it as well), every other seat is the computer's bot, and each race wears its own
    /// colour. <paramref name="options"/> lists every hero (<see cref="BattleDraft.Compose"/>).
    /// </summary>
    public static MatchRequest ForBattle(BattleMap map, BattleOptions options, PlayerId me, bool watch)
    {
        var seats = BattleSeats.Of(options);
        var rows = seats.Select(seat => new PlayerSeat(seat.Player, seat.Race, seat.Player == me && !watch ? PlayerKind.Human : PlayerKind.Bot,
            seat.Player == me ? NetLink.OfflineName
            : options.Heroes.FirstOrDefault(h => (h.Player ?? PlayerId.Of(h.Race)) == seat.Player)?.Name ?? "AI")).ToList();
        var races = rows.Select(r => r.Race).Distinct().Order().ToList();
        var mine = seats.FirstOrDefault(s => s.Player == me)?.Race ?? races[0];
        var starts = new StartPositions(mine,
            races.ToDictionary(r => r, r => rows.Any(row => row.Race == r && row.Kind == PlayerKind.Human) ? Controller.Player : Controller.Ai),
            races.ToDictionary(r => r, r => r));
        // The map is known ground in a battle, as in Wired.
        return new MatchRequest(map.Scenario, map.Planet, starts, new MatchOptions { Battle = options, FogOfWar = false })
        {
            Me = me,
            Players = new PlayerTable(rows),
        };
    }

    /// <summary>
    /// For automated checks: MACHINES_BATTLE=&lt;map&gt;[,tier=&lt;1-8&gt;][,lives=&lt;1-5&gt;][,win=both|wipe|capture][,bots=4][,team=&lt;n&gt;]
    /// starts a battle straight away, the player on the first team's anchor slot with a bot's loadout at the tier (not the
    /// pod's hero, so that a run does not depend on the profile); bots=4 lets a bot play the player's hero too, to watch;
    /// team=n seats n heroes a team (n against n, up to <see cref="BattleBalance.MostTeamSize"/>, fifteen, which the menus do
    /// not offer), the player the first of team A.
    /// </summary>
    private static MatchRequest? BattleFromEnvironment(string setting)
    {
        var parts = setting.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || BattleCatalogue.Map(parts[0]) is not { } map)
            throw new ArgumentException($"MACHINES_BATTLE: no battle map '{(parts.Length > 0 ? parts[0] : "")}'; the maps are {string.Join(", ", BattleCatalogue.Maps.Select(m => m.Scenario))}");
        var values = parts.Skip(1).Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].ToLowerInvariant(), p => p[1]);
        int Number(string key, int fallback) => values.TryGetValue(key, out var text) && int.TryParse(text, out var value) ? value : fallback;
        var tier = Math.Clamp(Number("tier", 3), 1, BattleCatalogue.MostTier);
        var win = values.TryGetValue("win", out var won) && Enum.TryParse<BattleWin>(won, true, out var parsed) ? parsed : BattleWin.Both;
        var watch = Number("bots", 3) >= 4;
        var team = Math.Clamp(Number("team", 0), 0, BattleBalance.MostTeamSize);
        var player = map.TeamA[0];
        var hero = watch ? null : BattleBots.Bot(map.Scenario, tier, player, 0) with { Name = BattleDraft.ShortName(NetLink.OfflineName), Bot = false };
        var options = BattleDraft.Compose(map, tier, Number("lives", 1), win, hero);
        // n against n: the player's hero takes the first seat of team A, bots every other.
        if (team > 0)
            options = BattleBots.Compose(options with { TeamSize = team, Heroes = [] }, hero is null ? [] : [hero with { Player = PlayerId.Of(player) }], map.Scenario);
        return ForBattle(map, options, PlayerId.Of(player), watch);
    }

    /// <summary>
    /// For automated checks: MACHINES_MAP=&lt;scenario&gt;,&lt;planet&gt; starts that map straight away, with an AI
    /// in every other start position. A recording or saved game that does not load (MACHINES_REPLAY, MACHINES_SAVED) is
    /// told on the console and the default match starts instead.
    /// </summary>
    private static MatchRequest? FromEnvironment()
    {
        try
        {
            return WithTeammate(Requested());
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or IOException)
        {
            Godot.GD.PrintErr($"Cannot load: {exception.Message}");
            return null;
        }
    }

    private static MatchRequest? Requested()
    {
        // MACHINES_REPLAY=<file> watches a recorded match.
        if (System.Environment.GetEnvironmentVariable("MACHINES_REPLAY") is { Length: > 0 } recording)
        {
            var replay = Replay.Load(recording);
            return FromMatch(replay.Match, clientId: -1) with { Replay = replay };
        }
        // MACHINES_SAVED=<file> goes on with a saved game, played by its first person.
        if (System.Environment.GetEnvironmentVariable("MACHINES_SAVED") is { Length: > 0 } file)
        {
            var saved = Replay.Load(file);
            return FromMatch(saved.Match, clientId: -1) with { Saved = saved };
        }
        // MACHINES_CAMPAIGN=<mission> plays a campaign mission (tut01, commsat ...) for the campaign's current player.
        if (System.Environment.GetEnvironmentVariable("MACHINES_CAMPAIGN") is { Length: > 0 } mission && Campaign.Entry(mission) is { } entry)
            return Campaign.Request(entry, CampaignProgress.Load().CurrentPlayer ?? new CampaignPlayer());
        // MACHINES_BATTLE=<map>[,...] starts a battle (see BattleFromEnvironment).
        if (System.Environment.GetEnvironmentVariable("MACHINES_BATTLE") is { Length: > 0 } battle)
            return BattleFromEnvironment(battle);
        var map = System.Environment.GetEnvironmentVariable("MACHINES_MAP");
        if (string.IsNullOrEmpty(map))
            return null;
        var parts = map.Split(',');
        var scenario = parts[0];
        var planet = parts.Length > 1 ? parts[1] : scenario;
        var slots = Paths.Data.LoadScenario(scenario).Races.Select(r => r.Race).ToList();
        var starts = StartPositions.Assign(slots, slots.Count, slots.Min(), random: false, new Random(0));
        // MACHINES_START=Blue,Red: the player takes start position Blue in red, as random starts can deal it; the
        // computer races take the colours left in order, a free one of their own first (StartPositions.Assign).
        if (System.Environment.GetEnvironmentVariable("MACHINES_START")?.Split(',') is [var slotName, var colourName]
            && Enum.TryParse<Race>(slotName, true, out var slot) && slots.Contains(slot) && Enum.TryParse<Race>(colourName, true, out var colour))
        {
            var colours = new Dictionary<Race, Race> { [slot] = colour };
            var unused = Enum.GetValues<Race>().Where(c => c != colour).ToList();
            foreach (var other in slots.Where(r => r != slot).OrderBy(r => r))
            {
                var look = unused.Contains(other) ? other : unused[0];
                colours[other] = look;
                unused.Remove(look);
            }
            starts = new StartPositions(slot, slots.ToDictionary(r => r, r => r == slot ? Controller.Player : Controller.Ai), colours);
        }
        return new MatchRequest(scenario, planet, starts,
            new MatchOptions
            {
                Resources = ResourceLevel.High,
                // MACHINES_AI=easy, normal or hard sets how well the AIs play.
                AiLevel = Enum.TryParse<AiLevel>(System.Environment.GetEnvironmentVariable("MACHINES_AI"), true, out var level) ? level : AiLevel.Normal,
                // MACHINES_WIRED=1 makes it a run of Wired, the remake's own survival mode.
                Wired = System.Environment.GetEnvironmentVariable("MACHINES_WIRED") == "1" ? new WiredOptions { Arena = scenario } : null,
            });
    }

    /// <summary>
    /// For screenshots of two commanders sharing a race: MACHINES_COOP=1 gives the skirmish asked for (MACHINES_MAP, or the
    /// default match) a second commander of the player's race, "Teammate", who is away from the first tick on (nobody plays
    /// them on this computer), so the player keeps their machines and orders them too. Not for battles, Wired runs,
    /// recordings and saved games.
    /// </summary>
    private static MatchRequest? WithTeammate(MatchRequest? request)
    {
        if (System.Environment.GetEnvironmentVariable("MACHINES_COOP") != "1")
            return request;
        var asked = request ?? Default;
        if (asked.Options.IsBattle || asked.Options.Wired is not null || asked.Replay is not null || asked.Saved is not null || asked.Network is not null)
            return request;
        var me = asked.Players[asked.Me];
        var rows = asked.Players.Rows.ToList();
        rows.Insert(rows.IndexOf(me) + 1, me with { Name = "Teammate" });
        var ids = PlayerTable.Number([.. rows.Select(r => r.Race)]);
        return asked with { Players = new PlayerTable(rows.Select((row, i) => row with { Id = ids[i] })), Me = asked.Me };
    }

    /// <summary>The people of a match played on this computer alone who are not at it: away from its first tick (MACHINES_COOP's teammate).</summary>
    public IEnumerable<PlayerId> AwayFromStart => Network is null && Replay is null && Saved is null
        ? Players.Humans.Where(id => id != Me)
        : [];
}
