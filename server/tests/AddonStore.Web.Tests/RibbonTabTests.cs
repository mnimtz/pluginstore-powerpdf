using System.Text.Json;
using AddonStore.Web.Validation;
using Xunit;

namespace AddonStore.Web.Tests;

/// <summary>S2.8.3: manifest field "ribbonTab". Only approved add-ons may put their group into
/// Power PDF's own "Accessibility" tab; every other add-on stays on the shared tab.</summary>
public class RibbonTabTests
{
    private const string Allowed = "com.tungsten.accessibilityreview";

    private static (string Tab, ValidationReport Report) Check(string manifestJson, string id = Allowed,
                                                                string ns = "FeaturePack::AccessibilityReview", bool noUi = false)
    {
        using var doc = JsonDocument.Parse(manifestJson);
        var report = new ValidationReport();
        var tab = PackageValidator.CheckRibbonTab(report, doc.RootElement, id, ns, noUi);
        return (tab, report);
    }

    private static string[] Codes(ValidationReport r) => r.Findings.Select(f => f.Code).ToArray();

    [Fact]
    public void Absent_or_shared_is_the_shared_tab_without_findings()
    {
        var (tab, report) = Check("{}");
        Assert.Equal("shared", tab);
        Assert.Empty(report.Findings);

        (tab, report) = Check("{\"ribbonTab\":\"shared\"}", id: "com.example.other", ns: "FeaturePack::Other");
        Assert.Equal("shared", tab);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void Approved_add_on_gets_the_accessibility_tab()
    {
        var (tab, report) = Check("{\"ribbonTab\":\"accessibility\"}");
        Assert.Equal("accessibility", tab);
        Assert.True(report.Passed);
        Assert.Equal(new[] { "RIBBON_TAB_ACCESSIBILITY" }, Codes(report));
    }

    [Theory]
    [InlineData("com.example.other")]
    [InlineData("com.tungsten.accessibilityreview.t01")]   // clones of the approved id are other add-ons
    [InlineData("COM.TUNGSTEN.ACCESSIBILITYREVIEW")]
    [InlineData("")]
    public void Other_add_ons_are_refused(string id)
    {
        var (tab, report) = Check("{\"ribbonTab\":\"accessibility\"}", id: id);
        Assert.Equal("shared", tab);
        Assert.False(report.Passed);
        Assert.Equal(new[] { "RIBBON_TAB_NOT_ALLOWED" }, Codes(report));
    }

    [Theory]
    [InlineData("\"Accessibility\"")]
    [InlineData("\"tool::accessibility\"")]
    [InlineData("\"\"")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("[\"accessibility\"]")]
    public void Unknown_values_are_refused(string value)
    {
        var (tab, report) = Check("{\"ribbonTab\":" + value + "}");
        Assert.Equal("shared", tab);
        Assert.Equal(new[] { "RIBBON_TAB_INVALID" }, Codes(report));
        Assert.False(report.Passed);
    }

    [Fact]
    public void Long_or_multi_line_values_are_shortened_in_the_message()
    {
        var (_, report) = Check("{\"ribbonTab\":\"line\\nbreak" + new string('x', 200) + "\"}");
        var msg = report.Findings.Single().Message;
        Assert.DoesNotContain("\n", msg);
        Assert.True(msg.Length < 100);
    }

    [Theory]
    [InlineData("", false)]                                   // no namespace
    [InlineData("FeaturePack", false)]                        // the shared tab itself
    [InlineData("FeaturePack::", false)]
    [InlineData("FeaturePack::A::B", false)]                  // a button atom, not a group
    [InlineData("CustomerSign", false)]                       // an own tab
    [InlineData("tool::accessibility", false)]                // the built-in tab is not a namespace
    [InlineData("FeaturePack::AccessibilityReview", true)]    // "ui": "none" has no group
    public void Needs_one_group_on_the_shared_tab(string ns, bool noUi)
    {
        var (tab, report) = Check("{\"ribbonTab\":\"accessibility\"}", ns: ns, noUi: noUi);
        Assert.Equal("shared", tab);
        Assert.Equal(new[] { "RIBBON_TAB_NAMESPACE" }, Codes(report));
    }

    [Fact]
    public void Allow_list_holds_only_the_approved_add_on()
    {
        Assert.Equal(new[] { Allowed }, PackageValidator.AccessibilityTabAllowList.ToArray());
    }

    [Fact]
    public void New_codes_are_in_the_ribbon_area_and_the_rule_reference()
    {
        foreach (var code in new[] { "RIBBON_TAB_INVALID", "RIBBON_TAB_NAMESPACE", "RIBBON_TAB_NOT_ALLOWED", "RIBBON_TAB_ACCESSIBILITY" })
        {
            Assert.Equal("Ribbon and layout", RuleCatalog.AreaOf(code));
            Assert.Contains($"| {code} |", AddonStore.Web.Api.AgentGuide.Markdown("{baseUrl}", ""));
        }
    }
}
