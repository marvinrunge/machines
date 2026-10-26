using System.Globalization;
using System.Text.RegularExpressions;

namespace Machines.Import.Parm;

/// <summary>
/// Reads numbers the way C's <c>atoi</c>/<c>atof</c> did in the original loaders: the longest numeric
/// prefix, or 0 if there is none. They never fail, so typos in the data quietly became numbers.
/// </summary>
internal static partial class CNumbers
{
    /// <returns>The value, and whether the whole token was a clean number.</returns>
    public static (double Value, bool Clean, string ReadAs) Parse(string token, bool integer)
    {
        var match = (integer ? IntegerPrefix() : FloatPrefix()).Match(token);
        var value = match.Success ? double.Parse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture) : 0;
        return (value, match.Success && match.Length == token.Length, match.Success ? match.Value : "0");
    }

    [GeneratedRegex(@"^[+-]?\d+", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerPrefix();

    [GeneratedRegex(@"^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex FloatPrefix();
}
