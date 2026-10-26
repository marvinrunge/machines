namespace Machines.Sim.Combat;

/// <summary>Where a weapon sits on a machine or building.</summary>
public enum Mounting
{
    Left,
    Right,
    Top,
}

/// <summary>
/// Splits a weapon combo name into its mounted weapons, e.g.
/// <c>LR_HEAVY_BOLTER1_X2</c> → left and right <c>HEAVY_BOLTER1</c>,
/// <c>L_PULSE_RIFLE_R_SUPERCHARGE_ADVANCED</c> → left <c>PULSE_RIFLE</c>, right <c>SUPERCHARGE_ADVANCED</c>.
/// </summary>
public static class WeaponCombos
{
    private static readonly Dictionary<string, Mounting[]> Mounts = new()
    {
        ["L"] = [Mounting.Left],
        ["R"] = [Mounting.Right],
        ["T"] = [Mounting.Top],
        ["LR"] = [Mounting.Left, Mounting.Right],
        ["LRT"] = [Mounting.Left, Mounting.Right, Mounting.Top],
    };

    public static IReadOnlyList<(Mounting Mounting, string Weapon)> Split(string combo)
    {
        var result = new List<(Mounting, string)>();
        Mounting[]? mounts = null;
        var weapon = new List<string>();

        void Flush()
        {
            if (mounts is null)
                return;
            if (weapon.Count == 0)
                throw new FormatException($"weapon combo '{combo}' has a mounting without a weapon");
            foreach (var mount in mounts)
                result.Add((mount, Canonical(string.Join('_', weapon))));
            weapon.Clear();
        }

        foreach (var token in combo.Split('_'))
        {
            if (Mounts.TryGetValue(token, out var next))
            {
                Flush();
                mounts = next;
            }
            else if (token.Length >= 1 && token[0] == 'X' && token[1..].All(char.IsDigit))
            {
                // _X2/_X3 repeat what the mounting already says; a bare _X (a typo in the data) is ignored too.
            }
            else if (mounts is null)
            {
                throw new FormatException($"weapon combo '{combo}' does not start with a mounting");
            }
            else
            {
                weapon.Add(token);
            }
        }
        Flush();
        return result;
    }

    /// <summary>Combo names that differ from the weapon's parmdata name (MachLogScenario's combo table).</summary>
    private static string Canonical(string weapon) => weapon switch
    {
        "TREACHERY" => "ORB_OF_TREACHERY",
        _ => weapon,
    };
}
