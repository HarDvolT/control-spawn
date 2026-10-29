using System.Globalization;
using System.Text.Json;

namespace Prism.Router;

/// <summary>
/// Helpers for the router's loosely typed JSON replies. The Python reference works on plain
/// dicts, so the C# port keeps the same shape: nested objects become
/// <see cref="Dictionary{TKey,TValue}"/> of string -> object?, arrays become
/// <see cref="List{T}"/> of object?.
/// </summary>
public static class Raw
{
    /// <summary>Python <c>str(value)</c> for the scalar types the router sends.</summary>
    public static string StrOf(object? value)
    {
        return value switch
        {
            null => "None",
            string s => s,
            bool b => b ? "True" : "False",
            long l => l.ToString(CultureInfo.InvariantCulture),
            int i => i.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString(CultureInfo.InvariantCulture),
            float f => f.ToString(CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
    }

    /// <summary>Like the Python code's <c>str(value or "")</c> for scalar values.</summary>
    public static string Str(object? value) => value is null ? "" : StrOf(value);

    /// <summary>Python truthiness for the values the router sends.</summary>
    public static bool Truthy(object? value)
    {
        return value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            long l => l != 0,
            int i => i != 0,
            double d => d != 0,
            float f => f != 0,
            decimal m => m != 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };
    }

    /// <summary>Python <c>float()</c> on a router value; null when missing or unparsable.</summary>
    public static double? ToFloat(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case double d:
                return d;
            case float f:
                return f;
            case decimal m:
                return (double)m;
            case long l:
                return l;
            case int i:
                return i;
            case string s:
            {
                var text = s.Trim();
                if (text.Length == 0)
                {
                    return null;
                }

                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;
            }
            default:
                // Python str(True) == "True", which float() rejects.
                return null;
        }
    }

    /// <summary>Python <c>int(str(value).strip())</c>: only whole numbers parse.</summary>
    public static long? ToInt(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case long l:
                return l;
            case int i:
                return i;
            case string s:
            {
                var text = s.Trim();
                if (text.Length == 0)
                {
                    return null;
                }

                return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;
            }
            default:
                // Python str(3.0) == "3.0", which int() rejects.
                return null;
        }
    }

    /// <summary>Object map from a nested reply value, or null.</summary>
    public static IReadOnlyDictionary<string, object?>? AsMap(object? value)
    {
        return value as IReadOnlyDictionary<string, object?>;
    }

    /// <summary>Array from a nested reply value, or null.</summary>
    public static IReadOnlyList<object?>? AsList(object? value)
    {
        return value as IReadOnlyList<object?>;
    }

    /// <summary>True only for the JSON literal <c>false</c> (Python <c>is False</c>).</summary>
    public static bool IsFalse(object? value) => value is bool b && !b;

    /// <summary>Converts a parsed JSON tree to the object graph used everywhere else.</summary>
    public static object? FromJson(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    map[property.Name] = FromJson(property.Value);
                }

                return map;
            }
            case JsonValueKind.Array:
            {
                var list = new List<object?>();
                foreach (var item in element.EnumerateArray())
                {
                    list.Add(FromJson(item));
                }

                return list;
            }
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                return element.TryGetInt64(out var whole) ? whole : element.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }
}

/// <summary>Parsing of the router's replies, including its leading-junk quirk.</summary>
public static class RouterJson
{
    /// <summary>
    /// The router's own web page strips anything before the first "{". Invalid JSON becomes a
    /// <see cref="RouterError"/>, matching the Python transport.
    /// </summary>
    public static object? Parse(string text)
    {
        var start = text.IndexOf('{');
        var json = start >= 0 ? text[start..] : text;
        try
        {
            using var document = JsonDocument.Parse(json);
            return Raw.FromJson(document.RootElement);
        }
        catch (JsonException e)
        {
            throw new RouterError("Router sent a non-JSON reply", e);
        }
    }
}
