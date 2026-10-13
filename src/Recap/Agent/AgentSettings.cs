using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Recap.Settings;

namespace Recap.Agent;

public static class AgentSettings
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static JsonObject Redacted(AppSettings settings)
    {
        var node = JsonSerializer.SerializeToNode(settings, Options)!.AsObject();
        Redact(node["azureSpeech"]!.AsObject(), "subscriptionKey");
        Redact(node["openAiCompatible"]!.AsObject(), "apiKey");
        return node;
    }

    private static void Redact(JsonObject section, string key)
    {
        section["hasCredential"] = !string.IsNullOrWhiteSpace(section[key]?.GetValue<string>());
        section.Remove(key);
    }

    public static AppSettings Apply(AppSettings settings, JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Object) throw new AgentException(400, "A settings object is required.");
        var node = JsonSerializer.SerializeToNode(settings, Options)!.AsObject();
        Merge(node, JsonNode.Parse(patch.GetRawText())!.AsObject());
        var result = node.Deserialize<AppSettings>(Options)!;
        if (result.SpeechProviderId != "azure-speech" || result.LlmProviderId != "openai-compatible")
            throw new AgentException(400, "Unknown provider ID.");
        if (patch.TryGetProperty("recordingsFolder", out _) && !System.IO.Path.IsPathFullyQualified(Environment.ExpandEnvironmentVariables(result.RecordingsFolder))
            || patch.TryGetProperty("logsFolder", out _) && !System.IO.Path.IsPathFullyQualified(Environment.ExpandEnvironmentVariables(result.LogsFolder)))
            throw new AgentException(400, "Storage paths must be absolute.");
        return result;
    }

    private static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var entry in patch)
        {
            if (!target.ContainsKey(entry.Key)) throw new AgentException(400, "Unknown settings field. GET /settings lists the supported fields; credentials are write-only.");
            if (target[entry.Key] is JsonObject nested)
            {
                if (entry.Value is not JsonObject child) throw new AgentException(400, "Settings sections must be objects.");
                Merge(nested, child);
            }
            else
            {
                if (entry.Value is null && entry.Key is not ("remoteDeviceId" or "microphoneDeviceId"))
                    throw new AgentException(400, "Use an empty string to clear a string field, not null.");
                if (entry.Value is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 8192)
                    throw new AgentException(400, "Settings value is too long.");
                target[entry.Key] = entry.Value?.DeepClone();
            }
        }
    }

    public static void Copy(AppSettings source, AppSettings destination)
    {
        foreach (var property in typeof(AppSettings).GetProperties())
            property.SetValue(destination, property.GetValue(source));
    }
}
