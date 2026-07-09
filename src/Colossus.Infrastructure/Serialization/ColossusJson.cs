using System.Text.Json;
using System.Text.Json.Serialization;

namespace Colossus.Infrastructure.Serialization;

/// <summary>Single source of truth for view/manifest JSON: camelCase properties and enums, matching
/// the web client and the authored config files.</summary>
public static class ColossusJson
{
    public static readonly JsonSerializerOptions Options = Apply(new JsonSerializerOptions());

    /// <summary>Applies the canonical Colossus JSON conventions to an options instance. Used both for
    /// <see cref="Options"/> and to configure the server's MVC pipeline, so file I/O and API responses
    /// serialize identically.</summary>
    public static JsonSerializerOptions Apply(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.PropertyNameCaseInsensitive = true;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.WriteIndented = true;
        options.AllowTrailingCommas = true;
        options.ReadCommentHandling = JsonCommentHandling.Skip;
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;
}
