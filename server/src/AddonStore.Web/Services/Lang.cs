using System.Globalization;

namespace AddonStore.Web.Services;

/// <summary>
/// The 21 Power PDF UI languages (S1.2.0): the 16 European ones every add-on
/// must ship, and five more (Simplified and Traditional Chinese, Japanese,
/// Korean, Arabic) that are recommended. Language codes as in manifests and
/// URLs: two letters, except "nb" for Norwegian and "zh-Hans"/"zh-Hant".
/// </summary>
public static class Lang
{
    /// <summary>Every language of the portal, the client and the catalog texts, in display order.</summary>
    public static readonly string[] Ui =
        Validation.PackageValidator.RequiredLanguages.Concat(Validation.PackageValidator.ExtendedLanguages).ToArray();

    /// <summary>Native name of each language, for selectors.</summary>
    public static readonly IReadOnlyDictionary<string, string> NativeNames = new Dictionary<string, string>
    {
        ["en"] = "English", ["de"] = "Deutsch", ["fr"] = "Français", ["it"] = "Italiano", ["es"] = "Español",
        ["nl"] = "Nederlands", ["pt"] = "Português", ["da"] = "Dansk", ["fi"] = "Suomi", ["nb"] = "Norsk bokmål",
        ["sv"] = "Svenska", ["pl"] = "Polski", ["cs"] = "Čeština", ["hu"] = "Magyar", ["ru"] = "Русский", ["tr"] = "Türkçe",
        ["zh-Hans"] = "简体中文", ["zh-Hant"] = "繁體中文", ["ja"] = "日本語", ["ko"] = "한국어", ["ar"] = "العربية",
    };

    /// <summary>Short label of the language selector in the header.</summary>
    public static string ShortLabel(string code) => code switch
    {
        "zh-Hans" => "简体", "zh-Hant" => "繁體", _ => code.ToUpperInvariant(),
    };

    /// <summary>Our language code of a culture: "zh-Hans"/"zh-Hant" for Chinese, "nb" for Norwegian, else two letters.</summary>
    public static string Code(CultureInfo c)
    {
        var name = c.Name;
        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return Normalize(name);
        var two = c.TwoLetterISOLanguageName;
        return two is "no" or "nn" ? "nb" : two;
    }

    /// <summary>The language code of the current request.</summary>
    public static string Current => Code(CultureInfo.CurrentUICulture);

    /// <summary>Right-to-left script (Arabic).</summary>
    public static bool IsRtl(string code) => code == "ar";
    public static bool CurrentIsRtl => IsRtl(Current);

    /// <summary>
    /// Any spelling to our code: "zh", "zh-CN", "zh-SG", "zh-Hans" become "zh-Hans"; "zh-TW", "zh-HK",
    /// "zh-MO", "zh-Hant" become "zh-Hant"; "no"/"nn" become "nb"; others their first two letters.
    /// </summary>
    public static string Normalize(string? code)
    {
        var c = (code ?? "").Trim().Replace('_', '-');
        var lower = c.ToLowerInvariant();
        if (lower.StartsWith("zh"))
            return lower.Contains("hant") || lower.EndsWith("-tw") || lower.EndsWith("-hk") || lower.EndsWith("-mo") ? "zh-Hant" : "zh-Hans";
        if (lower.Length > 2) lower = lower[..2];
        return lower is "no" or "nn" ? "nb" : lower;
    }
}
