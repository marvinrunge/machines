namespace Machines.Import.Parm;

/// <summary>
/// Splits the original text formats into whitespace-separated tokens, as <c>UtlLineTokeniser</c> did:
/// <c>//</c> starts a comment and blank lines are skipped.
/// </summary>
internal static class TextLines
{
    public static IEnumerable<ParmLine> Read(string text)
    {
        var lineNumber = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;

            var content = raw;
            var comment = content.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
                content = content[..comment];

            var tokens = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                continue;

            yield return new ParmLine(tokens[0], tokens[1..], lineNumber);
        }
    }
}
