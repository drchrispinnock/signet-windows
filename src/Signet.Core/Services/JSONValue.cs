using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Signet.Services;

/// <summary>
/// An immutable mirror of anything the bridge can hand back, so results can cross threads freely.
/// The bridge returns JSON text; this is that JSON as a value.
/// </summary>
public abstract record JSONValue
{
    public sealed record Null : JSONValue;
    public sealed record Bool(bool Value) : JSONValue;
    public sealed record Number(double Value) : JSONValue;
    public sealed record String(string Value) : JSONValue;

    public sealed record Array(IReadOnlyList<JSONValue> Items) : JSONValue
    {
        public bool Equals(Array? other) => other is not null && Items.SequenceEqual(other.Items);
        public override int GetHashCode() => Items.Count;
    }

    public sealed record Object(IReadOnlyDictionary<string, JSONValue> Members) : JSONValue
    {
        public bool Equals(Object? other) =>
            other is not null && Members.Count == other.Members.Count &&
            Members.All(m => other.Members.TryGetValue(m.Key, out var v) && Equals(m.Value, v));
        public override int GetHashCode() => Members.Count;
    }

    public static readonly JSONValue NullValue = new Null();

    /// <summary>Parses JSON text; anything unparseable becomes <c>Null</c>.</summary>
    public static JSONValue Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return NullValue;
        try { return FromNode(JsonNode.Parse(json)); }
        catch (JsonException) { return NullValue; }
    }

    public static JSONValue FromNode(JsonNode? node) => node switch
    {
        null => NullValue,
        JsonObject o => new Object(o.ToDictionary(p => p.Key, p => FromNode(p.Value))),
        JsonArray a => new Array(a.Select(FromNode).ToList()),
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.True => new Bool(true),
            JsonValueKind.False => new Bool(false),
            JsonValueKind.Number => new Number(v.GetValue<double>()),
            JsonValueKind.String => new String(v.GetValue<string>()),
            _ => NullValue,
        },
        _ => NullValue,
    };

    public string? StringValue => this is String s ? s.Value : null;
    public bool? BoolValue => this is Bool b ? b.Value : null;
    public double? DoubleValue => this is Number n ? n.Value : null;
    public IReadOnlyDictionary<string, JSONValue>? ObjectValue => this is Object o ? o.Members : null;
    public IReadOnlyList<JSONValue>? ArrayValue => this is Array a ? a.Items : null;
    public bool IsNull => this is Null;

    public JSONValue? this[string key] => ObjectValue is { } o && o.TryGetValue(key, out var v) ? v : null;

    public override string ToString() => this switch
    {
        Null => "null",
        Bool b => b.Value ? "true" : "false",
        Number n => n.Value.ToString(CultureInfo.InvariantCulture),
        String s => JsonSerializer.Serialize(s.Value),
        Array a => "[" + string.Join(",", a.Items) + "]",
        Object o => "{" + string.Join(",", o.Members.Select(m => JsonSerializer.Serialize(m.Key) + ":" + m.Value)) + "}",
        _ => "",
    };
}
