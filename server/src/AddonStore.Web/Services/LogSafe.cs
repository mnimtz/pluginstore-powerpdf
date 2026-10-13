namespace AddonStore.Web.Services;

/// <summary>
/// Values from requests, packages, the database or other servers on their way into the log
/// (S1.26.0, CWE-117): line breaks and other control characters become "_", so a value can
/// never start a forged log line; long values are cut.
/// </summary>
public static class LogSafe
{
    public static string Of(object? value, int max = 200)
    {
        var s = value?.ToString() ?? "";
        if (s.Length > max) s = s[..max] + "...";
        return string.Create(s.Length, s, static (span, src) =>
        {
            for (var i = 0; i < src.Length; i++)
                span[i] = char.IsControl(src[i]) || src[i] == (char)0x2028 || src[i] == (char)0x2029 ? '_' : src[i];
        });
    }
}
