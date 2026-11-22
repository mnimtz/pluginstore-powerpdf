using AddonStore.Web.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AddonStore.Web.Tests;

/// <summary>S2.6.0: which requests the maintenance pauses, which stay open, and the note cleaning.</summary>
public class MaintenanceGateTests
{
    [Theory]
    [InlineData("POST", true)]
    [InlineData("PUT", true)]
    [InlineData("PATCH", true)]
    [InlineData("DELETE", true)]
    [InlineData("GET", false)]
    [InlineData("HEAD", false)]
    [InlineData("OPTIONS", false)]
    public void Writes_are_the_changing_methods(string method, bool write) => Assert.Equal(write, MaintenanceGate.IsWrite(method));

    [Theory]
    [InlineData("/Account/Login", true)]
    [InlineData("/account/logout", true)]
    [InlineData("/signin-oidc", true)]
    [InlineData("/api/packages/validate", true)]
    [InlineData("/api/client/inventory", true)]
    [InlineData("/api/deliveries/release", true)]
    [InlineData("/a/smartbookmarks/click", true)]
    [InlineData("/api/packages", false)]
    [InlineData("/api/packages/com.example/rating", false)]
    [InlineData("/api/packages/com.example/feedback", false)]
    [InlineData("/Account/Register", false)]
    [InlineData("/Account/Forgot", false)]
    [InlineData("/Dashboard", false)]
    [InlineData("/Admin/Settings", false)]
    [InlineData("/a/smartbookmarks", false)]
    [InlineData("/api/federation/handover", false)]
    [InlineData("/sendto/transfers", false)]
    public void Open_paths(string path, bool open) => Assert.Equal(open, MaintenanceGate.IsOpen(new PathString(path)));

    [Fact]
    public void Retry_after_follows_the_expected_end()
    {
        var now = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal(900, MaintenanceGate.RetryAfterSeconds(null, now));
        Assert.Equal(900, MaintenanceGate.RetryAfterSeconds(now.AddMinutes(-5), now));
        Assert.Equal(60, MaintenanceGate.RetryAfterSeconds(now.AddSeconds(10), now));
        Assert.Equal(3600, MaintenanceGate.RetryAfterSeconds(now.AddHours(1), now));
        Assert.Equal(86400, MaintenanceGate.RetryAfterSeconds(now.AddDays(3), now));
    }

    [Fact]
    public void Stored_end_is_read_back_as_utc()
    {
        var now = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 11, 22, 12, 30, 0, DateTimeKind.Utc);
        var parsed = ServerMode.ParseUntil(end.ToString("o", System.Globalization.CultureInfo.InvariantCulture), now);
        Assert.Equal(end, parsed);
        Assert.Equal(DateTimeKind.Utc, parsed!.Value.Kind);
        Assert.Null(ServerMode.ParseUntil(end.ToString("o", System.Globalization.CultureInfo.InvariantCulture), end.AddMinutes(1)));
        Assert.Null(ServerMode.ParseUntil("", now));
        Assert.Null(ServerMode.ParseUntil("not a date", now));
    }

    [Fact]
    public void Notes_are_one_clean_line()
    {
        Assert.Equal("until 6 pm", ServerMode.CleanNote("  until 6 pm\r\n"));
        Assert.Equal("ab", ServerMode.CleanNote("a‮b"));
        Assert.Equal(ServerMode.MaxNote, ServerMode.CleanNote(new string('x', 500)).Length);
        Assert.Equal("", ServerMode.CleanNote(null));
    }
}
