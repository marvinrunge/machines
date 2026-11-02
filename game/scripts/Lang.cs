using Godot;

namespace Machines.Game;

/// <summary>
/// The language of the remake's own words, English or German; the original's words come from the player's game copy,
/// in its language. At first it is the system's, then the player's choice, kept in <c>user://settings.cfg</c> as
/// <c>[display] language</c>. The German words are in <c>res://lang/de.po</c>, keyed by the English ones: labels,
/// buttons and tooltips show them by themselves, and text the remake draws or puts together goes through
/// <see cref="T(string)"/>. MACHINES_LANG=de or en sets it for one run, unsaved (screenshots, checks).
/// </summary>
public static class Lang
{
    private const string Settings = "user://settings.cfg";

    /// <summary>The languages, by code, and their names as each calls itself.</summary>
    public static readonly string[] Codes = ["en", "de"];

    public static readonly string[] Names = ["English", "Deutsch"];

    private static string? _code;
    private static bool _loaded;

    /// <summary>The language's code: en or de.</summary>
    public static string Code
    {
        get
        {
            if (_code is not null)
                return _code;
            var config = new ConfigFile();
            var kept = config.Load(Settings) == Error.Ok ? (string)config.GetValue("display", "language", "") : "";
            var asked = System.Environment.GetEnvironmentVariable("MACHINES_LANG") ?? "";
            _code = Codes.Contains(asked) ? asked : Codes.Contains(kept) ? kept : OS.GetLocaleLanguage() == "de" ? "de" : "en";
            return _code;
        }
        set
        {
            _code = Codes.Contains(value) ? value : "en";
            var config = new ConfigFile();
            config.Load(Settings);
            config.SetValue("display", "language", _code);
            config.Save(Settings);
            Apply();
        }
    }

    /// <summary>Shows the remake's words in the language from now on; the pages built after show them.</summary>
    public static void Apply()
    {
        if (!_loaded)
        {
            _loaded = true;
            if (ResourceLoader.Exists("res://lang/de.po"))
                TranslationServer.AddTranslation(GD.Load<Translation>("res://lang/de.po"));
        }
        // Godot would take the system's language by itself; the player's choice decides.
        TranslationServer.SetLocale(Code);
    }

    /// <summary>The words in the language, or as they are where there is no translation.</summary>
    public static string T(string text) => text.Length == 0 ? text : TranslationServer.Translate(text);

    /// <summary>A sentence with values in it: <paramref name="format"/> in the language, then {0}, {1} ... filled in.</summary>
    public static string T(string format, params object?[] values) => string.Format(T(format), values);

    /// <summary>
    /// A text of the original's string table, <paramref name="text"/> in the game copy's language, whose English is
    /// <paramref name="english"/>: in another language than English the translation of that where there is one.
    /// </summary>
    public static string Original(string text, string english) =>
        Code != "en" && T(english) is var translated && translated != english ? translated : text;
}
