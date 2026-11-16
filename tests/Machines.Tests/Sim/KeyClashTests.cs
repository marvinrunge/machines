using Machines.Game;
using Xunit;

namespace Machines.Tests.Sim;

/// <summary>
/// How a run's keys settle when the settings put one of them on a key another run's action came with (KeyMap): J was a
/// spare key before it became the Special's, so a player's settings may hold a run's action on it.
/// </summary>
public class KeyClashTests
{
    private static readonly string[] Run = ["Hangar", "SquadFollow", "SquadAttack", "Ability1", "Dash", "Special"];

    private static readonly char[] Spare = ['K', 'L', 'U', 'Y', 'O', 'M'];

    // A run's first keys, as KeyMap has them, with what the settings name over them.
    private static Dictionary<string, char> Keys(params (string Act, char Key)[] named)
    {
        var keys = new Dictionary<string, char> { ["Hangar"] = 'I', ["SquadFollow"] = 'Z', ["SquadAttack"] = 'X', ["Ability1"] = 'G', ["Dash"] = ' ', ["Special"] = 'J' };
        foreach (var (act, key) in named)
            keys[act] = key;
        return keys;
    }

    private static bool Everywhere(string a, string b) => true;

    [Fact]
    public void A_run_action_the_player_put_on_J_keeps_it_and_the_special_moves_to_a_free_spare_key()
    {
        var keys = Keys(("SquadFollow", 'J'), ("Ability1", 'K'));
        var gaveWay = new List<string>();

        Assert.True(KeyClash.Settle(keys, Run, new HashSet<string> { "SquadFollow", "Ability1" }, Everywhere, Spare, gaveWay));

        Assert.Equal('J', keys["SquadFollow"]);
        Assert.Equal('K', keys["Ability1"]);
        Assert.Equal('L', keys["Special"]);
        Assert.Equal(new[] { "Special" }, gaveWay);
        // The rest of the player's run keys are untouched.
        Assert.Equal('I', keys["Hangar"]);
        Assert.Equal('X', keys["SquadAttack"]);
    }

    [Fact]
    public void Keys_without_a_clash_stay_as_they_are()
    {
        var keys = Keys(("SquadFollow", 'U'));
        var gaveWay = new List<string>();

        Assert.True(KeyClash.Settle(keys, Run, new HashSet<string> { "SquadFollow" }, Everywhere, Spare, gaveWay));

        Assert.Equal(Keys(("SquadFollow", 'U')), keys);
        Assert.Empty(gaveWay);
    }

    [Fact]
    public void Two_keys_the_player_named_that_clash_are_not_settled()
    {
        var keys = Keys(("SquadFollow", 'J'), ("SquadAttack", 'J'));

        Assert.False(KeyClash.Settle(keys, Run, new HashSet<string> { "SquadFollow", "SquadAttack" }, Everywhere, Spare, []));
    }

    [Fact]
    public void Actions_that_work_in_different_places_do_not_clash()
    {
        var keys = Keys(("Hangar", 'J'));
        var gaveWay = new List<string>();

        Assert.True(KeyClash.Settle(keys, Run, new HashSet<string> { "Hangar" }, (a, b) => !(a is "Hangar" && b is "Special" || a is "Special" && b is "Hangar"), Spare, gaveWay));

        Assert.Equal('J', keys["Special"]);
        Assert.Empty(gaveWay);
    }

    [Fact]
    public void With_no_spare_key_left_nothing_is_settled()
    {
        var keys = Keys(("SquadFollow", 'J'));
        foreach (var (key, i) in Spare.Select((key, i) => (key, i)))
            keys[$"Other{i}"] = key;

        Assert.False(KeyClash.Settle(keys, Run, new HashSet<string> { "SquadFollow" }, Everywhere, Spare, []));
    }
}
