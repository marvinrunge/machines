using Machines.Import.Model;
using Machines.Net;
using Machines.Sim;
using Machines.Sim.World;

namespace Machines.Tests.Net;

/// <summary>
/// Lockstep and recordings where a race has two players: each turn is its player's, a tick's comings and goings reach the
/// world at once and in the order they happened, and a world is built from the match's table.
/// </summary>
public class PlayerSessionTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    /// <summary>Anna and Cleo command Red together on Canyon Attack, Ben Blue; Green is the computer's.</summary>
    private static MatchInfo Together() => new("m_desert", "m_desert", new MatchOptions(), 7, 1,
    [
        new MatchPlayer(1, "Anna", Race.Red, Race.Red, Ai: false, new PlayerId(0)),
        new MatchPlayer(2, "Cleo", Race.Red, Race.Red, Ai: false, new PlayerId(4)),
        new MatchPlayer(3, "Ben", Race.Blue, Race.Blue, Ai: false, new PlayerId(1)),
        new MatchPlayer(0, "AI", Race.Green, Race.Green, Ai: true, new PlayerId(2)),
    ]);

    private static readonly PlayerId Anna = new(0), Ben = new(1), Cleo = new(4);

    /// <summary>Hands every person but the local one an empty turn for the tick ahead, as the relay would.</summary>
    private static void Others(LockstepSession session, params PlayerId[] players)
    {
        foreach (var player in players)
            session.Receive(new Turn(session.World.Tick, player, []));
    }

    [Fact]
    public void A_world_is_built_from_the_matchs_table_row_for_row()
    {
        var match = Together();
        var world = Replay.BuildWorld(DataLoader.Value, match);

        Assert.Equal(match.Table().Rows, world.Players.Rows);
        Assert.Equal([Anna, Ben, Cleo], world.Players.Humans);
        Assert.Equal(Race.Red, world.Players.RaceOf(Cleo));
        // A match without ids numbers its rows itself, as the relay would have.
        var unnumbered = match with { Players = [.. match.Players.Select(p => p with { Player = null })] };
        Assert.Equal(match.Table().Rows, unnumbered.Table().Rows);
    }

    [Fact]
    public void A_teammates_turn_is_carried_out_as_theirs_and_what_it_says_for_another_race_is_not()
    {
        var match = Together();
        var session = new LockstepSession(Replay.BuildWorld(DataLoader.Value, match), Anna, match.InputDelay);
        Assert.Equal([Anna, Ben, Cleo], session.Humans);
        session.Pump();
        Assert.True(session.TryStep());

        Assert.Equal([Ben, Cleo], session.Waiting());
        var red = session.World.Machines.First(m => m.Race == Race.Red).Id;
        var blue = session.World.Machines.First(m => m.Race == Race.Blue).Id;
        session.Receive(new Turn(1, Cleo, [new FollowCommand(Race.Red, [red], Target: 999_999), new FollowCommand(Race.Blue, [blue], Target: 999_999)]));
        // Nobody plays player 9: what comes in their name is passed over.
        session.Receive(new Turn(1, new PlayerId(9), [new FollowCommand(Race.Red, [red], Target: 999_998)]));
        Others(session, Ben);
        session.Pump();
        Assert.True(session.TryStep());

        var refused = Assert.Single(session.World.Rejected, r => r.Command is FollowCommand);
        Assert.Equal(("nothing to follow there", Race.Red, Cleo), (refused.Reason, refused.Command.Race, refused.Command.Issuer));
        Assert.Equal(Cleo, Assert.Single(session.Played).Player);
    }

    [Fact]
    public void A_teammate_who_comes_back_and_leaves_again_in_one_tick_is_away_after_it()
    {
        var match = Together();
        var session = new LockstepSession(Replay.BuildWorld(DataLoader.Value, match), Anna, match.InputDelay);
        session.Receive(new PlayerLeft(Cleo, 3));
        session.Receive(new PlayerReturned(Cleo, 5));
        session.Receive(new PlayerLeft(Cleo, 5));
        // A computer's row has no comings and goings to wait for.
        session.Receive(new PlayerLeft(new PlayerId(2), 2));
        Assert.Equal(3, session.Presence.Count);

        while (session.World.Tick < 4)
        {
            session.Pump();
            Others(session, session.World.Tick is >= 1 and < 3 ? new[] { Ben, Cleo } : new[] { Ben });
            Assert.True(session.TryStep());
        }
        // Away from 3: Anna keeps Cleo's machines, and Red stays a player's.
        Assert.True(session.World.Players.Away(Cleo));
        Assert.Equal(Anna, session.World.Players.KeeperOf(Cleo));
        Assert.Equal(Controller.Player, session.World.Races[Race.Red].Controller);

        // Tick 4 waits for Cleo no more than tick 5, where she comes back and goes again.
        session.Pump();
        Others(session, Ben);
        Assert.True(session.TryStep());
        Assert.True(session.World.Players.Away(Cleo));
        session.Pump();
        Assert.Equal([Ben], session.Waiting());
        Others(session, Ben);
        Assert.True(session.TryStep());
        Assert.Equal(6, session.World.Tick);
        Assert.True(session.World.Players.Away(Cleo));
        Assert.False(session.World.Players.Away(Anna));
    }

    [Fact]
    public void A_recording_of_two_commanders_a_side_plays_back_the_same()
    {
        var match = Together();
        var live = new LockstepSession(Replay.BuildWorld(DataLoader.Value, match), Anna, match.InputDelay);
        var rng = new Random(4);
        var red = live.World.Machines.Where(m => m.Race == Race.Red).Select(m => m.Id).ToList();
        live.Receive(new PlayerLeft(Cleo, 150));
        live.Receive(new PlayerReturned(Cleo, 220));
        for (var i = 0; i < 300; i++)
        {
            Command Order() => new MoveCommand(Race.Red, [red[rng.Next(red.Count)]], new System.Numerics.Vector2(300 + rng.Next(800), 300 + rng.Next(800)));
            if (rng.Next(15) == 0)
                live.Queue(Order());
            live.Pump();
            if (live.World.Tick >= match.InputDelay)
            {
                if (!live.Away(Cleo, live.World.Tick))
                    live.Receive(new Turn(live.World.Tick, Cleo, rng.Next(15) == 0 ? [Order()] : []));
                live.Receive(new Turn(live.World.Tick, Ben, []));
            }
            Assert.True(live.TryStep(), $"stuck at tick {live.World.Tick}");
        }
        Assert.Contains(live.Played, t => t.Player == Cleo);

        var path = Path.Combine(Path.GetTempPath(), $"together-{Guid.NewGuid():N}{Replay.Extension}");
        new Replay(match, live.Played, live.World.Tick, live.Presence).Save(path);
        var loaded = Replay.Load(path);
        File.Delete(path);
        Assert.Equal(NetJson.Protocol, loaded.Format);
        Assert.Equal(match.Players, loaded.Match.Players);

        var playback = loaded.Playback(Replay.BuildWorld(DataLoader.Value, loaded.Match));
        while (playback.World.Tick < loaded.Ticks && playback.TryStep())
        {
        }
        Assert.Equal(live.World.Tick, playback.World.Tick);
        Assert.Equal(live.World.Checksum(), playback.World.Checksum());
    }
}
