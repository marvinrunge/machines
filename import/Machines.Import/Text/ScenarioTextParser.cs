using System.Text;

namespace Machines.Import.Text;

/// <summary>
/// The texts of a campaign entry, <c>pdata/&lt;TEXT&gt;.bin</c> of the game copy (MachGuiDbITextData). Systems and
/// planets use only <see cref="CampaignText"/>; missions all of it. <c>&lt;w&gt;</c> starts a white heading line, and
/// a line holding only a no-break space is a spacer.
/// </summary>
/// <param name="CampaignText">Shown on the mission choice page.</param>
/// <param name="Objectives">The briefing, page by page.</param>
/// <param name="ObjectiveVoiceMails">The spoken briefing for each page, e.g. <c>Sounds/menus/tut01ob1.wav</c>, or empty.</param>
/// <param name="Hint">The briefing's hints.</param>
/// <param name="Tasks">The mission's tasks, numbered from 1 by the script's TASK_STATE.</param>
/// <param name="WinVoiceMail">Spoken with <see cref="WinText"/> on the debriefing, or empty.</param>
/// <param name="TasksStartAvailable">Whether each task is shown from the start, before the script makes it available.</param>
public sealed record ScenarioText(
    string CampaignText,
    IReadOnlyList<string> Objectives,
    IReadOnlyList<string> ObjectiveVoiceMails,
    string Hint,
    string WinText,
    string LoseText,
    IReadOnlyList<string> Tasks,
    string WinVoiceMail,
    string LoseVoiceMail,
    IReadOnlyList<bool> TasksStartAvailable)
{
    /// <summary>The text without its markup: no <c>&lt;w&gt;</c>, no spacer lines.</summary>
    public static string Plain(string text) => string.Join("\n",
        text.Replace("<w>", "").Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim(' ', ' ').Length > 0)).Trim();
}

/// <summary>
/// Reads the original's persistent stream (PerOstream) as MachGuiDbITextData's perWrite wrote it: every string and
/// vector is an object, a zero byte and a 32-bit id, then a string's length and Latin-1 bytes, or a vector's size,
/// reserve and capacity and its elements; a flag is a 32-bit number.
/// </summary>
public static class ScenarioTextParser
{
    public static ScenarioText Load(string path) => Parse(File.ReadAllBytes(path));

    public static ScenarioText Parse(byte[] bytes)
    {
        var reader = new Reader(bytes);
        reader.Header();
        var campaign = reader.Text();
        var objectives = reader.Texts();
        var objectiveVoiceMails = reader.Texts();
        var hint = reader.Text();
        var win = reader.Text();
        var lose = reader.Text();
        var tasks = reader.Texts();
        var winVoiceMail = reader.Text();
        var loseVoiceMail = reader.Text();
        var available = reader.Flags();
        return new ScenarioText(campaign, objectives, objectiveVoiceMails, hint, win, lose, tasks, winVoiceMail, loseVoiceMail, available);
    }

    private sealed class Reader(byte[] bytes)
    {
        private int _at;

        /// <summary>The text data object's own header comes first.</summary>
        public void Header() => _at = Math.Min(bytes.Length, 5);

        public string Text()
        {
            if (!Object())
                return "";
            var length = Int();
            if (length < 0 || _at + length > bytes.Length)
                throw new InvalidDataException($"a text of {length} bytes runs past the end at {_at}");
            var text = Encoding.Latin1.GetString(bytes, _at, length);
            _at += length;
            return text;
        }

        public List<string> Texts()
        {
            var count = Vector();
            var texts = new List<string>(count);
            for (var i = 0; i < count; i++)
                texts.Add(Text());
            return texts;
        }

        public List<bool> Flags()
        {
            var count = Vector();
            var flags = new List<bool>(count);
            for (var i = 0; i < count; i++)
                flags.Add(Object() && Int() != 0);
            return flags;
        }

        private int Vector()
        {
            if (!Object())
                return 0;
            var size = Int();
            Int(); // reserved
            Int(); // capacity
            return size < 0 ? throw new InvalidDataException($"a vector of {size} at {_at}") : size;
        }

        /// <summary>An object's header; false at the end of a shorter file, whose missing fields stay empty.</summary>
        private bool Object()
        {
            if (_at + 5 > bytes.Length)
                return false;
            _at += 5;
            return true;
        }

        private int Int()
        {
            if (_at + 4 > bytes.Length)
                throw new InvalidDataException($"the file ends inside a number at {_at}");
            var value = BitConverter.ToInt32(bytes, _at);
            _at += 4;
            return value;
        }
    }
}
