using System.Globalization;

namespace AddonStore.Web.Services;

/// <summary>
/// Language names written out in the page's language (S0.18.2): "Französisch" on a
/// German page, "French" on an English one. The names come from the runtime's
/// language data (ICU), no table of our own.
/// </summary>
public static class LanguageNames
{
    /// <summary>The name of a language code in the current UI language, capitalized.</summary>
    public static string Of(string code)
    {
        try
        {
            var name = CultureInfo.GetCultureInfo(code).DisplayName;
            return name.Length == 0 ? code : char.ToUpper(name[0], CultureInfo.CurrentUICulture) + name[1..];
        }
        catch (CultureNotFoundException) { return code; }
    }

    /// <summary>The 16 store languages as (code, name), sorted by name in the current UI language.</summary>
    public static IEnumerable<(string Code, string Name)> Options() =>
        Validation.PackageValidator.RequiredLanguages
            .Select(c => (c, Of(c)))
            .OrderBy(x => x.Item2, StringComparer.Create(CultureInfo.CurrentUICulture, true));
}
