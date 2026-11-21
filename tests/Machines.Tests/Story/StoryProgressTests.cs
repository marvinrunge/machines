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
    public void An_old_file_without_the_end_reads_as_under_way()
    {
        // Saved before chapters could be played to their end: no Done, no Ended.
        var old = """{ "Chapter": "prolog", "Checkpoint": 5, "Begun": ["prolog"], "IkeName": "Ike", "BobName": "Bob" }""";
        var progress = JsonSerializer.Deserialize<StoryProgress>(old)!;
        Assert.True(progress.CanContinue);
        Assert.Equal(("prolog", 5), progress.Next);
        Assert.Empty(progress.Done);
    }
}
