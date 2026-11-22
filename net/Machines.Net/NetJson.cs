using System.Runtime.CompilerServices;
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
    /// <summary>
    /// Bump when messages or commands change incompatibly; the relay refuses other versions. 11: battles (heroes in rooms, the
    /// options' Battle). 12: Wired's chassis specials (special, tower and scrap commands). 13: players apart from races (turns,
    /// checksums, leaving and coming back by player; seats with their race, several a race; recordings stamped with it).
    /// 14: relay-paced bundles (P2: the relay keeps the beat and sends a bundle a tick; lag marks, late joins, seat keys,
    /// rebuilds from the relay's log; recordings may hold joins and lag marks). 15: the story (its options in a match, story
    /// rooms of three seats, bots' seats of the trio, story commands, every <c>StoryCommand</c> found here as any other).
    /// 16: fitted bodies (the options' FittedBodies: machines stand and are hit in bodies fitted to their models in every
    /// mode, so a client of 15 would play another match; main's 15 is the same change without the story). 17: the
    /// computers' skirmish rules and the heroes' gun arcs (the options' SkirmishAi, a client of 16 would play the computers
    /// as before; and a sighted hero without an upper body angles its guns within their arcs, <see cref="MatchOptions.HeroGunArc"/>,
    /// recordings whose options do not name them play without); main's 16 is the same change without the story.
    /// </summary>
    public const int Protocol = 17;

    public static readonly JsonSerializerOptions Options = Create();

    // A bundle or a piece of the log goes to every member of a room: encoded once, for as long as the message lives.
    private static readonly ConditionalWeakTable<NetMessage, byte[]> Encoded = new();

    /// <summary>
    /// The message as JSON. A <see cref="Bundle"/> or <see cref="LogPart"/> is encoded once and the same bytes are given to
    /// every caller after, so they must not be changed.
    /// </summary>
    public static byte[] Encode(NetMessage message) => message is Bundle or LogPart
        ? Encoded.GetValue(message, static shared => Serialize(shared))
        : Serialize(message);

    public static NetMessage Decode(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize<NetMessage>(json, Options) ?? throw new JsonException("empty message");

    private static byte[] Serialize(NetMessage message) => JsonSerializer.SerializeToUtf8Bytes(message, Options);

    private static JsonSerializerOptions Create()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(Turn))
            {
                // What only bundle mode fills in is left out where it is not filled in: a turn of a recording, and every turn
                // inside a bundle, reads as it did before (the defaults come back from the constructor when read).
                foreach (var property in info.Properties)
                {
                    property.ShouldSerialize = property.Name switch
                    {
                        nameof(Turn.Seen) or nameof(Turn.Asked) => static (_, value) => value is not -1L,
                        nameof(Turn.Held) => static (_, value) => value is not 0,
                        _ => property.ShouldSerialize,
                    };
                }
                return;
            }
            if (info.Type == typeof(PlayerLeft))
            {
                // A plain leave reads as before P2: in a recording's presence, and in every bundle where it is no lag or rebuild.
                foreach (var property in info.Properties.Where(p => p.Name == nameof(PlayerLeft.Why)))
                    property.ShouldSerialize = static (_, value) => value is not LeaveReason.Left;
                return;
            }
            if (info.Type == typeof(DriveCommand))
            {
                // The story's sprint goes only where it is held: every other drive reads, and is written, as before it was.
                foreach (var property in info.Properties.Where(p => p.Name == nameof(DriveCommand.Sprint)))
                    property.ShouldSerialize = static (_, value) => value is true;
                return;
            }
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
