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
    /// <summary>Bump when messages or commands change incompatibly; the relay refuses other versions.</summary>
    public const int Protocol = 3;

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
            Converters = { new JsonStringEnumConverter() },
        };
    }
}
