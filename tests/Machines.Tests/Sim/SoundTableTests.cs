using Machines.Import.Audio;
using Machines.Sim;
using Machines.Sim.Presentation;

namespace Machines.Tests.Sim;

/// <summary>Every weapon, machine and report the game plays a sound for names one the original defines.</summary>
public class SoundTableTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));
    private static readonly Lazy<HashSet<string>> Sounds = new(() =>
        SoundDefinitionParser.Load(RepoPaths.OriginalData("sounds/snddef64.dat")).Select(s => s.Id).ToHashSet());
    private static readonly Lazy<HashSet<string>> Reports = new(() =>
        VoiceMailParser.Load(RepoPaths.OriginalData("sounds/vemail/vemail.dat")).Select(m => m.Id).ToHashSet());

    [Fact]
    public void Every_weapon_has_a_defined_firing_or_blast_sound()
    {
        // The virus is the one weapon the definitions give no sound (SID_VIRUS is not in snddef64.dat).
        var silent = DataLoader.Value.Parm.Weapons
            .Select(w => w.TypeName)
            .Where(w => w != "VIRUS")
            .Where(w => !Sounds.Value.Contains(SoundTable.FiringSound(w) ?? SoundTable.BlastSound(w) ?? "none"))
            .ToList();
        Assert.Empty(silent);
    }

    [Fact]
    public void Every_machine_level_has_a_defined_engine_sound()
    {
        var missing = DataLoader.Value.Parm.Machines
            .SelectMany(spec => spec.Levels.Select(level => (spec, level.HardwareLevel)))
            .Where(m => SoundTable.LocomotionSounds(m.spec.Type, m.spec.SubType, m.HardwareLevel).Idle is not { } idle || !Sounds.Value.Contains(idle))
            .Select(m => $"{m.spec.TypeName} {m.HardwareLevel}")
            .ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void Every_machine_type_answers_when_selected()
    {
        var mute = DataLoader.Value.Parm.Machines
            .Where(spec => SoundTable.VoiceOf(spec.Type, spec.SubType) is not { } voice
                || !Reports.Value.Any(id => id.StartsWith($"VID_{voice}_SELECTED", StringComparison.Ordinal)))
            .Select(spec => spec.TypeName)
            .ToList();
        Assert.Empty(mute);
    }

    [Fact]
    public void Explosions_and_hits_name_defined_sounds()
    {
        for (var variant = 1; variant <= 6; variant++)
            Assert.Contains(SoundTable.ExplosionSound(false, 5, variant), Sounds.Value);
        Assert.Contains(SoundTable.ExplosionSound(true, 50, 1), Sounds.Value);
        Assert.Contains(SoundTable.ExplosionSound(true, 20, 1), Sounds.Value);
        for (var variant = 1; variant <= 4; variant++)
            Assert.Contains(SoundTable.HitSound("MULTI_LAUNCHER1", variant)!, Sounds.Value);
        Assert.Contains(SoundTable.HitSound("BOLTER", 1)!, Sounds.Value);
        Assert.Contains(SoundTable.HitSound("PULSE_RIFLE", 1)!, Sounds.Value);
    }
}
