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

    [Fact]
    public void SameHashAsTheClientSends()
    {
        // the store client sends the code upper case without spaces; hyphens and case never change the hash
        Assert.Equal(CustomerService.Hash("FINANCE"), CustomerService.Hash("finance"));
        Assert.Equal(CustomerService.Hash("FINANCE-2026"), CustomerService.Hash("finance2026"));
        Assert.NotEqual(CustomerService.Hash("FINANCE"), CustomerService.Hash("FINANCES"));
    }
}
