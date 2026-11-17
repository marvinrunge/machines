using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Machines.Sim.World;

namespace Machines.Net;

/// <summary>
/// JSON for messages and the simulation's commands. Every <see cref="Command"/> record in the simulation is
/// known by its type name, so new commands need no registration here. Floats round-trip exactly, which
/// lockstep relies on: every world must carry out bit-identical commands.
/// </summary>
public static class NetJson
{
    /// <summary>Bump when messages or commands change incompatibly; the relay refuses other versions. 11: battles (heroes in rooms, the options' Battle). 12: Wired's chassis specials (special, tower and scrap commands).</summary>
    public const int Protocol = 12;

    public static readonly JsonSerializerOptions Options = Create();

    public static byte[] Encode(NetMessage message) => JsonSerializer.SerializeToUtf8Bytes(message, Options);

    public static NetMessage Decode(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize<NetMessage>(json, Options) ?? throw new JsonException("empty message");

    private static JsonSerializerOptions Create()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type != typeof(Command))
                return;
            info.PolymorphismOptions = new JsonPolymorphismOptions { TypeDiscriminatorPropertyName = "$command" };
            foreach (var type in typeof(Command).Assembly.GetTypes()
                         .Where(t => t.IsSubclassOf(typeof(Command)) && !t.IsAbstract)
                         .OrderBy(t => t.Name, StringComparer.Ordinal))
                info.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(type, type.Name));
        });
        return new JsonSerializerOptions
        {
            TypeInfoResolver = resolver,
            // Vector2 keeps its coordinates in fields.
            IncludeFields = true,
            Converters = { new JsonStringEnumConverter(), new PlayerIdConverter() },
        };
    }
}

/// <summary>A <see cref="PlayerId"/> as its bare number, also where it keys a dictionary.</summary>
public sealed class PlayerIdConverter : JsonConverter<PlayerId>
{
    public override PlayerId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetInt32());

    public override void Write(Utf8JsonWriter writer, PlayerId value, JsonSerializerOptions options) => writer.WriteNumberValue(value.Value);

    public override PlayerId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(int.Parse(reader.GetString() ?? throw new JsonException("a player without a number"), System.Globalization.CultureInfo.InvariantCulture));

    public override void WriteAsPropertyName(Utf8JsonWriter writer, PlayerId value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
