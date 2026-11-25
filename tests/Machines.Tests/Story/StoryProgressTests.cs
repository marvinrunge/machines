using System.Text.Json;
using Machines.Game;
using Machines.Sim.Story;

namespace Machines.Tests.Story;

/// <summary>
/// The story's progress (game/scripts/StoryProgress.cs, plan WP-K): where Continue and the host's co-op room go on from, a
/// checkpoint reached, and a chapter played to its end with its film and card.
/// </summary>
public class StoryProgressTests
{
    [Fact]
    public void A_story_never_begun_starts_the_prologue_from_its_start()
    {
        var progress = new StoryProgress();
        Assert.False(progress.CanContinue);
        Assert.Equal((StoryOptions.Prologue, 0), progress.Next);
    }

    [Fact]
    public void A_checkpoint_reached_is_where_the_story_goes_on()
    {
        var progress = new StoryProgress();
        progress.Reached(StoryOptions.Prologue, 0);
        progress.Reached(StoryOptions.Prologue, 4);
        Assert.True(progress.CanContinue);
        Assert.Equal((StoryOptions.Prologue, 4), progress.Next);
        Assert.Equal([StoryOptions.Prologue], progress.Begun);
        Assert.Empty(progress.Done);
    }

    [Fact]
    public void A_chapter_played_to_its_end_is_done_and_begins_again_from_its_start()
    {
        var progress = new StoryProgress();
        progress.Reached(StoryOptions.Prologue, 6);
        progress.Finished(StoryOptions.Prologue);
        Assert.False(progress.CanContinue);
        Assert.True(progress.Ended);
        Assert.Equal([StoryOptions.Prologue], progress.Done);
        Assert.Equal((StoryOptions.Prologue, 0), progress.Next);

        // Played again: under way until it ends again, still done once.
        progress.Reached(StoryOptions.Prologue, 2);
        Assert.True(progress.CanContinue);
        Assert.Equal((StoryOptions.Prologue, 2), progress.Next);
        progress.Finished(StoryOptions.Prologue);
        Assert.Single(progress.Done);
    }

    [Fact]
    public void The_options_carry_the_checkpoint_and_the_names_given()
    {
        var progress = new StoryProgress { IkeName = "Rost", BobName = "" };
        progress.Reached(StoryOptions.Prologue, 3);
        var (chapter, checkpoint) = progress.Next;
        var options = progress.Options(chapter, checkpoint);
        Assert.Equal(3, options.Checkpoint);
        Assert.Equal("Rost", options.IkeName);
        Assert.Equal("Bob", options.BobName);
    }

    [Fact]
    public void The_file_keeps_what_was_reached_and_nothing_worked_out()
    {
        var progress = new StoryProgress();
        progress.Reached(StoryOptions.Prologue, 6);
        progress.Finished(StoryOptions.Prologue);
        var text = JsonSerializer.Serialize(progress);
        Assert.DoesNotContain("CanContinue", text);
        Assert.DoesNotContain("Next", text);
        var back = JsonSerializer.Deserialize<StoryProgress>(text)!;
        Assert.True(back.Ended);
        Assert.Equal([StoryOptions.Prologue], back.Done);
        Assert.False(back.CanContinue);
    }

    [Fact]
    public void A_file_from_before_stage_7s_checkpoints_is_dropped_and_the_story_starts_afresh()
    {
        // Saved before stage 7 numbered the checkpoints 0–14: without a numbering, or an older one. Its 5 meant another place.
        var old = """{ "Chapter": "prolog", "Checkpoint": 5, "Begun": ["prolog"], "IkeName": "Rost", "BobName": "Bob" }""";
        Assert.Null(StoryProgress.FromJson(old));
        Assert.Null(StoryProgress.FromJson("""{ "CheckpointVersion": 1, "Chapter": "prolog", "Checkpoint": 5 }"""));
        Assert.ThrowsAny<JsonException>(() => StoryProgress.FromJson("{ broken"));
    }

    [Fact]
    public void A_file_of_today_keeps_its_checkpoint_and_the_parts_salvaged()
    {
        var progress = new StoryProgress();
        progress.Reached(StoryOptions.Prologue, 11);
        Assert.True(progress.AddSalvaged("chassis:grunt2"));
        Assert.True(progress.AddSalvaged("weapon:ELECTRIC_CHARGE"));
        Assert.False(progress.AddSalvaged("chassis:grunt2"));
        var back = StoryProgress.FromJson(JsonSerializer.Serialize(progress))!;
        Assert.Equal(StoryProgress.CurrentCheckpoints, back.CheckpointVersion);
        Assert.Equal((StoryOptions.Prologue, 11), back.Next);
        Assert.Equal(["chassis:grunt2", "weapon:ELECTRIC_CHARGE"], back.Salvaged);
    }
}
