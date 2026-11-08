using Machines.Game;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>The hangar's screen against the sim, which hears of everything later: what goes up comes down, and nothing stays open with no screen over it.</summary>
public class HangarWatchTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private sealed class Screen
    {
        private readonly HangarWatch _watch = new();

        public required GameWorld World { get; init; }

        public bool Up { get; private set; }

        public int Closed { get; private set; }

        public WiredState State => World.Wired!.Heroes[Race.Red];

        public void Open()
        {
            World.Issue(new WiredHangarCommand(Race.Red, true));
            _watch.Opening(State.HangarOpens);
            Up = true;
        }

        public void Close()
        {
            World.Issue(new WiredHangarCommand(Race.Red, false));
            _watch.Closing();
            Up = false;
        }

        /// <summary>A frame: so many ticks, then a look.</summary>
        public void Frame(int ticks = 1)
        {
            for (var i = 0; i < ticks; i++)
            {
                World.Step();
                foreach (var refused in World.Rejected.Where(r => r.Command is WiredHangarCommand { Open: true }))
                {
                    _watch.Refused();
                    Up &= !_watch.Answered(State.HangarOpens);
                }
            }
            switch (_watch.Follow(Up, State.HangarOpen, State.HangarOpens))
            {
                case HangarMove.TakeDown:
                    Up = false;
                    break;
                case HangarMove.Close:
                    Closed++;
                    World.Issue(new WiredHangarCommand(Race.Red, false));
                    break;
            }
        }
    }

    private static Screen New() => new()
    {
        World = MatchSetup.Create(DataLoader.Value, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, 7,
            new MatchOptions { Wired = new WiredOptions() }),
    };

    [Fact]
    public void The_screen_stays_while_the_opening_is_on_its_way_and_after()
    {
        var screen = New();

        screen.Open();
        Assert.True(screen.Up);
        screen.Frame();
        screen.Frame();

        Assert.True(screen.Up);
        Assert.True(screen.State.HangarOpen);
    }

    [Fact]
    public void An_opening_and_a_closing_between_two_looks_take_the_screen_down()
    {
        var screen = New();

        screen.Open();
        screen.World.Issue(new WiredReadyCommand(Race.Red));
        screen.Frame();

        Assert.Equal(1, screen.State.HangarOpens);
        Assert.False(screen.State.HangarOpen);
        Assert.False(screen.Up);
    }

    [Fact]
    public void The_sim_closing_the_hangar_takes_the_screen_down()
    {
        var screen = New();
        screen.Open();
        screen.Frame();

        screen.World.Issue(new WiredReadyCommand(Race.Red));
        screen.Frame();

        Assert.False(screen.Up);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void A_quick_second_opening_keeps_its_screen(int ticks)
    {
        var screen = New();
        screen.Open();
        screen.Frame(ticks);
        screen.Close();
        screen.Open();

        screen.Frame(1);
        screen.Frame(1);

        Assert.True(screen.Up);
        Assert.True(screen.State.HangarOpen);
        Assert.Equal(0, screen.Closed);
        Assert.Equal(2, screen.State.HangarOpens);
    }

    [Fact]
    public void Open_close_open_close_leaves_the_hangar_shut_and_the_countdown_running()
    {
        var screen = New();

        screen.Open();
        screen.Close();
        screen.Open();
        screen.Close();
        screen.Frame();
        screen.Frame();

        Assert.False(screen.Up);
        Assert.False(screen.State.HangarOpen);
        Assert.Equal(0, screen.Closed);
    }

    [Fact]
    public void A_refused_opening_takes_its_screen_down()
    {
        var screen = New();
        var hero = screen.World.HeroOf(Race.Red)!;
        screen.World.Damage(hero, 1_000_000, Race.Blue);
        screen.World.Step();

        screen.Open();
        screen.Frame();

        Assert.False(screen.Up);
        Assert.False(screen.State.HangarOpen);
    }

    [Fact]
    public void A_hangar_open_with_no_screen_is_closed_once()
    {
        var screen = New();
        screen.World.Issue(new WiredHangarCommand(Race.Red, true));
        screen.World.Step();

        screen.Frame(0);
        screen.Frame(0);
        Assert.Equal(1, screen.Closed);
        screen.Frame();

        Assert.False(screen.State.HangarOpen);
    }

    [Fact]
    public void A_screen_after_a_loaded_save_counts_on_from_the_saves_openings()
    {
        var screen = New();
        screen.World.Issue(new WiredHangarCommand(Race.Red, true));
        screen.World.Issue(new WiredHangarCommand(Race.Red, false));
        screen.World.Step();
        screen.Frame();

        screen.Open();
        screen.Frame(0);
        Assert.True(screen.Up);
        screen.Frame();

        Assert.True(screen.Up);
        Assert.True(screen.State.HangarOpen);
    }
}
