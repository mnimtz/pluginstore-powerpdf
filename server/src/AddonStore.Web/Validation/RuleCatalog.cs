using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AddonStore.Web.Api;
using AddonStore.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Validation;

/// <summary>
/// Every rule of the store in one place (S1.0.9): the codes of the agent guide's
/// rule reference (the single source of the rule texts, kept complete by
/// tools/check_docs.py), grouped into areas, plus the store's own extensions an
/// admin maintains under Admin, Rules:
///   - house rules: guidelines in plain words that reviewers check at approval
///     (or recommendations); they appear in the guide, the pre-flight
///     checklist, GET /api/rules and the PDF manual;
///   - stricter rules: warnings the store treats as errors (enforced by the
///     validator, e.g. "screenshots are mandatory here").
/// The extensions live in AppSettings (Rules.House, Rules.Escalated), so the
/// database backup carries them; a static snapshot serves the guide and the
/// validator without a database round trip.
/// </summary>
public static class RuleCatalog
{
    public record Rule(string Code, string Severity, string Description, string Area, bool Api);
    public record HouseRule(string Id, string Area, string Title, string Text, string Kind, string CreatedBy, DateTime CreatedAt);
    public record HouseState(IReadOnlyList<HouseRule> Rules, IReadOnlyCollection<string> Escalated)
    {
        public static readonly HouseState Empty = new(Array.Empty<HouseRule>(), Array.Empty<string>());
    }

    public const string HouseKey = "Rules.House";
    public const string EscalatedKey = "Rules.Escalated";

    /// <summary>Areas in display order (English = resx key).</summary>
    public static readonly string[] Areas =
    {
        "Package structure", "Manifest and versioning", "Categories", "Languages", "Binaries and integrity",
        "Runtime and dependencies", "Ribbon and layout", "Licenses, legal and privacy", "Icon and screenshots",
        "Source code", "Customer deliveries", "API, accounts and limits",
    };

    public static string AreaOf(string code)
    {
        bool Any(params string[] prefixes) => prefixes.Any(p => code.StartsWith(p, StringComparison.Ordinal));
        if (Any("SOURCE_") || code is "LICENSE_COPYLEFT_SOURCE" or "LICENSE_WEAK_COPYLEFT_SOURCE" or "THIRDPARTY_SOURCE_DETECTED") return "Source code";
        if (Any("CUSTOMER_", "DELIVERY_") || code is "PROMOTE_NOTHING" or "CODE_NOT_FOUND" or "CODE_MISSING") return "Customer deliveries";
        if (Any("CATEGORY_")) return "Categories";
        if (Any("ICON_", "SCREENSHOT")) return "Icon and screenshots";
        if (Any("LANG", "UI_LANGS", "UI_STRINGS")) return "Languages";
        if (Any("ATOM_", "OWN_TAB_") || code is "VISIBILITY_OWN_TAB" or "RESERVED_PANEL_NS" or "ICONMODE_SMALL" or "UILAYOUT_MISSING") return "Ribbon and layout";
        if (code is "PE_DEBUG_RUNTIME" or "FOREIGN_DEPENDENCY") return "Runtime and dependencies";
        if (Any("ARCH_", "HASH_", "PE_", "VERSIONINFO_", "ZXT_NAME_") || code is "FILE_DECLARATION_MISSING" or "FILE_MISSING" or "FILENAME_MISMATCH" or "RESERVED_NAME")
            return "Binaries and integrity";
        if (Any("LICENSE", "COMPLIANCE_", "EXTERNAL_SERVICE", "THIRDPARTY_") || code is "SECRET_DETECTED") return "Licenses, legal and privacy";
        if (Any("ZIP_", "MANIFEST_", "ENTRY_") || code is "SIZE_LIMIT" or "NESTED_ARCHIVE" or "UNEXPECTED_ENTRY" or "INFLATE_LIMIT"
            or "DOCS_ACTIVE_CONTENT" or "BUNDLE_INVALID" or "NO_PACKAGE" or "VALIDATION_FAILED") return "Package structure";
        if (Any("ID_", "NAME_", "MIN_HOST_VERSION_", "AUTHOR_", "CONTACT_", "VISIBILITY_") || code is "VERSION_INVALID" or "VERSION_NOT_INCREMENTED"
            or "VERSION_EXISTS" or "PACKAGE_OWNED_BY_OTHER" or "CHANGELOG_EMPTY" or "DESCRIPTION_TOO_LONG" or "METADATA_INVALID"
            or "ADMIN_UPLOAD_FOR_OWNER" or "CLIENT_ADMIN_ONLY") return "Manifest and versioning";
        return "API, accounts and limits";
    }

    private static readonly Lazy<IReadOnlyList<Rule>> s_rules = new(() =>
    {
        var md = AgentGuide.Markdown("{baseUrl}", "").Replace("\r\n", "\n");
        int a = md.IndexOf("## Complete rule reference", StringComparison.Ordinal);
        int b = md.IndexOf("## Good citizenship", a < 0 ? 0 : a, StringComparison.Ordinal);
        var part = a < 0 ? "" : b < 0 ? md[a..] : md[a..b];
        var list = new List<Rule>();
        var seen = new HashSet<string>();
        foreach (Match m in Regex.Matches(part, @"^\| ([A-Z0-9_]+) \| ([^|]+?) \| (.+?) \|$", RegexOptions.Multiline))
        {
            var code = m.Groups[1].Value;
            if (!seen.Add(code)) continue;
            var sev = m.Groups[2].Value.Trim();
            bool api = sev.Contains('(');
            list.Add(new Rule(code, api ? "error" : sev, m.Groups[3].Value.Trim(), AreaOf(code), api));
        }
        return list;
    });

    /// <summary>All rules of the reference, in reference order.</summary>
    public static IReadOnlyList<Rule> Rules => s_rules.Value;

    // ---- the store's own extensions ---------------------------------------
    private static volatile HouseState s_state = HouseState.Empty;
    public static HouseState House => s_state;

    public static async Task LoadAsync(AppDbContext db)
    {
        var house = (await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == HouseKey))?.Value ?? "";
        var esc = (await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == EscalatedKey))?.Value ?? "";
        s_state = Parse(house, esc);
    }

    public static HouseState Parse(string houseJson, string escalatedJson)
    {
        List<HouseRule> rules = new();
        List<string> escalated = new();
        try { if (houseJson.Length > 0) rules = JsonSerializer.Deserialize<List<HouseRule>>(houseJson) ?? new(); } catch (JsonException) { }
        try { if (escalatedJson.Length > 0) escalated = JsonSerializer.Deserialize<List<string>>(escalatedJson) ?? new(); } catch (JsonException) { }
        // only real warnings can be made stricter
        var warnings = Rules.Where(r => r.Severity == "warning" && !r.Api).Select(r => r.Code).ToHashSet();
        return new HouseState(rules, escalated.Where(warnings.Contains).Distinct().OrderBy(c => c).ToList());
    }

    public static async Task SaveAsync(Services.SettingsService settings, HouseState state)
    {
        await settings.SetAsync(HouseKey, JsonSerializer.Serialize(state.Rules));
        await settings.SetAsync(EscalatedKey, JsonSerializer.Serialize(state.Escalated));
        s_state = Parse(JsonSerializer.Serialize(state.Rules), JsonSerializer.Serialize(state.Escalated));
    }

    /// <summary>Stricter rules: the store's escalated warnings become errors.</summary>
    public static void Escalate(ValidationReport report)
    {
        var esc = s_state.Escalated;
        if (esc.Count == 0) return;
        for (int i = 0; i < report.Findings.Count; i++)
        {
            var f = report.Findings[i];
            if (f.Severity == "warning" && esc.Contains(f.Code))
                report.Findings[i] = f with
                {
                    Severity = "error",
                    Hint = f.Hint + " This store requires it (stricter store rule, see Rules)."
                };
        }
    }

    /// <summary>The extensions as part of the pre-flight checklist ("" without any).</summary>
    public static string ChecklistMarkdown()
    {
        var s = s_state;
        if (s.Rules.Count == 0 && s.Escalated.Count == 0) return "";
        var sb = new StringBuilder();
        sb.Append("**F. Rules of this store** (maintained by the store admins; GET /api/rules)\n");
        foreach (var code in s.Escalated)
        {
            // the rule list is parsed from this very guide: never force it from in here
            var r = s_rules.IsValueCreated ? Rules.FirstOrDefault(x => x.Code == code) : null;
            sb.Append($"- [ ] Mandatory here, not only recommended: {r?.Description ?? code} ({code})\n");
        }
        foreach (var h in s.Rules)
            sb.Append($"- [ ] {h.Title}: {OneLine(h.Text)}" +
                      (h.Kind == "review" ? " (checked by the reviewer at approval)" : " (recommendation)") + "\n");
        sb.Append('\n');
        return sb.ToString();
    }

    public static string OneLine(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();
}
