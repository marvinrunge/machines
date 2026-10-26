using Machines.Import.Model;
using Machines.Import.Parm;

namespace Machines.Import.Planets;

/// <summary>All three files of one planet folder, e.g. <c>models/planet/desert</c>.</summary>
public sealed record Planet(string Name, PlanetSurface Surface, ConfigSpace ConfigSpace, PlanetEnvironment Environment)
{
    /// <summary>Loads <c>&lt;folder&gt;/&lt;name&gt;.psf</c>, <c>.csp</c> and <c>.env</c>.</summary>
    public static Planet Load(string folder)
    {
        var name = Path.GetFileName(folder.TrimEnd('/', '\\'));
        string File(string extension) => Path.Combine(folder, name + extension);

        return new Planet(
            name,
            SurfaceParser.Load(File(".psf")),
            ConfigSpaceParser.Load(File(".csp")),
            EnvironmentParser.Load(File(".env")));
    }

    /// <summary>Runs a parser and puts the file name in front of any format error.</summary>
    internal static T WithFileName<T>(string path, Func<T> parse)
    {
        try
        {
            return parse();
        }
        catch (ParmFormatException e)
        {
            throw new ParmFormatException($"{Path.GetFileName(path)}: {e.Message}", e.LineNumber);
        }
    }
}
