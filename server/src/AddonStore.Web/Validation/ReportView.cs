using System.Text.Json;

namespace AddonStore.Web.Validation;

/// <summary>
/// Groups raw validation findings into the check areas shown in the review
/// report: every area appears, green when nothing was found, so reviewers see
/// what WAS checked, not only what failed.
/// </summary>
public static class ReportView
{
    public record Area(string Label, string Status, List<Finding> Findings);

    /// <summary>Area label (English = resx key) and the finding codes it covers.</summary>
    private static readonly (string Label, string[] Codes)[] Areas =
    {
        ("Package structure", new[] { "ZIP_UNREADABLE", "ZIP_SLIP", "SIZE_LIMIT", "MANIFEST_MISSING",
                                      "MANIFEST_INVALID_JSON", "MANIFEST_TOO_LARGE", "ENTRY_TOO_LARGE" }),
        ("Manifest and versioning", new[] { "ID_INVALID", "VERSION_INVALID", "VERSION_NOT_INCREMENTED",
                                      "VERSION_EXISTS", "NAME_MISSING", "CHANGELOG_EMPTY", "MIN_HOST_VERSION_MISSING",
                                      "CATEGORY_MISSING", "CATEGORY_UNKNOWN", "PACKAGE_OWNED_BY_OTHER" }),
        ("Binaries and integrity", new[] { "ARCH_MISSING", "ARCH_ARM64_ABSENT", "FILE_DECLARATION_MISSING",
                                      "FILE_MISSING", "FILENAME_MISMATCH", "HASH_MISSING", "HASH_MISMATCH",
                                      "PE_INVALID", "PE_WRONG_MACHINE", "PE_NOT_DLL", "RESERVED_NAME" }),
        ("Runtime and dependencies", new[] { "PE_DEBUG_RUNTIME", "FOREIGN_DEPENDENCY" }),
        ("Ribbon and layout", new[] { "ATOM_NAMESPACE_MISSING", "ATOM_COLLISION", "ATOM_NOT_SHARED_TAB",
                                      "RESERVED_PANEL_NS", "ICONMODE_SMALL" }),
        ("Languages", new[] { "LANG_TEXT_INCOMPLETE", "LANGS_INCOMPLETE", "LANG_ATOMS_INCONSISTENT" }),
        ("Licenses, legal and privacy", new[] { "LICENSES_MISSING", "LICENSE_GPL_MARKER", "COMPLIANCE_AUDIT_MISSING",
                                      "COMPLIANCE_AUDIT_CONFIRMED", "EXTERNAL_SERVICES_MISSING", "THIRDPARTY_DECLARATION_MISSING", "THIRDPARTY_INVALID",
                                      "LICENSE_NOT_ALLOWED", "LICENSE_NEEDS_REVIEW", "LICENSE_COPYLEFT_BINARY",
                                      "LICENSE_WEAK_COPYLEFT_BINARY", "THIRDPARTY_UNDECLARED", "THIRDPARTY_DETECTED",
                                      "SECRET_DETECTED", "EXTERNAL_SERVICE_UNDECLARED", "THIRDPARTY_TRADEMARK" }),
        ("Icon", new[] { "ICON_MISSING", "ICON_INVALID", "ICON_NOT_SQUARE", "ICON_TOO_LARGE" }),
    };

    /// <summary>
    /// Reads a stored report. Read manually: ValidationReport.Findings is a
    /// getter-only list, which the default JSON deserializer leaves EMPTY, and
    /// an empty report would wrongly render as "all passed".
    /// </summary>
    public static ValidationReport Parse(string json)
    {
        var report = new ValidationReport();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("findings", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var f in arr.EnumerateArray())
                {
                    string S(string n) => f.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    report.Findings.Add(new Finding(S("code"), S("severity"), S("message"), S("hint")));
                }
        }
        catch (JsonException)
        {
            report.Warn("REPORT_UNREADABLE", "The stored check report could not be read.", "Re-run the validation by uploading the package again.");
        }
        return report;
    }

    public static List<Area> Build(ValidationReport report)
    {
        var known = Areas.SelectMany(a => a.Codes).ToHashSet();
        var result = new List<Area>();
        foreach (var (label, codes) in Areas)
        {
            var f = report.Findings.Where(x => codes.Contains(x.Code)).ToList();
            result.Add(new Area(label, StatusOf(f), f));
        }
        var other = report.Findings.Where(x => !known.Contains(x.Code)).ToList();
        if (other.Count > 0) result.Add(new Area("Other checks", StatusOf(other), other));
        return result;
    }

    /// <summary>"error" &gt; "warning" &gt; "info" &gt; "ok". Info marks an area that is
    /// fine for the submitter but carries a notice (e.g. scan not configured).</summary>
    private static string StatusOf(List<Finding> f) =>
        f.Any(x => x.Severity == "error") ? "error" :
        f.Any(x => x.Severity == "warning") ? "warning" :
        f.Any(x => x.Severity == "info") ? "info" : "ok";

    public static (int Errors, int Warnings) Counts(ValidationReport r) =>
        (r.Findings.Count(f => f.Severity == "error"), r.Findings.Count(f => f.Severity == "warning"));
}
