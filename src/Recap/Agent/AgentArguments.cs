using System.Text.Json;

namespace Recap.Agent;

public sealed class AgentArguments(JsonElement body)
{
    public bool Has(string key) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out _);
    public void Only(params string[] keys)
    {
        if (body.ValueKind == JsonValueKind.Undefined) return;
        if (body.ValueKind != JsonValueKind.Object) throw new AgentException(400, "Expected an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in body.EnumerateObject())
            if (!keys.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new AgentException(400, "Unknown or repeated request field. Read /skill for the supported fields.");
    }
    public string Text(string key, string? fallback = null)
    {
        if (!Has(key)) return fallback ?? throw new AgentException(400, $"Missing {key}.");
        var value = body.GetProperty(key);
        if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > 8192) throw new AgentException(400, $"Invalid {key}.");
        return value.GetString()!;
    }
    public bool Bool(string key)
    {
        if (!Has(key)) return false;
        var value = body.GetProperty(key);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new AgentException(400, $"Invalid {key}.");
        return value.GetBoolean();
    }
    public double Number(string key, double fallback = 0)
    {
        if (!Has(key)) return fallback;
        var value = body.GetProperty(key);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number)) throw new AgentException(400, $"Invalid {key}.");
        return number;
    }
    public void Confirm()
    {
        if (!Bool("confirm")) throw new AgentException(400, "Explicit confirm: true is required. Obtain the user's approval first.");
    }
    public Dictionary<string, string> Names()
    {
        if (!Has("names") || body.GetProperty("names").ValueKind != JsonValueKind.Object)
            throw new AgentException(400, "names must map each source speaker label to a name.");
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in body.GetProperty("names").EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Value.GetString())
                || property.Value.GetString()!.Length > 200 || property.Value.GetString()!.Any(char.IsControl)
                || !names.TryAdd(property.Name, property.Value.GetString()!.Trim()))
                throw new AgentException(400, "Invalid or duplicate speaker name.");
        }
        return names;
    }
}
