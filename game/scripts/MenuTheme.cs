using Godot;

namespace Machines.Game;

/// <summary>
/// The look of the remake's menus (<see cref="MenuStage"/>): the font, its sizes and the original menus' colours,
/// white and yellow on dark green under green light, and a theme that gives Godot's own controls the same look:
/// buttons and panels as chips, lists with the original's green bar under the picked entry (gui/menu/longglow.bmp),
/// switches as LEDs. Sizes are menu pixels: the menus are laid out for a window 1080 of them high and scaled to the
/// real one, so the font is drawn from a distance field and the theme's little pictures at twice their size.
/// </summary>
public static class MenuTheme
{
    public static readonly Color Text = new(0.86f, 0.9f, 0.8f);
    public static readonly Color Bright = new(1f, 0.85f, 0.36f);
    public static readonly Color Dim = new(0.4f, 0.47f, 0.38f);
    /// <summary>Text on a lit chip (a button under the mouse): dark on the green light.</summary>
    public static readonly Color OnLight = new(0.03f, 0.13f, 0.03f);
    /// <summary>The screens' own green, darker than a chip's, for fields and grooves set into them.</summary>
    public static readonly Color Well = new(0.012f, 0.045f, 0.018f);
    public static readonly Color Edge = new(0.2f, 0.36f, 0.18f);
    public static readonly Color Glow = new(0.42f, 0.98f, 0.28f);

    private const string Settings = "user://settings.cfg";

    private static Font? _font;

    /// <summary>Saira (Open Font License, game/assets/fonts/saira), a variable font, at its semibold weight.</summary>
    public static Font Font => _font ??= LoadFont("res://assets/fonts/saira/Saira.ttf", 600);

    private static Font LoadFont(string path, int weight)
    {
        var file = GD.Load<FontFile>(path);
        file.MultichannelSignedDistanceField = true;
        file.MsdfSize = 64;
        var tag = TextServerManager.GetPrimaryInterface().NameToTag("wght");
        return new FontVariation { BaseFont = file, VariationOpentype = new Godot.Collections.Dictionary { [tag] = weight } };
    }

    /// <summary>The player's interface size, on top of the window's: 1 lays the menus out for the window's height.</summary>
    public static float UiScale
    {
        get
        {
            var config = new ConfigFile();
            return config.Load(Settings) == Error.Ok ? (float)config.GetValue("interface", "scale", 1f) : 1f;
        }
        set
        {
            var config = new ConfigFile();
            config.Load(Settings);
            config.SetValue("interface", "scale", value);
            config.Save(Settings);
        }
    }

    public static Theme Build()
    {
        var theme = new Theme { DefaultFont = Font, DefaultFontSize = 24 };
        theme.SetColor("font_color", "Label", Text);
        theme.SetColor("default_color", "RichTextLabel", Text);

        // Godot's own buttons: chips, lit under the mouse, pressed or with the keyboard on them.
        theme.SetStylebox("normal", "Button", new ChipStyle().WithPadding(24, 8));
        theme.SetStylebox("hover", "Button", new ChipStyle { Light = 1 }.WithPadding(24, 8));
        theme.SetStylebox("pressed", "Button", new ChipStyle { Light = 0.8f }.WithPadding(24, 8));
        theme.SetStylebox("hover_pressed", "Button", new ChipStyle { Light = 1 }.WithPadding(24, 8));
        theme.SetStylebox("disabled", "Button", new ChipStyle { Dim = true }.WithPadding(24, 8));
        theme.SetStylebox("focus", "Button", new ChipStyle { Light = 1, GlowOnly = true });
        theme.SetColor("font_color", "Button", Text);
        foreach (var state in new[] { "font_hover_color", "font_pressed_color", "font_hover_pressed_color", "font_focus_color" })
            theme.SetColor(state, "Button", OnLight);
        theme.SetColor("font_disabled_color", "Button", Dim);

        // Switches: an LED and the name beside it, the name yellow under the mouse.
        foreach (var type in new[] { "CheckBox", "CheckButton" })
        {
            var plain = new StyleBoxEmpty { ContentMarginLeft = 4, ContentMarginRight = 4, ContentMarginTop = 4, ContentMarginBottom = 4 };
            foreach (var state in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled" })
                theme.SetStylebox(state, type, plain);
            theme.SetStylebox("focus", type, new StyleBoxFlat
            {
                DrawCenter = false, BorderColor = Bright, BorderWidthBottom = 2,
                ContentMarginLeft = 4, ContentMarginRight = 4, ContentMarginTop = 4, ContentMarginBottom = 4,
            });
            theme.SetColor("font_color", type, Text);
            theme.SetColor("font_pressed_color", type, Text);
            foreach (var state in new[] { "font_hover_color", "font_hover_pressed_color", "font_focus_color" })
                theme.SetColor(state, type, Bright);
            theme.SetColor("font_disabled_color", type, Dim);
            theme.SetConstant("h_separation", type, 14);
            var on = Led(true);
            var off = Led(false);
            foreach (var name in new[] { "checked", "radio_checked", "checked_mirrored" })
            {
                theme.SetIcon(name, type, on);
                theme.SetIcon(name + "_disabled", type, off);
            }
            foreach (var name in new[] { "unchecked", "radio_unchecked", "unchecked_mirrored" })
            {
                theme.SetIcon(name, type, off);
                theme.SetIcon(name + "_disabled", type, off);
            }
        }

        // Fields to type in: set into the screen, the frame lit while typing.
        var field = Inset(Well, Edge);
        theme.SetStylebox("normal", "LineEdit", field);
        theme.SetStylebox("read_only", "LineEdit", Inset(Well, Edge.Darkened(0.4f)));
        theme.SetStylebox("focus", "LineEdit", new StyleBoxFlat
        {
            DrawCenter = false, BorderColor = Glow, BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
        });
        theme.SetColor("font_color", "LineEdit", Text);
        theme.SetColor("font_placeholder_color", "LineEdit", Dim);
        theme.SetColor("font_selected_color", "LineEdit", OnLight);
        theme.SetColor("selection_color", "LineEdit", new Color(Glow, 0.75f));
        theme.SetColor("caret_color", "LineEdit", Bright);
        theme.SetConstant("caret_width", "LineEdit", 2);

        // Lists: plain on the chip they lie on, the picked entry on the green bar, the keyboard's entry outlined.
        var bar = new StyleBoxTexture
        {
            Texture = new GradientTexture2D
            {
                Width = 4,
                Height = 64,
                FillFrom = new Vector2(0, 0),
                FillTo = new Vector2(0, 1),
                Gradient = new Gradient
                {
                    Offsets = [0f, 0.45f, 1f],
                    Colors = [new Color(0.62f, 1f, 0.46f), new Color(0.36f, 0.92f, 0.22f), new Color(0.13f, 0.5f, 0.09f)],
                },
            },
            ContentMarginLeft = 8, ContentMarginRight = 8,
        };
        var hovered = new StyleBoxFlat { BgColor = new Color(Glow, 0.13f) };
        var cursor = new StyleBoxFlat { DrawCenter = false, BorderColor = Bright, BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2 };
        foreach (var type in new[] { "Tree", "ItemList" })
        {
            theme.SetStylebox("panel", type, new StyleBoxEmpty());
            theme.SetStylebox("focus", type, new StyleBoxEmpty());
            theme.SetStylebox("selected", type, bar);
            theme.SetStylebox("selected_focus", type, bar);
            theme.SetStylebox("hovered", type, hovered);
            theme.SetStylebox("hovered_selected", type, bar);
            theme.SetStylebox("hovered_selected_focus", type, bar);
            theme.SetStylebox("cursor", type, cursor);
            theme.SetStylebox("cursor_unfocused", type, new StyleBoxEmpty());
            theme.SetFontSize("font_size", type, 22);
            theme.SetColor("font_color", type, Text);
            theme.SetColor("font_hovered_color", type, Bright);
            theme.SetColor("font_selected_color", type, OnLight);
            theme.SetColor("font_hovered_selected_color", type, OnLight);
            theme.SetColor("guide_color", type, Colors.Transparent);
            theme.SetConstant("v_separation", type, 8);
        }
        theme.SetConstant("draw_guides", "Tree", 0);
        theme.SetConstant("draw_relationship_lines", "Tree", 0);
        theme.SetConstant("item_margin", "Tree", 26);
        theme.SetConstant("inner_item_margin_left", "Tree", 6);
        theme.SetIcon("arrow", "Tree", Arrow(Vector2.Down));
        theme.SetIcon("arrow_collapsed", "Tree", Arrow(Vector2.Right));
        theme.SetIcon("arrow_collapsed_mirrored", "Tree", Arrow(Vector2.Left));

        // Sliders: a groove in the screen, green as far as the value, an LED to drag.
        var groove = Inset(Well, Edge, 6);
        theme.SetStylebox("slider", "HSlider", groove);
        theme.SetStylebox("grabber_area", "HSlider", Filled(new Color(0.2f, 0.66f, 0.14f), 6));
        theme.SetStylebox("grabber_area_highlight", "HSlider", Filled(new Color(0.32f, 0.86f, 0.22f), 6));
        theme.SetIcon("grabber", "HSlider", Led(true));
        theme.SetIcon("grabber_highlight", "HSlider", Led(true, hot: true));
        theme.SetIcon("grabber_disabled", "HSlider", Led(false));
        theme.SetIcon("tick", "HSlider", new ImageTexture());

        foreach (var type in new[] { "VScrollBar", "HScrollBar" })
        {
            theme.SetStylebox("scroll", type, Inset(Well, Edge.Darkened(0.3f), 5));
            theme.SetStylebox("scroll_focus", type, Inset(Well, Edge.Darkened(0.3f), 5));
            theme.SetStylebox("grabber", type, Filled(new Color(0.16f, 0.44f, 0.12f), 5));
            theme.SetStylebox("grabber_highlight", type, Filled(new Color(0.28f, 0.72f, 0.2f), 5));
            theme.SetStylebox("grabber_pressed", type, Filled(new Color(0.38f, 0.9f, 0.26f), 5));
        }
        theme.SetStylebox("panel", "ScrollContainer", new StyleBoxEmpty());
        theme.SetStylebox("panel", "PanelContainer", new ChipStyle { ArtScale = 0.5f }.WithPadding(18, 18));
        theme.SetStylebox("panel", "Panel", new ChipStyle { ArtScale = 0.5f });

        FitPopups(theme, 1);
        return theme;
    }

    /// <summary>
    /// Sizes the theme's popups (tooltips, drop-down lists) for the menus' scale: they open in windows of their own,
    /// which the stage's scale does not reach, so their sizes are scaled here instead.
    /// </summary>
    public static void FitPopups(Theme theme, float scale)
    {
        int Px(float size) => Math.Max(1, (int)MathF.Round(size * scale));
        StyleBoxFlat Box(Color fill, Color border, float across, float down) => new()
        {
            BgColor = fill, BorderColor = border,
            BorderWidthLeft = Px(2), BorderWidthRight = Px(2), BorderWidthTop = Px(2), BorderWidthBottom = Px(2),
            CornerRadiusTopLeft = Px(4), CornerRadiusTopRight = Px(4), CornerRadiusBottomLeft = Px(4), CornerRadiusBottomRight = Px(4),
            ContentMarginLeft = Px(across), ContentMarginRight = Px(across), ContentMarginTop = Px(down), ContentMarginBottom = Px(down),
        };
        theme.SetStylebox("panel", "TooltipPanel", Box(new Color(0.03f, 0.09f, 0.035f, 0.96f), Edge, 12, 8));
        theme.SetColor("font_color", "TooltipLabel", Text);
        theme.SetFontSize("font_size", "TooltipLabel", Px(20));
        theme.SetStylebox("panel", "PopupMenu", Box(new Color(0.03f, 0.09f, 0.035f, 0.98f), Edge, 6, 6));
        theme.SetStylebox("hover", "PopupMenu", Box(new Color(0.3f, 0.85f, 0.2f), new Color(0.3f, 0.85f, 0.2f), 6, 2));
        theme.SetColor("font_color", "PopupMenu", Text);
        theme.SetColor("font_hover_color", "PopupMenu", OnLight);
        theme.SetColor("font_disabled_color", "PopupMenu", Dim);
        theme.SetFontSize("font_size", "PopupMenu", Px(22));
        theme.SetConstant("v_separation", "PopupMenu", Px(8));
    }

    public static Label Label(string text, int size, Color colour, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var label = new Label { Text = text, HorizontalAlignment = align };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", colour);
        return label;
    }

    /// <summary>A label that wraps its words to the width it is given.</summary>
    public static Label Paragraph(string text, int size, Color colour)
    {
        var label = Label(text, size, colour);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        label.CustomMinimumSize = new Vector2(80, 0);
        return label;
    }

    private static StyleBoxFlat Inset(Color fill, Color border, float thickness = 0)
    {
        var box = new StyleBoxFlat
        {
            BgColor = fill, BorderColor = border,
            BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
            ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 8, ContentMarginBottom = 8,
        };
        if (thickness > 0)
            box.ContentMarginLeft = box.ContentMarginRight = box.ContentMarginTop = box.ContentMarginBottom = thickness;
        return box;
    }

    private static StyleBoxFlat Filled(Color fill, float thickness) => new()
    {
        BgColor = fill,
        CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
        ContentMarginLeft = thickness, ContentMarginRight = thickness, ContentMarginTop = thickness, ContentMarginBottom = thickness,
    };

    // ---- the theme's little pictures, drawn at twice their size for sharpness at any scale ----------

    private const int IconSize = 30;

    /// <summary>A round LED in a dark ring: lit green, or dark.</summary>
    private static Texture2D Led(bool on, bool hot = false) => Icon(IconSize, p =>
    {
        var r = p.Length();
        Color lens = on
            ? new Color(0.25f, 0.85f, 0.16f).Lerp(new Color(0.9f, 1f, 0.75f), Mathf.Clamp(1 - r / 0.42f, 0, 1) * (hot ? 1 : 0.8f))
            : new Color(0.06f, 0.16f, 0.07f).Lerp(new Color(0.16f, 0.3f, 0.16f), Mathf.Clamp(1 - (p - new Vector2(-0.15f, -0.15f)).Length() / 0.3f, 0, 1));
        if (r < 0.62f)
            return lens;
        if (r < 0.8f)
            return new Color(0.02f, 0.05f, 0.025f);
        // A halo round a lit one.
        return on ? new Color(Glow, Mathf.Clamp(1 - (r - 0.8f) / 0.2f, 0, 1) * (hot ? 0.6f : 0.35f)) : Colors.Transparent;
    });

    /// <summary>A small solid triangle pointing <paramref name="way"/>, for the map tree's branches.</summary>
    private static Texture2D Arrow(Vector2 way) => Icon(20, p =>
    {
        // In the arrow's own frame: x along the way it points, y across.
        var along = p.Dot(way);
        var across = Mathf.Abs(p.Dot(way.Orthogonal()));
        return along < 0.45f && along > -0.4f && across < (0.45f - along) * 0.62f ? Text : Colors.Transparent;
    });

    /// <summary>
    /// A picture <paramref name="size"/> menu pixels square, drawn by a function of the point (-1 to 1 across) with
    /// four by four samples a pixel at twice that size, and shown at <paramref name="size"/>.
    /// </summary>
    private static Texture2D Icon(int size, Func<Vector2, Color> shade)
    {
        var pixels = size * 2;
        var image = Image.CreateEmpty(pixels, pixels, false, Image.Format.Rgba8);
        for (var y = 0; y < pixels; y++)
        for (var x = 0; x < pixels; x++)
        {
            var sum = new Color(0, 0, 0, 0);
            for (var sy = 0; sy < 4; sy++)
            for (var sx = 0; sx < 4; sx++)
            {
                var c = shade(new Vector2((x + (sx + 0.5f) / 4) / pixels * 2 - 1, (y + (sy + 0.5f) / 4) / pixels * 2 - 1));
                sum += new Color(c.R * c.A, c.G * c.A, c.B * c.A, c.A);
            }
            var a = sum.A / 16;
            image.SetPixel(x, y, a > 0 ? new Color(sum.R / sum.A, sum.G / sum.A, sum.B / sum.A, a) : Colors.Transparent);
        }
        image.GenerateMipmaps();
        var texture = ImageTexture.CreateFromImage(image);
        texture.SetSizeOverride(new Vector2I(size, size));
        return texture;
    }
}
