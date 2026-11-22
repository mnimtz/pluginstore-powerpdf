using AddonStore.Web.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AddonStore.Web.Tests;

/// <summary>S2.4.0: operating mode values; unknown or empty is always production, the app setting wins.</summary>
public class ServerModeTests
{
    [Theory]
    [InlineData("beta", "beta")]
    [InlineData(" BETA ", "beta")]
    [InlineData("production", "production")]
    [InlineData("Production", "production")]
    [InlineData("", "production")]
    [InlineData(null, "production")]
    [InlineData("maintenance", "production")]
    [InlineData("<script>", "production")]
    public void Values_are_normalized(string? given, string expected) => Assert.Equal(expected, ServerMode.Normalize(given));

    [Theory]
    [InlineData("beta", true)]
    [InlineData("production", true)]
    [InlineData("test", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Known_modes(string? given, bool known) => Assert.Equal(known, ServerMode.IsKnown(given));

    private static IConfiguration Config(string? mode) => new ConfigurationBuilder()
        .AddInMemoryCollection(mode is null ? new Dictionary<string, string?>() : new Dictionary<string, string?> { [ServerMode.ConfigKey] = mode })
        .Build();

    [Fact]
    public void Only_a_known_app_setting_fixes_the_mode()
    {
        Assert.True(ServerMode.IsForced(Config("beta")));
        Assert.True(ServerMode.IsForced(Config("Production")));
        Assert.False(ServerMode.IsForced(Config(null)));
        Assert.False(ServerMode.IsForced(Config("")));
        Assert.False(ServerMode.IsForced(Config("offline")));
    }
}
