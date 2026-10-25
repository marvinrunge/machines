namespace Machines.Import.Parm;

/// <summary>
/// Reads the block format used by <c>parmdata.dat</c>:
/// <code>
/// SECTION
/// TYPE A B
/// KEY v1 v2      // comment
/// END
/// END SECTION    // or a bare END
/// </code>
/// Mirrors the structure that <c>MachPhysDataParser</c> in the original source expects.
/// </summary>
public static class ParmReader
{
    public static ParmDocument Read(string text)
    {
        var sections = new List<ParmSection>();

        string? sectionName = null;
        int sectionLine = 0;
        List<ParmRecord> records = [];
        List<ParmLine> sectionLines = [];

        List<string>? recordType = null;
        int recordLine = 0;
        List<ParmLine> recordLines = [];

        foreach (var line in TextLines.Read(text))
        {
            if (sectionName is null)
            {
                if (line.Args.Count != 0)
                    throw new ParmFormatException($"expected a section name, found '{line.Keyword} {string.Join(' ', line.Args)}'", line.LineNumber);

                sectionName = line.Keyword;
                sectionLine = line.LineNumber;
                records = [];
                sectionLines = [];
                continue;
            }

            if (recordType is not null)
            {
                if (line.Keyword == "END")
                {
                    if (line.Args.Count != 0)
                        throw new ParmFormatException($"record '{string.Join(' ', recordType)}' is not closed before 'END {string.Join(' ', line.Args)}'", line.LineNumber);

                    records.Add(new ParmRecord(recordType, recordLines, recordLine));
                    recordType = null;
                }
                else if (line.Keyword == "TYPE")
                {
                    throw new ParmFormatException($"record '{string.Join(' ', recordType)}' is not closed before a new TYPE", line.LineNumber);
                }
                else
                {
                    recordLines.Add(line);
                }
                continue;
            }

            switch (line.Keyword)
            {
                case "TYPE":
                    if (line.Args.Count == 0)
                        throw new ParmFormatException("TYPE without a name", line.LineNumber);
                    recordType = [.. line.Args];
                    recordLine = line.LineNumber;
                    recordLines = [];
                    break;

                case "END":
                    if (line.Args.Count > 0 && line.Args[0] != sectionName)
                        throw new ParmFormatException($"'END {line.Args[0]}' closes section '{sectionName}'", line.LineNumber);
                    sections.Add(new ParmSection(sectionName, records, sectionLines, sectionLine));
                    sectionName = null;
                    break;

                default:
                    sectionLines.Add(line);
                    break;
            }
        }

        if (recordType is not null)
            throw new ParmFormatException($"record '{string.Join(' ', recordType)}' is never closed", recordLine);
        if (sectionName is not null)
            throw new ParmFormatException($"section '{sectionName}' is never closed", sectionLine);

        return new ParmDocument(sections);
    }
}
