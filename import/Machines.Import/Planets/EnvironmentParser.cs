using System.Globalization;
using System.Numerics;
using System.Text;
using Machines.Import.Model;
using Machines.Import.Parm;

namespace Machines.Import.Planets;

/// <summary>
/// Reads a planet environment (<c>.env</c>), the block format the original read with its own lexer:
/// <code>
/// colour table: "sun colours" { 3.75 (1.00, 0.10, 0.00) ... }
/// orbit: "sun orbit" { min elevation: -2.5  radius: 80000 ... }
/// dynamic sky: { mesh file: "..."  colour table @ 20: "sky colours 20" }
/// </code>
/// </summary>
public static class EnvironmentParser
{
    public static PlanetEnvironment Load(string path) => Planet.WithFileName(path, () => Parse(File.ReadAllText(path)));

    public static PlanetEnvironment Parse(string text)
    {
        var blocks = new BlockReader(Tokenize(text)).ReadAll();

        var colourTables = new Dictionary<string, IReadOnlyList<ColourKey>>();
        var orbits = new Dictionary<string, Orbit>();
        var satellites = new List<Satellite>();
        Fog? fog = null;
        var other = new List<EnvBlock>();

        foreach (var block in blocks)
        {
            switch (block.Kind)
            {
                case "colour table":
                    var name = block.RequireName();
                    if (block.Properties.Count > 0)
                        throw new ParmFormatException($"colour table \"{name}\" may only hold rows", block.LineNumber);
                    var keys = block.Rows.Select(r => r.Tuple.Count switch
                    {
                        3 => new ColourKey(r.Key, new Vector4(r.Tuple[0], r.Tuple[1], r.Tuple[2], 1)),
                        4 => new ColourKey(r.Key, new Vector4(r.Tuple[0], r.Tuple[1], r.Tuple[2], r.Tuple[3])),
                        _ => throw new ParmFormatException("a colour needs 3 or 4 values", r.LineNumber),
                    }).ToList();
                    if (!colourTables.TryAdd(name, keys))
                        throw new ParmFormatException($"colour table \"{name}\" is defined twice", block.LineNumber);
                    break;

                case "orbit":
                    block.RequireNoRows();
                    if (!orbits.TryAdd(block.RequireName(), new Orbit(
                            block.Number("min elevation"), block.Number("max elevation"), block.Number("radius"),
                            block.Number("start time"), block.Number("period"), block.OptionalNumber("set heading"))))
                        throw new ParmFormatException($"orbit \"{block.Name}\" is defined twice", block.LineNumber);
                    break;

                case "satellite":
                    block.RequireNoRows();
                    satellites.Add(new Satellite(
                        block.RequireName(), block.Text("orbit"), block.OptionalText("mesh file"),
                        block.OptionalText("mesh colour table"), block.OptionalText("dir colour table"), block.OptionalText("ambient colour table")));
                    break;

                case "fog":
                    block.RequireNoRows();
                    if (fog is not null)
                        throw new ParmFormatException("fog is defined twice", block.LineNumber);
                    fog = new Fog(block.Number("max visibility"), block.Text("colour table"), block.OptionalText("controller"));
                    break;

                case "dynamic sky" or "uniform sky" or "stars" or "skyline" or "nvg" or "day phase":
                    block.RequireNoRows();
                    other.Add(new EnvBlock(block.Kind, block.Name, block.Properties.Select(p => (p.Key, p.Value)).ToList(), block.LineNumber));
                    break;

                default:
                    throw new ParmFormatException($"unknown block '{block.Kind}'", block.LineNumber);
            }
        }

        // Every colour table and orbit that is named must exist.
        foreach (var block in blocks.Where(b => b.Kind != "colour table"))
        {
            foreach (var property in block.Properties.Where(p => p.Value.Text is not null))
            {
                if (property.Key.EndsWith("colour table", StringComparison.Ordinal) || property.Key.StartsWith("colour table @", StringComparison.Ordinal))
                {
                    if (!colourTables.ContainsKey(property.Value.Text!))
                        throw new ParmFormatException($"{block.Kind} uses colour table \"{property.Value.Text}\", which is not defined", property.LineNumber);
                }
                else if (property.Key == "orbit" && !orbits.ContainsKey(property.Value.Text!))
                {
                    throw new ParmFormatException($"{block.Kind} uses orbit \"{property.Value.Text}\", which is not defined", property.LineNumber);
                }
                else if (property.Key == "controller" && satellites.All(s => s.Name != property.Value.Text))
                {
                    throw new ParmFormatException($"{block.Kind} is controlled by \"{property.Value.Text}\", which is not a satellite", property.LineNumber);
                }
            }
        }

        return new PlanetEnvironment(colourTables, orbits, satellites, fog, other);
    }

    // ---- tokens --------------------------------------------------------------------------------

    private enum TokenKind { Word, Colon, At, Open, Close, Text, Number, Tuple }

    private sealed record Token(TokenKind Kind, string Text, IReadOnlyList<float>? Tuple, int LineNumber);

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var line = 1;
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\n') { line++; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }

            switch (c)
            {
                case ':': tokens.Add(new(TokenKind.Colon, ":", null, line)); i++; continue;
                case '@': tokens.Add(new(TokenKind.At, "@", null, line)); i++; continue;
                case '{': tokens.Add(new(TokenKind.Open, "{", null, line)); i++; continue;
                case '}': tokens.Add(new(TokenKind.Close, "}", null, line)); i++; continue;

                case '"':
                {
                    var end = text.IndexOf('"', i + 1);
                    if (end < 0 || text.AsSpan(i, end - i).Contains('\n'))
                        throw new ParmFormatException("unclosed string", line);
                    tokens.Add(new(TokenKind.Text, text[(i + 1)..end], null, line));
                    i = end + 1;
                    continue;
                }

                case '(':
                {
                    var end = text.IndexOf(')', i + 1);
                    if (end < 0 || text.AsSpan(i, end - i).Contains('\n'))
                        throw new ParmFormatException("unclosed tuple", line);
                    var parts = text[(i + 1)..end].Split(',', StringSplitOptions.TrimEntries);
                    var values = parts.Select(p => float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                        ? v
                        : throw new ParmFormatException($"'{p}' in a tuple is not a number", line)).ToList();
                    tokens.Add(new(TokenKind.Tuple, text[i..(end + 1)], values, line));
                    i = end + 1;
                    continue;
                }
            }

            if (char.IsDigit(c) || c is '-' or '+' or '.')
            {
                var start = i++;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] is '.' or 'e' or 'E'
                    || text[i] is '-' or '+' && text[i - 1] is 'e' or 'E'))
                    i++;
                var number = text[start..i];
                if (!float.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    throw new ParmFormatException($"'{number}' is not a number", line);
                tokens.Add(new(TokenKind.Number, number, null, line));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                tokens.Add(new(TokenKind.Word, text[start..i], null, line));
                continue;
            }

            throw new ParmFormatException($"unexpected character '{c}'", line);
        }

        return tokens;
    }

    // ---- blocks --------------------------------------------------------------------------------

    private sealed record RawProperty(string Key, EnvValue Value, int LineNumber);

    private sealed record RawRow(float Key, IReadOnlyList<float> Tuple, int LineNumber);

    private sealed record RawBlock(string Kind, string? Name, List<RawProperty> Properties, List<RawRow> Rows, int LineNumber)
    {
        public string RequireName() => Name ?? throw new ParmFormatException($"{Kind} needs a name", LineNumber);

        public void RequireNoRows()
        {
            if (Rows.Count > 0)
                throw new ParmFormatException($"{Kind} cannot hold colour rows", Rows[0].LineNumber);
        }

        private RawProperty? Find(string key)
        {
            var matches = Properties.Where(p => p.Key == key).ToList();
            return matches.Count switch
            {
                0 => null,
                1 => matches[0],
                _ => throw new ParmFormatException($"{Kind}: '{key}' is set twice", matches[1].LineNumber),
            };
        }

        public float? OptionalNumber(string key) => Find(key) is { } p
            ? p.Value.Number ?? throw new ParmFormatException($"{Kind}: '{key}' must be a number", p.LineNumber)
            : null;

        public float Number(string key) => OptionalNumber(key) ?? throw new ParmFormatException($"{Kind} needs '{key}'", LineNumber);

        public string? OptionalText(string key) => Find(key) is { } p
            ? p.Value.Text ?? throw new ParmFormatException($"{Kind}: '{key}' must be a quoted name", p.LineNumber)
            : null;

        public string Text(string key) => OptionalText(key) ?? throw new ParmFormatException($"{Kind} needs '{key}'", LineNumber);
    }

    private sealed class BlockReader(List<Token> tokens)
    {
        private int _next;

        public List<RawBlock> ReadAll()
        {
            var blocks = new List<RawBlock>();
            while (_next < tokens.Count)
                blocks.Add(ReadBlock());
            return blocks;
        }

        private RawBlock ReadBlock()
        {
            var first = Peek();
            var kind = ReadWords();
            Expect(TokenKind.Colon);
            string? name = null;
            if (Peek().Kind == TokenKind.Text)
                name = Take().Text;
            Expect(TokenKind.Open);

            var block = new RawBlock(kind, name, [], [], first.LineNumber);
            while (Peek().Kind != TokenKind.Close)
            {
                var token = Peek();
                if (token.Kind == TokenKind.Number)
                {
                    // A colour row: <key> (r, g, b[, a])
                    var key = float.Parse(Take().Text, CultureInfo.InvariantCulture);
                    var tuple = Expect(TokenKind.Tuple);
                    block.Rows.Add(new RawRow(key, tuple.Tuple!, token.LineNumber));
                    continue;
                }

                var propertyKey = ReadWords();
                if (Peek().Kind == TokenKind.At)
                {
                    Take();
                    propertyKey += " @ " + Expect(TokenKind.Number).Text;
                }
                Expect(TokenKind.Colon);

                var value = Take();
                var envValue = value.Kind switch
                {
                    TokenKind.Text => new EnvValue(value.Text, null, null),
                    TokenKind.Number => new EnvValue(null, float.Parse(value.Text, CultureInfo.InvariantCulture), null),
                    TokenKind.Tuple => new EnvValue(null, null, value.Tuple),
                    _ => throw new ParmFormatException($"expected a value after '{propertyKey}:'", value.LineNumber),
                };
                block.Properties.Add(new RawProperty(propertyKey, envValue, token.LineNumber));
            }
            Take();
            return block;
        }

        private string ReadWords()
        {
            var words = new StringBuilder();
            while (Peek().Kind == TokenKind.Word)
            {
                if (words.Length > 0) words.Append(' ');
                words.Append(Take().Text);
            }
            return words.Length > 0 ? words.ToString() : throw new ParmFormatException($"expected a name, found '{Peek().Text}'", Peek().LineNumber);
        }

        private Token Peek() =>
            _next < tokens.Count ? tokens[_next] : throw new ParmFormatException("file ends inside a block", tokens.Count == 0 ? 0 : tokens[^1].LineNumber);

        private Token Take()
        {
            var token = Peek();
            _next++;
            return token;
        }

        private Token Expect(TokenKind kind)
        {
            var token = Take();
            return token.Kind == kind ? token : throw new ParmFormatException($"expected {kind}, found '{token.Text}'", token.LineNumber);
        }
    }
}
