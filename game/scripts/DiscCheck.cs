using Godot;

namespace Machines.Game;

/// <summary>
/// The check of the player's own Machines CD, for a package that brings every asset along (<see cref="Bundled"/>):
/// before its first match the game wants to see CD 1, in a drive, as a disc image or as a folder of what is on it, or a
/// copy its setup installed (<see cref="GameDisc.Check"/>). Nothing is copied from it. Once it has been seen, the
/// answer is kept in <c>user://settings.cfg</c> (<c>[disc] checked</c>) and the game runs without the CD from then on.
/// A package without the assets imports them from the CD instead (<see cref="ClassicImport"/>) and checks nothing.
/// MACHINES_DISC_CHECK=&lt;drive, folder or disc image&gt; checks it before the menus open, for automated checks.
/// </summary>
public static class DiscCheck
{
    private const string Settings = "user://settings.cfg";

    /// <summary>
    /// The file that marks a package bringing every asset along: <c>classic/bundled.txt</c>, written by
    /// <c>tools/release/build-complete.sh</c>.
    /// </summary>
    public static string MarkerFile => Path.Combine(Paths.Classic, "bundled.txt");

    /// <summary>This package brings the original's assets and every look along, so it checks the CD rather than importing.</summary>
    public static bool Bundled => File.Exists(MarkerFile);

    /// <summary>The CD has been checked on this computer, and what it was (a drive, a disc image's name or a folder).</summary>
    public static string? Checked
    {
        get
        {
            var config = new ConfigFile();
            return config.Load(Settings) == Error.Ok && config.GetValue("disc", "checked", "").AsString() is { Length: > 0 } what ? what : null;
        }
        private set
        {
            var config = new ConfigFile();
            config.Load(Settings);
            config.SetValue("disc", "checked", value ?? "");
            config.Save(Settings);
        }
    }

    /// <summary>This package wants to see the CD before the game starts.</summary>
    public static bool Needed => Bundled && Checked is null;

    /// <summary>Checks <paramref name="place"/> and keeps the answer where it proves the game is the player's.</summary>
    /// <returns>null when it does, otherwise why not, in the player's language.</returns>
    public static string? Run(string place)
    {
        var proof = GameDisc.Check(place);
        if (proof == GameDisc.Proof.Owned)
        {
            Checked = File.Exists(place) ? Path.GetFileName(place) : place.TrimEnd('\\', '/');
            return null;
        }
        return proof switch
        {
            GameDisc.Proof.SecondCd => Lang.T("This is CD 2, with the campaign's films. Put in CD 1."),
            GameDisc.Proof.Different => Lang.T("That is not the Machines CD: its files are not the game's."),
            _ => File.Exists(place)
                ? Lang.T("That file is no disc image of a Machines CD.")
                : Lang.T("No Machines CD there. Pick the CD's drive, a disc image of CD 1 (.iso) or the folder Machines is installed in."),
        };
    }
}
