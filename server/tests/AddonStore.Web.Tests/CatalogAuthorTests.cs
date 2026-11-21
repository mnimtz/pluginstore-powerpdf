using System.Text.Json;
using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Xunit;

namespace AddonStore.Web.Tests;

/// <summary>S2.3.3: the company is never shown as an add-on's author; the real author or nothing.</summary>
public class CatalogAuthorTests
{
    [Theory]
    [InlineData("Tungsten Automation")]
    [InlineData("tungsten automation")]
    [InlineData("  Tungsten   Automation  ")]
    [InlineData("Tungsten Automation Corp.")]
    [InlineData("Tungsten Automation, Inc.")]
    [InlineData("Tungsten Automation GmbH")]
    [InlineData("Tungsten")]
    public void Company_names_are_not_authors(string author) => Assert.True(CatalogUi.IsCompanyName(author));

    [Theory]
    [InlineData("Marcus Nimtz")]
    [InlineData("Guillaume Alboin, Tungsten Automation")]
    [InlineData("Tungsten Automation PS Team Benelux")]
    [InlineData("Marco Jansen")]
    [InlineData("")]
    [InlineData(null)]
    public void People_and_teams_are_authors(string? author) => Assert.False(CatalogUi.IsCompanyName(author));

    private static JsonElement Manifest(string? author) =>
        JsonDocument.Parse(author is null ? "{}" : JsonSerializer.Serialize(new { author })).RootElement;

    [Fact]
    public void Manifest_company_falls_back_to_the_account() =>
        Assert.Equal("Marco Jansen", CatalogUi.EffectiveAuthor(new Package(), Manifest("Tungsten Automation"), "Marco Jansen"));

    [Fact]
    public void Catalog_entry_company_falls_back_to_the_manifest() =>
        Assert.Equal("Marcus Nimtz", CatalogUi.EffectiveAuthor(new Package { Author = "Tungsten Automation" }, Manifest("Marcus Nimtz"), "Someone"));

    [Fact]
    public void Real_names_win_as_before()
    {
        Assert.Equal("Team X", CatalogUi.EffectiveAuthor(new Package { Author = "Team X" }, Manifest("Marcus Nimtz"), "Owner"));
        Assert.Equal("Marcus Nimtz", CatalogUi.EffectiveAuthor(new Package(), Manifest("Marcus Nimtz"), "Owner"));
        Assert.Equal("Owner", CatalogUi.EffectiveAuthor(new Package(), Manifest(null), "Owner"));
    }

    [Fact]
    public void Hidden_owner_shows_no_author_instead_of_the_company()
    {
        var hidden = new AppUser { DisplayName = "Marco Jansen", ShowContactPublicly = false };
        Assert.Equal("", CatalogUi.PublicName(hidden));
        Assert.Equal("", CatalogUi.PublicName(null));
        Assert.Equal("", CatalogUi.EffectiveAuthor(new Package(), Manifest("Tungsten Automation"), CatalogUi.PublicName(hidden)));
        Assert.Equal("Marco Jansen", CatalogUi.PublicName(new AppUser { DisplayName = "Marco Jansen", ShowContactPublicly = true }));
    }

    [Fact]
    public void Catalog_entry_refuses_the_company_as_author()
    {
        var issues = PackageMetaService.Validate(new MetaChange { SetAuthor = true, Author = "Tungsten Automation" });
        Assert.Contains(issues, i => i.Code == "AUTHOR_INVALID" && i.Severity == "error");
        Assert.DoesNotContain(PackageMetaService.Validate(new MetaChange { SetAuthor = true, Author = "Marcus Nimtz" }), i => i.Code == "AUTHOR_INVALID");
    }
}
