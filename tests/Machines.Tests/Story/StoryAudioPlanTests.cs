using System.Numerics;
using Machines.Game;
using Machines.Import.Planets;

namespace Machines.Tests.Story;

/// <summary>The story's sound as rules (game/scripts/StoryAudioPlan.cs): channels, the line queue, the threat, the deck's rooms and the assets they name.</summary>
public class StoryAudioPlanTests
{
    private static string Repo()
    {
        var data = RepoPaths.OriginalDataFolder();
        return Path.GetFullPath(Path.Combine(data, "..", ".."));
    }

    private static string Assets(string relative) => Path.Combine(Repo(), "game", "assets", "story", relative);

    private static StorySfxManifest Sfx() => StorySfxManifest.Parse(File.ReadAllText(Assets("sfx/manifest.json")));

    private static StoryVoiceManifest Voices() => StoryVoiceManifest.Parse(File.ReadAllText(Assets("voice/manifest.json")));

    [Theory]
    [InlineData("P_ANN_WAKE_1", "ship", StoryVoiceChannel.Announcer)]
    [InlineData("P_ANN_CRASH", "ship-old", StoryVoiceChannel.Announcer)]
    [InlineData("P_AUTO_T10", "auto", StoryVoiceChannel.Announcer)]
    [InlineData("P_ANN_FOUND", "ship-dry", StoryVoiceChannel.Local)]
    [InlineData("P_IKE_WAKE", "ike", StoryVoiceChannel.Radio)]
    [InlineData("P_BOB_GLITCH", "bob-radio", StoryVoiceChannel.Radio)]
    [InlineData("P_WHISPER_1", "whisper", StoryVoiceChannel.Whisper)]
    [InlineData("P_ANN_FOUND", null, StoryVoiceChannel.Local)]
    [InlineData("P_AUTO_1", null, StoryVoiceChannel.Announcer)]
    [InlineData("P_BOB_UP", null, StoryVoiceChannel.Radio)]
    [InlineData("P_HUD_BOOT", null, StoryVoiceChannel.Text)]
    [InlineData("P_L7_BOOT_HUM", "l7", StoryVoiceChannel.Inner)]
    [InlineData("P_L7_BOOT_LEGS", null, StoryVoiceChannel.Inner)]
    public void LinesGoToTheirChannel(string line, string? preset, StoryVoiceChannel channel) =>
        Assert.Equal(channel, StorySoundscape.Channel(line, preset));

    [Fact]
    public void OnlyTheFriendlyPaHasAGong()
    {
        Assert.True(StorySoundscape.Gong("P_ANN_WAKE_1", "ship"));
        Assert.True(StorySoundscape.Gong("P_ANN_C1", "ship"));
        Assert.False(StorySoundscape.Gong("P_ANN_C5", "ship"));
        Assert.False(StorySoundscape.Gong("P_ANN_FOUND", "ship-dry"));
        Assert.False(StorySoundscape.Gong("P_ANN_CRASH", "ship-old"));
        Assert.False(StorySoundscape.Gong("P_AUTO_1", "auto"));
        Assert.False(StorySoundscape.Gong("P_IKE_WAKE", "ike"));
    }

    private static StoryQueuedLine Line(string id, StoryVoiceChannel channel, double seconds, double asked, bool gong = false) =>
        new(id, channel, seconds, asked) { Gong = gong };

    [Fact]
    public void AnAnnouncementWaitsForTheRadioAndGoesFirst()
    {
        var queue = new StoryLineQueue();
        var started = queue.Add(Line("P_IKE_WAKE", StoryVoiceChannel.Radio, 3, 0), 0);
        Assert.Equal(StoryQueueChange.Started, Assert.Single(started).Change);
        queue.Add(Line("P_BOB_WAKE", StoryVoiceChannel.Radio, 2, 0.5), 0.5);

        // Ike is not cut off: the announcement waits for him, ahead of Bob.
        Assert.Empty(queue.Add(Line("P_ANN_WAKE_1", StoryVoiceChannel.Announcer, 4, 1), 1));
        Assert.Equal(["P_ANN_WAKE_1", "P_BOB_WAKE"], queue.Waiting.Select(w => w.Id));
        var ike = queue.Update(3.0);
        Assert.Equal((StoryQueueChange.Ended, "P_IKE_WAKE"), (Assert.Single(ike).Change, ike[0].Line.Id));
        var announcement = queue.Update(3.0 + StoryLineQueue.Gap);
        Assert.Equal((StoryQueueChange.Started, "P_ANN_WAKE_1"), (Assert.Single(announcement).Change, announcement[0].Line.Id));

        // Bob waits for the announcement and the gap after it.
        var end = 3.0 + StoryLineQueue.Gap + 4;
        Assert.Empty(queue.Update(end - 0.1));
        var ended = queue.Update(end);
        Assert.Equal((StoryQueueChange.Ended, "P_ANN_WAKE_1"), (Assert.Single(ended).Change, ended[0].Line.Id));
        var bob = queue.Update(end + StoryLineQueue.Gap);
        Assert.Equal((StoryQueueChange.Started, "P_BOB_WAKE"), (bob[0].Change, bob[0].Line.Id));
    }

    [Fact]
    public void TheLocatorsOwnLoudspeakerCutsTheRadio()
    {
        var queue = new StoryLineQueue();
        queue.Add(Line("P_IKE_STEPS", StoryVoiceChannel.Radio, 3, 0), 0);
        var events = queue.Add(Line("P_ANN_FOUND", StoryVoiceChannel.Local, 1, 1), 1);
        Assert.Equal([(StoryQueueChange.Interrupted, "P_IKE_STEPS"), (StoryQueueChange.Started, "P_ANN_FOUND")],
            events.Select(e => (e.Change, e.Line.Id)));
    }

    [Fact]
    public void TheRadioHasATurnBetweenAnnouncements()
    {
        // The alarm: two announcements asked at once, Ike's bark, then two more; Ike and Bob each come after the
        // announcement speaking, not after all of them (which would leave them stale).
        var queue = new StoryLineQueue();
        var order = new List<string>();
        void Note(IEnumerable<StoryQueueEvent> events) => order.AddRange(events.Where(e => e.Change == StoryQueueChange.Started).Select(e => e.Line.Id));
        Note(queue.Add(Line("C1", StoryVoiceChannel.Announcer, 5, 0), 0));
        Note(queue.Add(Line("C2", StoryVoiceChannel.Announcer, 4, 0), 0));
        var asks = new (double At, StoryQueuedLine Line)[]
        {
            (1.5, Line("P_IKE_HAMMER", StoryVoiceChannel.Radio, 3, 1.5)),
            (6.5, Line("C3", StoryVoiceChannel.Announcer, 3, 6.5)),
            (7.5, Line("P_BOB_ROUND", StoryVoiceChannel.Radio, 3, 7.5)),
            (11, Line("C4", StoryVoiceChannel.Announcer, 4, 11)),
        };
        var dropped = new List<string>();
        var next = 0;
        for (var now = 0.0; now < 40; now += 0.05)
        {
            var events = new List<StoryQueueEvent>();
            while (next < asks.Length && asks[next].At <= now)
                events.AddRange(queue.Add(asks[next++].Line, now));
            events.AddRange(queue.Update(now));
            Note(events);
            dropped.AddRange(events.Where(e => e.Change is StoryQueueChange.Dropped or StoryQueueChange.Interrupted).Select(e => e.Line.Id));
        }
        Assert.Empty(dropped);
        Assert.Equal(["C1", "P_IKE_HAMMER", "C2", "P_BOB_ROUND", "C3", "C4"], order);
    }

    [Fact]
    public void BarksDuringTheAlarmDoNotMakeItsLastAnnouncementStale()
    {
        // The alarm (asks relative to it, the PA's lengths from the German manifest) with a bark in every turn the radio
        // gets: one still speaking as the alarm goes (Ike told to wait), Ike's hammering, Bob's going round and an order a
        // co-op peer gives after C3. Each is 5 s, as a recording may well be. C4 then starts some 27 s after it was asked,
        // most of it under the barks; that time does not count against AnnouncementStale, so the ship still says it.
        var queue = new StoryLineQueue();
        var order = new List<string>();
        var dropped = new List<string>();
        var asks = new (double At, StoryQueuedLine Line)[]
        {
            (-0.5, Line("P_IKE_WAIT", StoryVoiceChannel.Radio, 5, -0.5)),
            (0, Line("C1", StoryVoiceChannel.Announcer, 5.97, 0, gong: true)),
            (0, Line("C2", StoryVoiceChannel.Announcer, 4.72, 0)),
            (1.5, Line("P_IKE_HAMMER", StoryVoiceChannel.Radio, 5, 1.5)),
            (6.5, Line("C3", StoryVoiceChannel.Announcer, 4.51, 6.5)),
            (7.5, Line("P_BOB_ROUND", StoryVoiceChannel.Radio, 5, 7.5)),
            (11, Line("C4", StoryVoiceChannel.Announcer, 5.86, 11)),
            (20, Line("P_BOB_FOLLOW", StoryVoiceChannel.Radio, 5, 20)),
            (30, Line("P_BOB_FIRE", StoryVoiceChannel.Radio, 1.91, 30)),
        };
        var next = 0;
        var c4 = double.NaN;
        for (var now = -1.0; now < 80; now += 0.05)
        {
            var events = new List<StoryQueueEvent>();
            while (next < asks.Length && asks[next].At <= now)
                events.AddRange(queue.Add(asks[next++].Line, now));
            events.AddRange(queue.Update(now));
            order.AddRange(events.Where(e => e.Change == StoryQueueChange.Started).Select(e => e.Line.Id));
            dropped.AddRange(events.Where(e => e.Change is StoryQueueChange.Dropped or StoryQueueChange.Interrupted).Select(e => e.Line.Id));
            if (events.Any(e => e is { Change: StoryQueueChange.Started, Line.Id: "C4" }))
                c4 = now;
        }
        Assert.Empty(dropped);
        Assert.Equal(["P_IKE_WAIT", "C1", "P_IKE_HAMMER", "C2", "P_BOB_ROUND", "C3", "P_BOB_FOLLOW", "C4", "P_BOB_FIRE"], order);
        Assert.True(c4 - 11 > StoryLineQueue.AnnouncementStale, $"C4 waited only {c4 - 11:0.00} s: the case no longer tests the booking");
    }

    [Fact]
    public void AnnouncementsKeepTheirOrderAheadOfTheRadio()
    {
        var queue = new StoryLineQueue();
        queue.Add(Line("A1", StoryVoiceChannel.Announcer, 2, 0), 0);
        queue.Add(Line("R1", StoryVoiceChannel.Radio, 1, 0), 0);
        queue.Add(Line("A2", StoryVoiceChannel.Announcer, 2, 0), 0);
        queue.Add(Line("A3", StoryVoiceChannel.Local, 2, 0), 0);
        Assert.Equal(["A2", "A3", "R1"], queue.Waiting.Select(w => w.Id));
    }

    [Fact]
    public void StaleRadioIsDropped()
    {
        // A radio line that waits behind the radio longer than RadioStale is dropped (the time an announcement speaks
        // does not count, see TheShipTalkingDoesNotMakeABarkStale).
        var queue = new StoryLineQueue();
        queue.Add(Line("R0", StoryVoiceChannel.Radio, StoryLineQueue.RadioStale + 2, 0), 0);
        queue.Add(Line("R1", StoryVoiceChannel.Radio, 1, 0.1), 0.1);
        queue.Update(StoryLineQueue.RadioStale + 2);
        var events = queue.Update(StoryLineQueue.RadioStale + 2 + StoryLineQueue.Gap);
        Assert.Contains(events, e => e is { Change: StoryQueueChange.Dropped, Line.Id: "R1" });
        Assert.Null(queue.Current);
    }

    [Fact]
    public void TheGongComesAfterAQuietPa()
    {
        var queue = new StoryLineQueue();
        var first = queue.Add(Line("P_ANN_WAKE_1", StoryVoiceChannel.Announcer, 3, 0, gong: true), 0);
        Assert.Equal(StoryLineQueue.GongLead, first.Single().Line.Lead);
        queue.Update(3 + StoryLineQueue.GongLead);
        // Straight after: no gong.
        var second = queue.Add(Line("P_ANN_WAKE_2", StoryVoiceChannel.Announcer, 3, 5, gong: true), 5);
        Assert.Equal(0, second.Single().Line.Lead);
        queue.Update(8);
        // After a quiet while: the gong again.
        var third = queue.Add(Line("P_ANN_DECK_1", StoryVoiceChannel.Announcer, 3, 40, gong: true), 40);
        Assert.Equal(StoryLineQueue.GongLead, third.Single().Line.Lead);
    }

    [Fact]
    public void TheShipTalkingDoesNotMakeABarkStale()
    {
        // The squad: the ship's announcement (with its gong), Bob's "the squad" behind it, and his "Door secured." asked
        // as the announcement starts. The bark waits more than RadioStale in all, most of it under the PA, and is still said.
        var queue = new StoryLineQueue();
        queue.Add(Line("P_ANN_SQUAD", StoryVoiceChannel.Announcer, 7.1, 0, gong: true), 0);
        queue.Add(Line("P_BOB_SQUAD", StoryVoiceChannel.Radio, 3.9, 0.2), 0.2);
        queue.Add(Line("P_BOB_BARRICADE", StoryVoiceChannel.Radio, 1.3, 1.5), 1.5);
        var started = new List<string>();
        for (var now = 1.5; now < 30; now += 0.05)
        {
            var events = queue.Update(now);
            Assert.DoesNotContain(events, e => e.Change == StoryQueueChange.Dropped);
            started.AddRange(events.Where(e => e.Change == StoryQueueChange.Started).Select(e => e.Line.Id));
        }
        Assert.Equal(["P_BOB_SQUAD", "P_BOB_BARRICADE"], started);
    }

    [Fact]
    public void ACutOfTheAnnouncementsLeavesTheRadio()
    {
        // The core blows: the PA breaks off and what it had still to say goes, Ike's "Run!" waiting does not.
        var queue = new StoryLineQueue();
        queue.Add(Line("C5", StoryVoiceChannel.Announcer, 4, 0), 0);
        queue.Add(Line("C9", StoryVoiceChannel.Announcer, 2, 0), 0);
        queue.Add(Line("P_IKE_LIMITER", StoryVoiceChannel.Radio, 2, 0.5), 0.5);
        var events = queue.Cut(1, clear: true, onlyAnnouncements: true);
        Assert.Equal([(StoryQueueChange.Interrupted, "C5"), (StoryQueueChange.Dropped, "C9")], events.Select(e => (e.Change, e.Line.Id)));
        Assert.Equal("P_IKE_LIMITER", Assert.Single(queue.Waiting).Id);
        Assert.Equal("P_IKE_LIMITER", queue.Update(1 + StoryLineQueue.Gap).Single().Line.Id);

        // A radio line speaking goes on through such a cut.
        Assert.Empty(queue.Cut(2, clear: true, onlyAnnouncements: true));
        Assert.Equal("P_IKE_LIMITER", queue.Current?.Id);
    }

    [Fact]
    public void ACutscenesLineLetsABarkFinishButCutsTheShip()
    {
        // Ike urges the others into the pod a moment before it drops: his bark is let finish, the drop's line comes next,
        // ahead of Bob's bark waiting.
        var queue = new StoryLineQueue();
        queue.Add(Line("P_IKE_POD", StoryVoiceChannel.Radio, 1.74, 0), 0);
        queue.Add(Line("P_BOB_POD", StoryVoiceChannel.Radio, 0.9, 0.5), 0.5);
        Assert.Empty(queue.Force(Line("P_AUTO_POD_GO", StoryVoiceChannel.Announcer, 3.3, 1), 1));
        Assert.Equal("P_IKE_POD", queue.Current?.Id);
        Assert.Equal([(StoryQueueChange.Ended, "P_IKE_POD")], queue.Update(1.74).Select(e => (e.Change, e.Line.Id)));
        Assert.Equal([(StoryQueueChange.Started, "P_AUTO_POD_GO")], queue.Update(1.74 + StoryLineQueue.Gap).Select(e => (e.Change, e.Line.Id)));
        Assert.Equal("P_BOB_POD", Assert.Single(queue.Waiting).Id);

        // An announcement speaking is cut off by it.
        var ship = new StoryLineQueue();
        ship.Add(Line("P_AUTO_POD_NO", StoryVoiceChannel.Announcer, 3, 0), 0);
        Assert.Equal([(StoryQueueChange.Interrupted, "P_AUTO_POD_NO"), (StoryQueueChange.Started, "P_AUTO_POD_GO")],
            ship.Force(Line("P_AUTO_POD_GO", StoryVoiceChannel.Announcer, 3.3, 1), 1).Select(e => (e.Change, e.Line.Id)));
    }

    [Fact]
    public void ACutClearsTheQueue()
    {
        var queue = new StoryLineQueue();
        queue.Add(Line("A1", StoryVoiceChannel.Announcer, 5, 0), 0);
        queue.Add(Line("A2", StoryVoiceChannel.Announcer, 5, 0), 0);
        var events = queue.Cut(1, clear: true);
        Assert.Equal([(StoryQueueChange.Interrupted, "A1"), (StoryQueueChange.Dropped, "A2")], events.Select(e => (e.Change, e.Line.Id)));
        Assert.Null(queue.Current);
        Assert.Empty(queue.Waiting);
    }

    [Theory]
    [InlineData(null, 0f, 0)]
    [InlineData(80f, 0f, 0)]
    [InlineData(30f, 0f, 1)]
    [InlineData(10f, 0f, 2)]
    [InlineData(null, 0.3f, 1)]
    [InlineData(null, 0.7f, 2)]
    public void ThreatFollowsTheThresherAndSuspicion(float? distance, float suspicion, int level) =>
        Assert.Equal(level, StoryThreat.Raw(distance, suspicion));

    [Fact]
    public void ThreatRisesAtOnceAndCalmsSlowly()
    {
        var threat = new StoryThreat();
        Assert.True(threat.Update(10, 0, 0));
        Assert.Equal(2, threat.Level);
        Assert.False(threat.Update(null, 0, 1));
        Assert.False(threat.Update(null, 0, 1 + StoryThreat.CalmAfter - 0.1));
        Assert.True(threat.Update(null, 0, 1 + StoryThreat.CalmAfter));
        Assert.Equal(0, threat.Level);

        // The director's level holds whatever the Thresher does.
        threat.Override = 1;
        Assert.True(threat.Update(5, 0, 10));
        Assert.Equal(1, threat.Level);
        threat.Override = null;
        Assert.True(threat.Update(5, 0, 11));
        Assert.Equal(2, threat.Level);
    }

    [Fact]
    public void TheDecksRoomsHaveAmbienceAndReverb()
    {
        var deck = DeckParser.Load(Path.Combine(Repo(), "data", "planets", "prolog", "prolog.deck"));
        var sfx = Sfx();
        var rooms = deck.Zones.Where(z => z.Tags.Contains("room")).ToList();
        Assert.NotEmpty(rooms);
        foreach (var room in rooms)
        {
            var layers = StorySoundscape.Ambience(room.Name, StorySoundscape.Section(room));
            Assert.True(layers.Count > 0, $"room {room.Name} has no ambience");
            Assert.All(layers, l => Assert.True(sfx.Loops(l.Sound), $"{room.Name}: {l.Sound} is no loop of the sfx set"));
        }
        // A duct inside deck A is heard as the duct, deck A as a hall, the core as the biggest hall of all.
        var duct = StorySoundscape.RoomAt(deck, new Vector2(184, 120));
        Assert.Equal("V1b", duct?.Name);
        Assert.Equal(StorySoundscape.Duct, StorySoundscape.Reverb(duct));
        Assert.Equal(StorySoundscape.Hall, StorySoundscape.Reverb(StorySoundscape.RoomAt(deck, new Vector2(156, 60))));
        Assert.Equal(StorySoundscape.Cathedral, StorySoundscape.Reverb(deck.Zone("R9")));
        Assert.Equal(StorySoundscape.Room, StorySoundscape.Reverb(null));
    }

    [Fact]
    public void TheMusicStandInsUseTheSfxSet()
    {
        var sfx = Sfx();
        foreach (var cue in StorySoundscape.Cues)
        {
            var layers = StorySoundscape.MusicStandIn(cue);
            if (cue == "silence")
            {
                Assert.Empty(layers);
                continue;
            }
            Assert.Contains(layers, l => l.Threat == 0 && l.Every == 0);
            Assert.Contains(layers, l => l.Threat == 2);
            Assert.All(layers, l => Assert.True(sfx.Has(l.Sound), $"{cue}: {l.Sound} missing"));
            Assert.All(layers.Where(l => l.Every == 0), l => Assert.True(sfx.Loops(l.Sound), $"{cue}: {l.Sound} does not loop"));
            // No steps that are not his: they would ruin finding the Thresher by ear (stage 7).
            Assert.DoesNotContain(layers, l => l.Sound.StartsWith("thresher_", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void TheDirectorsSoundsExist()
    {
        var sfx = Sfx();
        foreach (var id in new[] { "pa_gong", "pa_cutoff", "hack_click", "sting_found", "sting_dread", "sting_alarm", "clamp_release", "amb_alarm_klaxon" })
        {
            Assert.True(sfx.Has(id), id);
            Assert.True(File.Exists(Assets($"sfx/{sfx.File(id)}")), id);
        }
        // A family by its name plays one of its numbered variants.
        Assert.Equal("thresher_step_2", sfx.Resolve("thresher_step", count => 1));
        Assert.Null(sfx.Resolve("no_such_sound", _ => 0));
    }

    [Fact]
    public void EveryVoicedLineHasItsFilesAndAChannel()
    {
        var voices = Voices();
        Assert.NotEmpty(voices.Lines);
        foreach (var line in voices.Lines)
        {
            Assert.NotEqual(StoryVoiceChannel.Text, StorySoundscape.Channel(line, voices.Preset(line)));
            foreach (var lang in new[] { "en", "de" })
            {
                var file = voices.Find(line, lang);
                Assert.NotNull(file);
                Assert.True(file.Seconds > 0, $"{line} {lang}");
                Assert.True(File.Exists(Assets($"voice/{file.File}")), $"{line} {lang}: {file.File}");
            }
            // The preset and the id agree on where the line is heard.
            Assert.Equal(StorySoundscape.Channel(line, null), StorySoundscape.Channel(line, voices.Preset(line)));
        }
        // A language without the line falls back to English.
        Assert.Equal("en", voices.Find("P_ANN_WAKE_1", "fr")?.Lang);
        Assert.Null(voices.Find("P_NO_SUCH_LINE", "en"));
    }

    [Fact]
    public void SpeakersAreTheNearestInRange()
    {
        var speakers = new[] { new Vector3(100, 5, 0), new Vector3(10, 5, 0), new Vector3(30, 5, 0), new Vector3(20, 5, 0), new Vector3(5, 5, 0) };
        var near = StorySoundscape.NearestSpeakers(speakers, Vector3.Zero);
        Assert.Equal([new Vector3(5, 5, 0), new Vector3(10, 5, 0), new Vector3(20, 5, 0)], near);
        Assert.Empty(StorySoundscape.NearestSpeakers(speakers, new Vector3(1000, 0, 0)));
        Assert.InRange(StorySoundscape.SpeakerDelay(0), 0.03, 0.031);
        Assert.InRange(StorySoundscape.SpeakerDelay(2), 0.079, 0.081);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(5, 0)]
    [InlineData(6, 1)]
    [InlineData(7, 2)]
    [InlineData(12, 2)]
    [InlineData(14, 2)]
    public void A_chapter_begun_at_a_checkpoint_starts_the_playlist_where_a_run_usually_hears_it(int checkpoint, int track) =>
        Assert.Equal((track, 0d), StoryScore.Start(checkpoint, null));
}
