namespace Machines.Import.Parm;

/// <summary>One meaningful line of a parameter file: a keyword and its arguments, comments stripped.</summary>
public sealed record ParmLine(string Keyword, IReadOnlyList<string> Args, int LineNumber)
{
    public override string ToString() => $"{LineNumber}: {Keyword} {string.Join(' ', Args)}";
}

/// <summary>A <c>TYPE ... END</c> block. <see cref="TypeTokens"/> holds the words after <c>TYPE</c>.</summary>
public sealed record ParmRecord(IReadOnlyList<string> TypeTokens, IReadOnlyList<ParmLine> Lines, int LineNumber)
{
    public string TypeName => string.Join(' ', TypeTokens);
}

/// <summary>
/// A top-level section such as <c>MACHINES</c>. Holds its <c>TYPE</c> records, plus any
/// key/value lines that sit directly in the section (as in <c>GENERAL_DATA</c>).
/// </summary>
public sealed record ParmSection(string Name, IReadOnlyList<ParmRecord> Records, IReadOnlyList<ParmLine> Lines, int LineNumber);

/// <summary>The syntax tree of a parameter file, before any meaning is given to keywords.</summary>
public sealed record ParmDocument(IReadOnlyList<ParmSection> Sections)
{
    public ParmSection? Section(string name) => Sections.FirstOrDefault(s => s.Name == name);
}

public sealed class ParmFormatException(string message, int lineNumber)
    : Exception($"line {lineNumber}: {message}")
{
    public int LineNumber { get; } = lineNumber;
}
