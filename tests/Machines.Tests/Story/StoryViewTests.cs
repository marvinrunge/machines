using Machines.Game;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Tests.Story;

/// <summary>
/// The story's camera rule: the prologue and the first act are played from the faceplate alone. The view from behind of
/// runs and battles (Game.Chase.cs) waits for the story's one switch (<see cref="StoryMarks.ThirdPerson"/>), which a later act
/// sets when L-7 finds the small flying machine; until then the Hero view option, F and the mouse wheel leave it in first person.
/// </summary>
public class StoryViewTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameWorld Start(int checkpoint) =>
        MatchSetup.Create(DataLoader.Value, StoryOptions.Prologue, StoryOptions.Prologue,
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, 3,
            new MatchOptions { Story = new StoryOptions { Checkpoint = checkpoint }, FogOfWar = false });

    [Fact]
    public void Every_match_but_a_story_may_be_seen_from_behind() => Assert.True(ChaseCamera.Allowed(null));

    [Fact]
    public void The_prologue_stays_in_first_person_from_every_checkpoint()
    {
        for (var checkpoint = 0; checkpoint <= Prologue.LastCheckpoint; checkpoint++)
        {
            var world = Start(checkpoint);
            for (var i = 0; i < 2 * GameWorld.TicksPerSecond; i++)
                world.Step();
            Assert.NotNull(world.Story);
            Assert.False(StoryMarks.ThirdPersonUnlocked(world.Story!), $"checkpoint {checkpoint}");
            Assert.False(ChaseCamera.Allowed(world.Story), $"checkpoint {checkpoint}");
        }
    }

    [Fact]
    public void A_story_hero_has_no_gun_arc_whatever_the_options_say()
    {
        var world = Start(0);
        Assert.True(world.Options.HeroGunArc);
        Assert.NotNull(world.Wired);
        var grunt = world.AddMachine(Race.Red, new System.Numerics.Vector2(300, 250), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        Assert.Equal(0, world.GunArcOf(grunt));
    }

    [Fact]
    public void The_switch_lets_the_story_hero_out_of_the_faceplate()
    {
        var world = Start(0);
        world.Story!.Flags.Add(StoryMarks.ThirdPerson);
        Assert.True(ChaseCamera.Allowed(world.Story));
    }
}
