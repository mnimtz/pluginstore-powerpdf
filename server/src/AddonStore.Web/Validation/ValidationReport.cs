using System.Text.Json;
using System.Text.Json.Serialization;

namespace AddonStore.Web.Validation;

public record Finding(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("hint")] string Hint);

public class ValidationReport
{
    [JsonPropertyName("findings")]
    public List<Finding> Findings { get; } = new();

    [JsonPropertyName("passed")]
    public bool Passed => !Findings.Any(f => f.Severity == "error");

    public void Error(string code, string message, string hint) =>
        Findings.Add(new Finding(code, "error", message, hint));

    public void Warn(string code, string message, string hint) =>
        Findings.Add(new Finding(code, "warning", message, hint));

    public void Info(string code, string message, string hint = "") =>
        Findings.Add(new Finding(code, "info", message, hint));

    public string ToJson() => JsonSerializer.Serialize(this);
}

/// <summary>The fields the server itself needs from a parsed manifest.</summary>
public class ParsedManifest
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Changelog { get; set; } = "";
    public string AtomNamespace { get; set; } = "";
    public string MinPowerPdfVersion { get; set; } = "";
    /// <summary>Manifest "visibility": "public" (default) or "private" (customer deliveries only).</summary>
    public string Visibility { get; set; } = "public";
    public string Category { get; set; } = "";
    /// <summary>Names of a proposed new category (all checks passed); created on submission.</summary>
    public Dictionary<string, string>? NewCategoryNames { get; set; }
    public string RawJson { get; set; } = "{}";
}
