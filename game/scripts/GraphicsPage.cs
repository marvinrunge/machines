using Godot;

namespace Machines.Game;

/// <summary>
/// The look part by part (<see cref="GraphicsParts"/>), opened from the options: a whole look at the top, then the
/// ground, the machines and buildings, the lighting and sky, the effects, the weather and the trees and rocks, each
/// with a line on what its choice shows. Changing a part makes the look custom, unless the parts then make one of
/// the others; choosing a look sets every part. It is the same before a match and over one, where the new look shows
/// from the next match.
/// </summary>
public partial class GraphicsPage(Action back, bool inGame) : MenuPage(back)
{
    /// <summary>A part's row: its choice, the line under it, which choice the parts hold and what each shows.</summary>
    private sealed record Part(ChipChoice Choice, Label Note, Func<GraphicsParts, int> Of, string[] Notes);

    private readonly List<Part> _parts = [];
    private ChipChoice _look = null!;
    private Label _lookNote = null!;

    protected override void Build()
    {
        var page = Column(Gap);
        AddChild(page);
        page.AddChild(Heading("Graphics"));

        var chip = new ChipPanel { Padding = 28, SizeFlagsVertical = SizeFlags.ShrinkBegin };
        var rows = Column(18);
        chip.AddChild(rows);
        page.AddChild(chip);

        _look = new ChipChoice(Enum.GetValues<Look>().Select(GraphicsSettings.Name), (int)GraphicsSettings.Look);
        _look.ItemSelected += i =>
        {
            GraphicsSettings.Look = (Look)i;
            Refresh();
        };
        _lookNote = Note();
        rows.AddChild(Row("Look", _look, _lookNote));
        rows.AddChild(new HSeparator());

        var sharp = HdTextures.Made ? "" : " Import the original again in the options to make them.";
        rows.AddChild(Choice("Ground", ["Original", "Sharpened", "Painted"], p => (int)p.Terrain, (p, i) => p with { Terrain = (TerrainLook)i },
        [
            "The original's ground and its pictures.",
            "The original's ground, its pictures upscaled with relief and roughness." + sharp,
            "A finer shape that casts shadows; the natural planets painted with photographed ground.",
        ]));
        rows.AddChild(Choice("Machines & buildings", ["Original", "Overhauled", "HD"], p => (int)p.Models, (p, i) => p with { Models = (ModelLook)i },
        [
            "The original's models and textures.",
            "The original's models with rounded edges, baked shading, sharper textures and a little sheen.",
            "The remade models in the original's rigs; those not remade yet show overhauled.",
        ]));
        rows.AddChild(Choice("Lighting & sky", ["Original", "Remake"], p => p.Lighting ? 1 : 0, (p, i) => p with { Lighting = i == 1 },
        [
            "The original's light.",
            "Tone mapping, glow, ambient occlusion, soft shadows, photographed skies and lamps at night.",
        ]));
        rows.AddChild(Choice("Effects", ["Original", "Remake"], p => p.Effects ? 1 : 0, (p, i) => p with { Effects = i == 1 },
        [
            "The original's explosions and shots.",
            "Explosions, machines flying apart, smoke, tracks, scorch marks and the light of fire.",
        ]));
        rows.AddChild(Choice("Weather", ["Off", "On"], p => p.Weather ? 1 : 0, (p, i) => p with { Weather = i == 1 },
        [
            "No weather, as in the original.",
            "Sand, snow, rain or ash as the planet has them, and storms now and then.",
        ]));
        rows.AddChild(Choice("Trees & rocks", ["Original", "Remake"], p => p.Scenery ? 1 : 0, (p, i) => p with { Scenery = i == 1 },
        [
            "The original's trees and rocks.",
            "The remade trees and rocks; on the painted ground grass and stones grow too.",
        ]));

        if (inGame)
            page.AddChild(MenuTheme.Paragraph("The new look shows from the next match.", 22, MenuTheme.Text));
        var backButton = Button("Back", Back);
        backButton.CustomMinimumSize = new Vector2(400, 76);
        page.AddChild(Line(Gap, backButton));
        Refresh();
        SwitchOn();
        Focus(_look);
    }

    /// <summary>A part's row; changing it changes that part alone.</summary>
    private HBoxContainer Choice(string name, string[] items, Func<GraphicsParts, int> of, Func<GraphicsParts, int, GraphicsParts> with, string[] notes)
    {
        var choice = new ChipChoice(items, of(GraphicsSettings.Parts));
        choice.ItemSelected += i =>
        {
            GraphicsSettings.Parts = with(GraphicsSettings.Parts, i);
            Refresh();
        };
        var note = Note();
        _parts.Add(new Part(choice, note, of, notes));
        return Row(name, choice, note);
    }

    private static HBoxContainer Row(string name, ChipChoice choice, Label note)
    {
        choice.CustomMinimumSize = new Vector2(320, 0);
        choice.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        return Setting(name, Line(24, choice, note), 260);
    }

    private static Label Note()
    {
        var note = MenuTheme.Paragraph("", 20, MenuTheme.Dim);
        note.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        return note;
    }

    /// <summary>Shows every choice as the parts now are.</summary>
    private void Refresh()
    {
        var parts = GraphicsSettings.Parts;
        _look.Selected = (int)parts.Look;
        _lookNote.Text = parts.Look switch
        {
            Look.Classic => "Everything as the original had it.",
            Look.Overhauled => "The original sharpened, with the remake's lighting, effects, weather, trees and rocks.",
            Look.Hd => "The remade models on the painted ground, with the rest of the remake.",
            _ => "Each part as chosen below.",
        };
        foreach (var part in _parts)
        {
            part.Choice.Selected = part.Of(parts);
            part.Note.Text = part.Notes[part.Choice.Selected];
        }
    }
}
