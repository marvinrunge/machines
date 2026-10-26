using System.Text;
using System.Xml;
using System.Xml.Linq;
using Machines.Import.Parm;

namespace Machines.Import.Text;

/// <summary>
/// <c>machstrg.xml</c>: UI texts, unit and weapon names, menu names (<c>IDS</c> ids in <c>sceninfo.dat</c>).
/// </summary>
/// <param name="Strings">Text by id.</param>
public sealed record StringTable(IReadOnlyDictionary<int, string> Strings)
{
    /// <summary>
    /// Bytes 0x80 to 0xFF in the file are glyph codes in the game's own font, not text. 0x81 stands in front of
    /// every BMU amount, so it is most likely the BMU sign. They are kept as the characters U+0080 to U+00FF.
    /// </summary>
    public const char BmuSign = '\u0081';

    public string this[int id] => Strings.TryGetValue(id, out var text) ? text : throw new KeyNotFoundException($"no string {id}");
}

public static class StringTableParser
{
    public static StringTable Load(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>
    /// The file claims UTF-8 but holds single font-glyph bytes, so it is decoded byte for byte (Latin-1).
    /// </summary>
    public static StringTable Parse(byte[] bytes)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(Encoding.Latin1.GetString(bytes), LoadOptions.SetLineInfo);
        }
        catch (XmlException e)
        {
            throw new ParmFormatException(e.Message, e.LineNumber);
        }

        if (document.Root?.Name != "resources")
            throw new ParmFormatException("expected a <resources> root", 1);

        var strings = new Dictionary<int, string>();
        foreach (var element in document.Root.Elements())
        {
            var line = ((IXmlLineInfo)element).LineNumber;
            if (element.Name != "string")
                throw new ParmFormatException($"unexpected <{element.Name}>", line);
            if (!int.TryParse((string?)element.Attribute("id"), out var id))
                throw new ParmFormatException("<string> needs a numeric id", line);
            if (!strings.TryAdd(id, element.Value))
                throw new ParmFormatException($"string id {id} appears twice", line);
        }
        return new StringTable(strings);
    }
}
