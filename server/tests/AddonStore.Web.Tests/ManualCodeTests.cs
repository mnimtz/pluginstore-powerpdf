using AddonStore.Web.Services;
using Xunit;

namespace AddonStore.Web.Tests;

/// <summary>S2.8.5: customer codes of an admin's own choice (e.g. FINANCE for a campaign).</summary>
public class ManualCodeTests
{
    [Theory]
    [InlineData("FINANCE", true)]
    [InlineData("finance", true)]                 // case does not count
    [InlineData("Finance-2026", true)]
    [InlineData("HR-24", false)]                  // "HR24": 4 letters and digits, below the minimum of 5
    [InlineData("HR-2026", true)]
    [InlineData("FIN", false)]                    // too short
    [InlineData("F-I-N-A", false)]                // hyphens do not count: 4
    [InlineData("12345", false)]                  // no letter
    [InlineData("FINANCE TEAM", false)]           // space
    [InlineData("FINANZÄMTER", false)]            // only A to Z
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ1234567", false)]   // 33 letters and digits
    [InlineData("", false)]
    public void Rules(string code, bool ok) => Assert.Equal(ok, CustomerService.CheckManualCode(code) is null);

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]                                   // IPv4 in IPv6 form counts as IPv4
    [InlineData("2001:db8:1234:5678:aaaa:bbbb:cccc:dddd", "20010db812345678::/64")]
    [InlineData("2001:db8:1234:5678::1", "20010db812345678::/64")]                     // same /64: one counter
    public void GuessingLimitCountsIpv6PerNetwork(string ip, string key) =>
        Assert.Equal(key, CustomerService.GuessKey(System.Net.IPAddress.Parse(ip)));

    [Fact]
    public void SameHashAsTheClientSends()
    {
        // the store client sends the code upper case without spaces; hyphens and case never change the hash
        Assert.Equal(CustomerService.Hash("FINANCE"), CustomerService.Hash("finance"));
        Assert.Equal(CustomerService.Hash("FINANCE-2026"), CustomerService.Hash("finance2026"));
        Assert.NotEqual(CustomerService.Hash("FINANCE"), CustomerService.Hash("FINANCES"));
    }
}
