using Machines.Import.Parm;

namespace Machines.Import.Audio;

/// <summary>
/// One line of a sound definition file (<c>snddef64.dat</c>, <c>Sd2d64.dat</c>, ...):
/// <c>SID_BOLTER sounds/bolter.wav 100 30 1000 1250 PRELOAD 3D HW_MIXED</c>.
/// The original chose the file by sound mode (3D: <c>snddef</c>, 2D: <c>Sd2d</c>) and memory (64 = full set, 32 = reduced).
/// </summary>
/// <param name="MaxVolumePercent">Loudest the sound plays.</param>
/// <param name="NearDistance">Distance in metres up to which the sound plays at full volume.</param>
/// <param name="FarDistance">Distance in metres beyond which it is silent.</param>
/// <param name="Priority">Higher priorities win when too many sounds play at once.</param>
public sealed record SoundDefinition(
    string Id,
    string File,
    int MaxVolumePercent,
    float NearDistance,
    float FarDistance,
    int Priority,
    bool Preload,
    bool Is3D,
    bool HardwareMixed,
    int LineNumber);

/// <summary>
/// One line of <c>sounds/vemail/vemail.dat</c>: a spoken report such as "Grunt destroyed".
/// <c>VID_GRUNT_DESTROYED sounds\vemail\Grunt\Gt_DES_1.wav VM_PART_FUNCTION 10 NO_PRELOAD</c>.
/// </summary>
/// <param name="Type"><c>VM_FULL_FUNCTION</c>, <c>VM_PART_FUNCTION</c>, <c>VM_TASK_AFFIRMATION</c>,
/// <c>VM_SELECTION_AFFIRMATION</c> or <c>VM_WAIT_UNTIL_NOTHING_PLAYING</c>.</param>
public sealed record VoiceMailDefinition(string Id, string File, string Type, int Priority, bool Preload, int LineNumber);

public static class SoundDefinitionParser
{
    public static IReadOnlyList<SoundDefinition> Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Follows <c>W4dSoundManager::readSoundDefinitionFile</c>; duplicate ids are rejected, as the original asserts.</summary>
    public static IReadOnlyList<SoundDefinition> Parse(string text)
    {
        var sounds = new List<SoundDefinition>();
        var ids = new HashSet<string>();

        foreach (var line in TextLines.Read(text))
        {
            // id file volume near far priority [PRELOAD|NOPRELOAD] [3D|2D] [HW_MIXED]
            if (line.Args.Count is < 5 or > 8)
                throw new ParmFormatException($"{line.Keyword}: expected file, volume, near, far, priority and up to three flags", line.LineNumber);
            if (!ids.Add(line.Keyword))
                throw new ParmFormatException($"sound {line.Keyword} is defined twice", line.LineNumber);

            var flags = line.Args.Skip(5).ToList();
            if (flags.Except(["PRELOAD", "NOPRELOAD", "3D", "2D", "HW_MIXED"]).FirstOrDefault() is { } unknown)
                throw new ParmFormatException($"{line.Keyword}: unknown flag '{unknown}'", line.LineNumber);

            sounds.Add(new SoundDefinition(
                line.Keyword,
                line.Args[0],
                ParmDataParser.ParseInt(line.Args[1], line),
                ParmDataParser.ParseFloat(line.Args[2], line),
                ParmDataParser.ParseFloat(line.Args[3], line),
                ParmDataParser.ParseInt(line.Args[4], line),
                // The original reads the flags by position: 7th word PRELOAD, 8th 3D, 9th HW_MIXED.
                Preload: flags.Count > 0 && flags[0] == "PRELOAD",
                Is3D: flags.Count > 1 && flags[1] == "3D",
                HardwareMixed: flags.Count > 2 && flags[2] == "HW_MIXED",
                line.LineNumber));
        }
        return sounds;
    }
}

public static class VoiceMailParser
{
    private static readonly HashSet<string> Types =
        ["VM_FULL_FUNCTION", "VM_PART_FUNCTION", "VM_TASK_AFFIRMATION", "VM_SELECTION_AFFIRMATION", "VM_WAIT_UNTIL_NOTHING_PLAYING"];

    public static IReadOnlyList<VoiceMailDefinition> Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Follows <c>MachLogVoiceMailManager</c>; file names are lower-cased there, so they are kept as written here.</summary>
    public static IReadOnlyList<VoiceMailDefinition> Parse(string text)
    {
        var mails = new List<VoiceMailDefinition>();
        var ids = new HashSet<string>();

        foreach (var line in TextLines.Read(text))
        {
            if (line.Args.Count != 4)
                throw new ParmFormatException($"{line.Keyword}: expected file, type, priority and PRELOAD or NO_PRELOAD", line.LineNumber);
            if (!ids.Add(line.Keyword))
                throw new ParmFormatException($"voice mail {line.Keyword} is defined twice", line.LineNumber);
            if (!Types.Contains(line.Args[1]))
                throw new ParmFormatException($"{line.Keyword}: unknown voice mail type '{line.Args[1]}'", line.LineNumber);
            if (line.Args[3] is not ("PRELOAD" or "NO_PRELOAD"))
                throw new ParmFormatException($"{line.Keyword}: last word must be PRELOAD or NO_PRELOAD", line.LineNumber);

            mails.Add(new VoiceMailDefinition(line.Keyword, line.Args[0], line.Args[1],
                ParmDataParser.ParseInt(line.Args[2], line), line.Args[3] == "PRELOAD", line.LineNumber));
        }
        return mails;
    }
}
