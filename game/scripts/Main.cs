using Godot;
using Machines.Import.Parm;

namespace Machines.Game;

/// <summary>Start scene for M0: proves the Godot project can load the original data through the importer.</summary>
public partial class Main : Node3D
{
    public override void _Ready()
    {
        var status = GetNode<Label>("Status");

        // During development the data sits next to the Godot project; exports will bundle it later.
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            ProjectSettings.GlobalizePath("res://"), "..", "data", "original", "parmdata.dat"));

        try
        {
            var data = ParmDataParser.Load(path);
            var grunt = data.Machine("AGGRESSOR GRUNT").Level(1);
            status.Text =
                $"Loaded {path}\n" +
                $"{data.Machines.Count} machine types, {data.Constructions.Count} buildings, {data.Weapons.Count} weapons\n" +
                $"Scout: {grunt.Cost} BMU, {grunt.HitPoints} HP, {grunt.Armour} armour";
            GD.Print(status.Text);
        }
        catch (Exception e) when (e is System.IO.IOException or ParmFormatException)
        {
            status.Text = $"Could not load {path}:\n{e.Message}";
            GD.PushError(e.ToString());
        }
    }
}
