using System.Text.Json;
using System.Text.Json.Nodes;

namespace Home.Client;

/// <summary>Human-readable point values: enum names, on/off, units.</summary>
public static class PointFormat
{
    public static string Format(PointDto p, JsonNode? v, bool withUnit = true)
    {
        if (v == null) return "-";
        string s;
        var kind = v.GetValueKind();
        if (p.Type == "enum" && kind == JsonValueKind.Number)
        {
            var i = v.GetValue<int>();
            s = i >= 0 && i < p.Options.Count ? p.Options[i] : i.ToString();
        }
        else if (p.Type == "bool" && kind is JsonValueKind.True or JsonValueKind.False) s = v.GetValue<bool>() ? "on" : "off";
        else s = kind == JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString();
        return withUnit && p.Unit != null ? $"{s} {p.Unit}" : s;
    }
}
