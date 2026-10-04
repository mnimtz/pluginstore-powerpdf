using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using Microsoft.AspNetCore.DataProtection;

namespace AddonStore.Web.Services;

/// <summary>
/// Optional AI assistance on the server (S0.12.0): sorting problem reports,
/// a review aid for approvals and natural-language catalog search. Off by
/// default; an admin chooses the provider (Claude via the official Anthropic
/// SDK, or Google Gemini via REST), stores the API key (encrypted with the
/// data protection keys in data/keys) and switches each feature on.
/// Only store texts and, if explicitly allowed, source excerpts are sent;
/// never e-mail addresses, IP addresses or log excerpts. Every answer is a
/// JSON object constrained by a schema and shown as plain text; nothing the
/// model returns is ever executed.
/// </summary>
public class AiService
{
    public const string ProviderKey = "Ai.Provider";           // off, claude, gemini (fake: Development only)
    public const string ApiKeyKey = "Ai.ApiKey";               // protected
    public const string ModelKey = "Ai.Model";
    public const string TriageKey = "Ai.Triage";               // "1" = sort problem reports
    public const string ReviewKey = "Ai.Review";               // "1" = review aid on demand
    public const string ReviewAutoKey = "Ai.ReviewAuto";       // "1" = review aid automatically for new versions
    public const string ReviewSourceKey = "Ai.ReviewSource";   // "1" = send source excerpts with the review aid
    public const string SearchKey = "Ai.Search";               // "1" = natural-language search
    public const string DailyLimitKey = "Ai.DailyLimit";       // calls per day, default 300

    public const string DefaultClaudeModel = "claude-opus-5-5";
    public const string DefaultGeminiModel = "gemini-2.5-flash";

    private readonly SettingsService _settings;
    private readonly IDataProtector _protector;
    private readonly IHttpClientFactory _http;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<AiService> _log;
    private static int _callsToday;
    private static string _day = "";
    private static readonly object _gate = new();

    public AiService(SettingsService settings, IDataProtectionProvider dp, IHttpClientFactory http,
                     IWebHostEnvironment env, ILogger<AiService> log)
    {
        _settings = settings; _protector = dp.CreateProtector("AddonStore.AiKey"); _http = http; _env = env; _log = log;
    }

    public record Config(string Provider, string Model, bool HasKey, bool Triage, bool Review, bool ReviewAuto,
                         bool ReviewSource, bool Search, int DailyLimit)
    {
        public bool On => Provider is "claude" or "gemini" or "fake";
    }

    public async Task<Config> ConfigAsync()
    {
        var provider = (await _settings.GetAsync(ProviderKey)).Trim().ToLowerInvariant();
        if (provider == "fake" && !_env.IsDevelopment()) provider = "off";
        if (provider is not ("claude" or "gemini" or "fake")) provider = "off";
        var model = (await _settings.GetAsync(ModelKey)).Trim();
        if (model.Length == 0) model = provider == "gemini" ? DefaultGeminiModel : DefaultClaudeModel;
        int.TryParse(await _settings.GetAsync(DailyLimitKey), out var limit);
        return new Config(provider, model, (await _settings.GetAsync(ApiKeyKey)).Length > 0,
            await _settings.GetAsync(TriageKey) == "1", await _settings.GetAsync(ReviewKey) == "1",
            await _settings.GetAsync(ReviewAutoKey) == "1", await _settings.GetAsync(ReviewSourceKey) == "1",
            await _settings.GetAsync(SearchKey) == "1", limit is >= 1 and <= 100000 ? limit : 300);
    }

    public async Task SetApiKeyAsync(string key) =>
        await _settings.SetAsync(ApiKeyKey, key.Length == 0 ? "" : _protector.Protect(key));

    private async Task<string?> ApiKeyAsync()
    {
        var stored = await _settings.GetAsync(ApiKeyKey);
        if (stored.Length == 0) return null;
        try { return _protector.Unprotect(stored); }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Data protection keys changed (e.g. restore on another server): the admin enters the key again.
            _log.LogWarning("AI API key cannot be decrypted; enter it again in the settings");
            return null;
        }
    }

    private bool TakeQuota(int limit)
    {
        lock (_gate)
        {
            var day = DateTime.UtcNow.ToString("yyyyMMdd");
            if (day != _day) { _day = day; _callsToday = 0; }
            if (_callsToday >= limit) return false;
            _callsToday++;
            return true;
        }
    }

    /// <summary>
    /// One request returning a JSON object that follows <paramref name="schema"/>
    /// (JSON Schema, "type": "object"). Null when AI is off, the quota is used up
    /// or the provider fails; callers then simply show nothing.
    /// </summary>
    public async Task<JsonElement?> JsonAsync(string system, string user, object schema, bool deep = false, CancellationToken ct = default)
    {
        var cfg = await ConfigAsync();
        if (!cfg.On || !TakeQuota(cfg.DailyLimit)) return null;
        try
        {
            string? text = cfg.Provider switch
            {
                "claude" => await ClaudeAsync(cfg, system, user, schema, deep, ct),
                "gemini" => await GeminiAsync(cfg, system, user, schema, ct),
                "fake" => FakeAnswer(schema),
                _ => null
            };
            if (string.IsNullOrWhiteSpace(text)) return null;
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "AI request failed ({Provider})", cfg.Provider);
            return null;
        }
    }

    /// <summary>Connection test for the settings page: (ok, message).</summary>
    public async Task<(bool Ok, string Message)> TestAsync()
    {
        var cfg = await ConfigAsync();
        if (!cfg.On) return (false, "AI is switched off.");
        if (cfg.Provider != "fake" && await ApiKeyAsync() is null) return (false, "No API key stored.");
        var r = await JsonAsync("You answer with JSON only.", "Reply with {\"ok\": true}.",
            new { type = "object", properties = new { ok = new { type = "boolean" } }, required = new[] { "ok" }, additionalProperties = false });
        return r is { } e && e.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
            ? (true, $"{cfg.Provider} / {cfg.Model}: OK")
            : (false, $"{cfg.Provider} / {cfg.Model}: no valid answer (see server log).");
    }

    // --- Claude (official Anthropic C# SDK) -----------------------------------
    private async Task<string?> ClaudeAsync(Config cfg, string system, string user, object schema, bool deep, CancellationToken ct)
    {
        var key = await ApiKeyAsync();
        if (key is null) return null;
        var client = new AnthropicClient { ApiKey = key };
        var schemaDict = JsonSerializer.SerializeToElement(schema).EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone());
        var parameters = new MessageCreateParams
        {
            Model = cfg.Model,
            MaxTokens = deep ? 16000 : 4000,
            System = system,
            Messages = [new() { Role = Role.User, Content = user }],
            OutputConfig = new BetaOutputConfig
            {
                Effort = deep ? "medium" : "low",
                Format = new BetaJsonOutputFormat { Schema = schemaDict },
            },
            // A policy decline is answered by the fallback model in the same call.
            Betas = ["server-side-fallback-2026-06-01"],
            Fallbacks = new BetaFallbacksParam(new List<BetaFallbackParam> { new() { Model = "claude-opus-4-8" } }),
        };
        var response = await client.Beta.Messages.Create(parameters, cancellationToken: ct);
        if (response.StopReason == "refusal")
        {
            _log.LogInformation("AI request declined by the model");
            return null;
        }
        var sb = new StringBuilder();
        foreach (var block in response.Content)
            if (block.TryPickText(out var t)) sb.Append(t.Text);
        return sb.ToString();
    }

    // --- Google Gemini (REST, generateContent) ----------------------------------
    private async Task<string?> GeminiAsync(Config cfg, string system, string user, object schema, CancellationToken ct)
    {
        var key = await ApiKeyAsync();
        if (key is null) return null;
        var http = _http.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(3);
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(cfg.Model)}:generateContent";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("x-goog-api-key", key);
        req.Content = JsonContent.Create(new
        {
            systemInstruction = new { parts = new[] { new { text = system + "\n\nAnswer with one JSON object matching this JSON Schema:\n" + JsonSerializer.Serialize(schema) } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = user } } } },
            generationConfig = new { responseMimeType = "application/json", temperature = 0.2 }
        });
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("Gemini returned {Status}: {Body}", (int)resp.StatusCode, body.Length > 300 ? body[..300] : body);
            return null;
        }
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("candidates", out var c) || c.GetArrayLength() == 0) return null;
        var parts = c[0].GetProperty("content").GetProperty("parts");
        return string.Concat(parts.EnumerateArray().Select(p => p.TryGetProperty("text", out var t) ? t.GetString() : ""));
    }

    // --- Development: deterministic answers for automated tests ------------------
    private static string FakeAnswer(object schema)
    {
        var el = JsonSerializer.SerializeToElement(schema);
        return JsonSerializer.Serialize(FakeValue(el));
    }

    private static object? FakeValue(JsonElement s)
    {
        var type = s.TryGetProperty("type", out var t) ? t.GetString() : "string";
        if (s.TryGetProperty("enum", out var en) && en.GetArrayLength() > 0) return en[0].GetString();
        return type switch
        {
            "object" => s.TryGetProperty("properties", out var props)
                ? props.EnumerateObject().ToDictionary(p => p.Name, p => FakeValue(p.Value))
                : new Dictionary<string, object?>(),
            "array" => s.TryGetProperty("items", out var items) ? new[] { FakeValue(items) } : Array.Empty<object>(),
            "boolean" => true,
            "integer" or "number" => 0,
            _ => "fake"
        };
    }
}
