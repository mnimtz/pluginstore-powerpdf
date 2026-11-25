using AddonStore.Web.Services;
using Xunit;

namespace AddonStore.Web.Tests;

/// <summary>S2.8.6: page names of the customers' own catalog pages (/finance).</summary>
public class CustomerPageTests
{
    [Theory]
    [InlineData("finance", true)]
    [InlineData("finance-2026", true)]
    [InlineData("hr", true)]
    [InlineData("f", false)]                 // too short
    [InlineData("Finance", false)]           // stored and checked in lower case (the page lower-cases the address)
    [InlineData("-finance", false)]
    [InlineData("finance-", false)]
    [InlineData("fin ance", false)]
    [InlineData("finanzämter", false)]
    [InlineData("a234567890123456789012345678901234567890", true)]    // 40: the longest
    [InlineData("a2345678901234567890123456789012345678901", false)]  // 41
    [InlineData("admin", false)]             // the store's own addresses
    [InlineData("api", false)]
    [InlineData("a", false)]
    [InlineData("docs", false)]
    [InlineData("customer-logo", false)]
    [InlineData("download", false)]
    public void Slugs(string slug, bool ok) => Assert.Equal(ok, CustomerPageService.CheckSlug(slug) is null);
}
