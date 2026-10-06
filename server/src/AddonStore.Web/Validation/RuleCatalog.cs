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
    /// <summary>The store's own wording of a standard condition (S1.0.10); DefaultAtEdit shows when the standard changed since.</summary>
    public record TextOverride(string Text, string DefaultAtEdit, string By, DateTime At);
    public record HouseState(IReadOnlyList<HouseRule> Rules, IReadOnlyCollection<string> Escalated,
                             IReadOnlyDictionary<string, TextOverride> Texts)
    {
        public static readonly HouseState Empty = new(Array.Empty<HouseRule>(), Array.Empty<string>(),
                                                      new Dictionary<string, TextOverride>());
    }

    public const string HouseKey = "Rules.House";
    public const string EscalatedKey = "Rules.Escalated";
    public const string TextsKey = "Rules.Texts";

    // ---- mandatory conditions (S1.0.10) -------------------------------------
    /// <summary>One numbered condition for the import (A1..E4, from the pre-flight checklist) or the approval (R1.., house rules).</summary>
    public record Condition(string Id, string Group, string GroupTitle, string DefaultText, string Text, string[] Codes,
                            bool Online, bool Edited, string Raw, string? HouseRuleId = null);

    /// <summary>What a reviewer confirms for every version before approving it (editable wording).</summary>
    public static readonly (string Id, string Text)[] ReviewDuties =
    {
        ("R1", "The compliance declaration (thirdParty, complianceAudit, externalServices) matches what the package really contains and does."),
        ("R2", "Every warning in the check report was examined; none hides a real problem (weak copyleft, an undeclared service, a third-party brand)."),
        ("R3", "Name, description, changelog and screenshots describe the add-on correctly and use no other companies' product names or trademarks."),
        ("R4", "The version was tried in Power PDF, or the change carries no functional risk (for example texts only)."),
        ("R5", "An add-on without ribbon buttons (\"ui\": \"none\") says in its description where the function appears in Power PDF and how to switch it off; it does nothing in the background beyond that (otherwise: not applicable)."),
    };

    private static readonly Regex CodeGroups = new(@"\(([A-Z][A-Z0-9_]+(?:, [A-Z][A-Z0-9_]+)*)\)", RegexOptions.Compiled);

    /// <summary>The import conditions A..E of a guide text (raw block kept for replacing it with the store's wording).</summary>
    public static List<Condition> ParseImportConditions(string md)
    {
        var list = new List<Condition>();
        md = md.Replace("\r\n", "\n");
        int a = md.IndexOf("## Pre-flight checklist", StringComparison.Ordinal);
        int b = a < 0 ? -1 : md.IndexOf("## Compliance audit", a, StringComparison.Ordinal);
        if (a < 0 || b < 0) return list;
        var lines = md[a..b].Split('\n');
        string group = "", title = "";
        int n = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            var g = Regex.Match(lines[i], @"^\*\*([A-E])\. (.+?)\*\*");
            if (g.Success) { group = g.Groups[1].Value; title = g.Groups[2].Value; n = 0; continue; }
            if (Regex.IsMatch(lines[i], @"^\*\*[F-Z]\. ")) { group = ""; continue; }
            if (group.Length == 0 || !lines[i].TrimStart().StartsWith("- [ ]", StringComparison.Ordinal)) continue;
            var block = new List<string> { lines[i] };
            while (i + 1 < lines.Length && lines[i + 1].StartsWith("      ", StringComparison.Ordinal) && !lines[i + 1].TrimStart().StartsWith("- [", StringComparison.Ordinal))
                block.Add(lines[++i]);
            var raw = string.Join("\n", block);
            var text = OneLine(string.Join(" ", block.Select(l => l.Trim())).Substring(5));
            var codes = CodeGroups.Matches(text).SelectMany(m => m.Groups[1].Value.Split(", ")).ToArray();
            var clean = Regex.Replace(OneLine(CodeGroups.Replace(text, "")), @"\s+([.;,])(?=\s|$)", "$1");   // "x (CODE)." -> "x."; keeps ".zxt"
            n++;
            list.Add(new Condition(group + n, group, title, clean, clean, codes, text.Contains("checked online"), false, raw));
        }
        return list;
    }

    /// <summary>The import conditions with the store's wording applied.</summary>
    public static List<Condition> ImportConditions()
    {
        var texts = s_state.Texts;
        return ParseImportConditions(AgentGuide.MarkdownCore("{baseUrl}", "")).Select(c =>
            texts.TryGetValue(c.Id, out var o) ? c with { Text = o.Text, Edited = true } : c).ToList();
    }

    /// <summary>The approval conditions: the review duties (store wording applied) and the house rules a reviewer checks.</summary>
    public static List<Condition> ApprovalConditions()
    {
        var texts = s_state.Texts;
        var list = ReviewDuties.Select(d => texts.TryGetValue(d.Id, out var o)
            ? new Condition(d.Id, "R", "Approval", d.Text, o.Text, Array.Empty<string>(), false, true, "")
            : new Condition(d.Id, "R", "Approval", d.Text, d.Text, Array.Empty<string>(), false, false, "")).ToList();
        int n = 0;
        foreach (var h in s_state.Rules.Where(r => r.Kind == "review"))
            list.Add(new Condition("H" + (++n), "H", h.Area, h.Text, h.Title + ": " + OneLine(h.Text), Array.Empty<string>(), false, false, "", h.Id));
        return list;
    }

    /// <summary>The store's wording of the import conditions in a guide text (codes stay, they are what the check reports).</summary>
    public static string ApplyTextOverrides(string md)
    {
        var texts = s_state.Texts;
        if (texts.Count == 0) return md;
        var nl = md.Contains("\r\n") ? "\r\n" : "\n";
        var norm = md.Replace("\r\n", "\n");
        foreach (var c in ParseImportConditions(norm))
            if (texts.TryGetValue(c.Id, out var o))
                norm = norm.Replace(c.Raw, "- [ ] " + OneLine(o.Text) + (c.Codes.Length > 0 ? " (" + string.Join(", ", c.Codes) + ")" : "")
                                          + (c.Online && !o.Text.Contains("online") ? " (checked online)" : ""));
        return nl == "\n" ? norm : norm.Replace("\n", nl);
    }

    /// <summary>Areas in display order (English = resx key).</summary>
    public static readonly string[] Areas =
    {
        "Package structure", "Manifest and versioning", "Categories", "Languages", "Binaries and integrity",
        "Runtime and dependencies", "Ribbon and layout", "Licenses, legal and privacy", "Icon and screenshots",
        "Security and system access", "Source code", "Customer deliveries", "API, accounts and limits",
    };

    public static string AreaOf(string code)
    {
        bool Any(params string[] prefixes) => prefixes.Any(p => code.StartsWith(p, StringComparison.Ordinal));
        if (code is "NETWORK_UNDECLARED" or "PROCESS_INJECTION" or "RUNTIME_DOWNLOAD" or "PROCESS_START" or "PERSISTENCE"
            or "INSECURE_HTTP" or "TLS_CHECK_DISABLED" or "PACKAGE_BLOCKED") return "Security and system access";
        if (Any("SOURCE_") || code is "LICENSE_COPYLEFT_SOURCE" or "LICENSE_WEAK_COPYLEFT_SOURCE" or "THIRDPARTY_SOURCE_DETECTED") return "Source code";
        if (Any("CUSTOMER_", "DELIVERY_") || code is "PROMOTE_NOTHING" or "CODE_NOT_FOUND" or "CODE_MISSING") return "Customer deliveries";
        if (Any("CATEGORY_")) return "Categories";
        if (Any("ICON_", "SCREENSHOT")) return "Icon and screenshots";
        if (Any("LANG", "UI_LANGS", "UI_STRINGS") || code is "NAME_NOT_LOCALIZED") return "Languages";
        if (Any("ATOM_", "OWN_TAB_", "UI_NONE") || code is "UI_INVALID" or "VISIBILITY_OWN_TAB" or "RESERVED_PANEL_NS" or "ICONMODE_SMALL" or "UILAYOUT_MISSING") return "Ribbon and layout";
        if (Any("BIN_") || code is "PE_DEBUG_RUNTIME" or "FOREIGN_DEPENDENCY") return "Runtime and dependencies";   // bin/: S1.4.0
        if (Any("ARCH_", "HASH_", "PE_", "VERSIONINFO_", "ZXT_NAME_") || code is "FILE_DECLARATION_MISSING" or "FILE_MISSING" or "FILENAME_MISMATCH" or "RESERVED_NAME")
            return "Binaries and integrity";
        if (Any("LICENSE", "COMPLIANCE_", "EXTERNAL_SERVICE", "THIRDPARTY_") || code is "SECRET_DETECTED") return "Licenses, legal and privacy";
        if (Any("ZIP_", "MANIFEST_", "ENTRY_") || code is "SIZE_LIMIT" or "NESTED_ARCHIVE" or "UNEXPECTED_ENTRY" or "INFLATE_LIMIT"
            or "DOCS_ACTIVE_CONTENT" or "BUNDLE_INVALID" or "NO_PACKAGE" or "VALIDATION_FAILED") return "Package structure";
        if (Any("ID_", "NAME_", "MIN_HOST_VERSION_", "AUTHOR_", "CONTACT_", "VISIBILITY_", "TEXT_CONTROL_") || code is "VERSION_INVALID" or "VERSION_NOT_INCREMENTED"
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

    /// <summary>Version of the running server (set at start-up), recorded in every rules snapshot.</summary>
    public static string ServerVersion { get; set; } = "0.0.0-dev";

    /// <summary>
    /// The rules in force right now (S1.3.0), stored with every checked upload and every review
    /// decision: which server version checked, a hash over every rule with its effective severity,
    /// the store's house rules, the warnings made mandatory and the wording of every condition.
    /// The hash tells at a glance whether two versions were checked against the same rules.
    /// </summary>
    public static object Snapshot()
    {
        var esc = s_state.Escalated.OrderBy(c => c, StringComparer.Ordinal).ToList();
        var rules = Rules.Select(r => new { code = r.Code, severity = esc.Contains(r.Code) ? "error" : r.Severity, text = r.Description }).ToList();
        var house = s_state.Rules.Select(h => new { id = h.Id, area = h.Area, kind = h.Kind, title = h.Title, text = h.Text }).ToList();
        var conditions = ImportConditions().Concat(ApprovalConditions()).Select(c => new { id = c.Id, text = c.Text }).ToList();
        var canonical = System.Text.Json.JsonSerializer.Serialize(new { rules, house, escalated = esc, conditions });
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new
        {
            serverVersion = ServerVersion,
            at = DateTime.UtcNow,
            rulesHash = hash,
            ruleCount = rules.Count,
            houseRules = house,
            mandatoryWarnings = esc,
            conditions,
        };
    }

    public static string SnapshotJson() => System.Text.Json.JsonSerializer.Serialize(Snapshot());

    public static async Task LoadAsync(AppDbContext db)
    {
        var house = (await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == HouseKey))?.Value ?? "";
        var esc = (await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == EscalatedKey))?.Value ?? "";
        var texts = (await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == TextsKey))?.Value ?? "";
        s_state = Parse(house, esc, texts);
    }

    public static HouseState Parse(string houseJson, string escalatedJson, string textsJson = "")
    {
        List<HouseRule> rules = new();
        List<string> escalated = new();
        Dictionary<string, TextOverride> texts = new();
        try { if (houseJson.Length > 0) rules = JsonSerializer.Deserialize<List<HouseRule>>(houseJson) ?? new(); } catch (JsonException) { }
        try { if (escalatedJson.Length > 0) escalated = JsonSerializer.Deserialize<List<string>>(escalatedJson) ?? new(); } catch (JsonException) { }
        try { if (textsJson.Length > 0) texts = JsonSerializer.Deserialize<Dictionary<string, TextOverride>>(textsJson) ?? new(); } catch (JsonException) { }
        // only real warnings can be made stricter
        var warnings = Rules.Where(r => r.Severity == "warning" && !r.Api).Select(r => r.Code).ToHashSet();
        return new HouseState(rules, escalated.Where(warnings.Contains).Distinct().OrderBy(c => c).ToList(),
                              texts.Where(kv => Regex.IsMatch(kv.Key, "^[A-ER][0-9]{1,2}$") && kv.Value.Text.Trim().Length > 0)
                                   .ToDictionary(kv => kv.Key, kv => kv.Value));
    }

    public static async Task SaveAsync(Services.SettingsService settings, HouseState state)
    {
        var rules = JsonSerializer.Serialize(state.Rules);
        var esc = JsonSerializer.Serialize(state.Escalated);
        var texts = JsonSerializer.Serialize(state.Texts);
        await settings.SetAsync(HouseKey, rules);
        await settings.SetAsync(EscalatedKey, esc);
        await settings.SetAsync(TextsKey, texts);
        s_state = Parse(rules, esc, texts);
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
        var sb = new StringBuilder();
        var advice = s.Rules.Where(h => h.Kind != "review").ToList();
        if (s.Escalated.Count > 0 || advice.Count > 0)
        {
            sb.Append("**F. Rules of this store** (maintained by the store admins; GET /api/rules)\n");
            foreach (var code in s.Escalated)
            {
                // the rule list is parsed from this very guide: never force it from in here
                var r = s_rules.IsValueCreated ? Rules.FirstOrDefault(x => x.Code == code) : null;
                sb.Append($"- [ ] Mandatory here, not only recommended: {r?.Description ?? code} ({code})\n");
            }
            foreach (var h in advice)
                sb.Append($"- [ ] {h.Title}: {OneLine(h.Text)} (recommendation)\n");
            sb.Append('\n');
        }
        // what the reviewer confirms before a version goes live: prepare for it
        sb.Append("**G. Approval conditions** (the reviewer confirms each before a version goes live; prepare for them)\n");
        foreach (var c in ApprovalConditions())
            sb.Append($"- [ ] {OneLine(c.Text)} (checked by the reviewer at approval)\n");
        sb.Append('\n');
        return sb.ToString();
    }

    public static string OneLine(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();
}
