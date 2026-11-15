using Godot;

namespace Machines.Game;

/// <summary>
/// The way from one screen of the menus to the next, on the board: the page there switches off, its chips flickering
/// out, while a signal runs along every trace of the board, and the next page switches on behind it. The original
/// played a film here (MachGuiStartupScreens's CTX_TRANSITION, <c>flics/&lt;before&gt;&lt;after&gt;.smk</c>); those
/// showed its old boards, which the remake's no longer look like. With the options' Menu Transitions off, the page
/// simply follows.
/// </summary>
public static class MenuTransition
{
    /// <summary>The original's names of its screens: a page shown again on the same screen comes without a transition.</summary>
    public const string Main = "sa", Multiplayer = "sb", Join = "sc", Room = "sd", InGame = "se", Options = "sf", Load = "sg", Save = "sh",
        SinglePlayer = "si", Debriefing = "sj", Briefing = "sk", Scenario = "sl", CampaignPlayers = "sm", Skirmish = "sn", HotKeys = "so",
        Settings = "sp", Statistics = "sq";

    /// <summary>How long the page before takes to switch off, in seconds; the next comes up then.</summary>
    public const double Off = 0.24;

    /// <summary>Whether the page before is switching off: the pages do not move on meanwhile.</summary>
    public static bool Playing { get; private set; }

    // Automated checks go straight to what they look at.
    private static bool Automated => new[] { "MACHINES_SCREENSHOT", "MACHINES_MENU_SCREENSHOT", "MACHINES_MAP", "MACHINES_MP", "MACHINES_MENU_START" }
        .Any(name => System.Environment.GetEnvironmentVariable(name) is { Length: > 0 });

    /// <summary>Goes from one screen to the other on <paramref name="stage"/>; <paramref name="then"/> puts up the next page.</summary>
    public static void Between(MenuStage stage, string from, string to, Action then)
    {
        if (from == to || !GameOptions.Transitions || Automated || stage.Page is not { } page || !GodotObject.IsInstanceValid(page))
        {
            then();
            return;
        }
        Playing = true;
        stage.SwitchOff(Off);
        stage.Board?.Surge();
        stage.GetTree().CreateTimer(Off).Timeout += () =>
        {
            Playing = false;
            then();
        };
    }
}
