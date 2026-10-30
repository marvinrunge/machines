using Godot;

namespace Machines.Game;

/// <summary>
/// The circuit board the menus lie on (tools/blender/menu-art.py): the rendered tile repeated over the whole window
/// with its signal pulses (game/assets/menu/menu_board.gdshader), and the parts that sit on it, a fan turning in the
/// corner, cables coming in from the edge, capacitors and a coil. The parts hold to the window's edges, so whatever
/// its shape they stay in view and the panels lie on the board between them.
/// </summary>
public partial class MenuBoard : Control
{
    /// <summary>Menu pixels per pixel of the board tile.</summary>
    public const float TileScale = 2f / 3;

    // The parts are rendered at two pixels per pixel of the board.
    private const float PartScale = TileScale / 2;

    private readonly List<(Control Part, Vector2 Anchor, Vector2 Align, Vector2 Offset)> _parts = [];
    private ShaderMaterial _board = null!;
    private Control? _blades;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        TextureFilter = TextureFilterEnum.LinearWithMipmaps;
        _board = new ShaderMaterial { Shader = GD.Load<Shader>("res://assets/menu/menu_board.gdshader") };
        _board.SetShaderParameter("board", GD.Load<Texture2D>("res://assets/menu/board.png"));
        _board.SetShaderParameter("traces", GD.Load<Texture2D>("res://assets/menu/board_traces.png"));
        _board.SetShaderParameter("tile", 2048 * TileScale);
        _board.SetShaderParameter("tint", new Color(0.82f, 1.08f, 0.8f));
        var surface = new ColorRect { Material = _board, MouseFilter = MouseFilterEnum.Ignore };
        surface.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(surface);

        Part("ribbon", new(0, 0.1f), new(0, 0.5f), Vector2.Zero);
        Part("cable", new(0, 1), new(0, 1), new(0, -40));
        Part("capacitor_axial", new(0.5f, 0), new(0.5f, 0), new(-140, 6));
        Part("capacitor_radial", new(1, 0), new(1, 0), new(-30, 14));
        Part("coil", new(0.5f, 1), new(0.5f, 1), new(160, 4));
        Part("fan", new(1, 1), new(1, 1), new(-20, -10));
        _blades = Part("fan_blades", new(1, 1), new(1, 1), new(-20, -10));
        _blades.PivotOffset = _blades.Size / 2;
        Resized += Layout;
        Layout();
    }

    public override void _Process(double delta)
    {
        if (_blades is not null)
            _blades.Rotation += (float)delta * 5;
    }

    private Control Part(string name, Vector2 anchor, Vector2 align, Vector2 offset)
    {
        var texture = GD.Load<Texture2D>($"res://assets/menu/{name}.png");
        var part = new TextureRect
        {
            Texture = texture,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
            Size = texture.GetSize() * PartScale,
        };
        AddChild(part);
        _parts.Add((part, anchor, align, offset));
        return part;
    }

    private void Layout()
    {
        _board.SetShaderParameter("size", Size);
        foreach (var (part, anchor, align, offset) in _parts)
            part.Position = anchor * Size - align * part.Size + offset;
    }
}
