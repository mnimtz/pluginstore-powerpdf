using System.IO.Compression;
using System.Text;
using AddonStore.Web.Validation;
using Xunit;

namespace AddonStore.Web.Tests;

/// <summary>S2.7.0: the help rule of the upload check (form, limits, topics, version history, comparison hash).</summary>
public class HelpCheckTests
{
    private const string Id = "com.example.demo";

    private static Dictionary<string, byte[]?> Files(params (string Name, string Text)[] files) =>
        files.ToDictionary(f => f.Name, f => (byte[]?)Encoding.UTF8.GetBytes(f.Text), StringComparer.OrdinalIgnoreCase);

    private static string Json(string langs = "[\"en\",\"de\"]", string addon = Id, string extra = "") =>
        "{\"format\":\"tungsten-addon-help\",\"version\":1,\"addon\":\"" + addon + "\",\"langs\":" + langs + extra + "}";

    private const string En = "<h1 id=\"overview\">Demo</h1><p>Text</p><h1 id=\"version-history\">Version history</h1><h2>1.3.0</h2>";

    [Fact]
    public void Valid_help()
    {
        var r = HelpCheck.Inspect(Files(("docs/help/help.json", Json(extra: ",\"topics\":{\"overview\":\"overview\",\"history\":\"version-history\"}")),
                                        ("docs/help/en.html", En), ("docs/help/de.html", En)), Id);
        Assert.True(r.Present);
        Assert.Empty(r.Problems);
        Assert.Equal(new[] { "en", "de" }, r.Langs);
        Assert.Empty(r.UnknownTopics);
        Assert.Equal(64, r.Hash.Length);
    }

    [Fact]
    public void Help_json_with_a_byte_order_mark_is_valid()
    {
        // PowerShell 5.1 writes UTF-8 with a BOM; the client's parser skips it (store/helpcore.h), so the check must too
        var files = Files(("docs/help/en.html", En));
        files["docs/help/help.json"] = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(Json("[\"en\"]"))).ToArray();
        var r = HelpCheck.Inspect(files, Id);
        Assert.Empty(r.Problems);
        Assert.Equal(new[] { "en" }, r.Langs);
    }

    [Fact]
    public void Version_written_as_one_point_zero_is_valid()
    {
        var json = "{\"format\":\"tungsten-addon-help\",\"version\":1.0,\"addon\":\"" + Id + "\",\"langs\":[\"en\"]}";
        Assert.Empty(HelpCheck.Inspect(Files(("docs/help/help.json", json), ("docs/help/en.html", En)), Id).Problems);
    }

    [Fact]
    public void Deeply_nested_help_json_is_reported_not_thrown()
    {
        var deep = string.Concat(Enumerable.Repeat("{\"a\":", 100)) + "1" + new string('}', 100);
        var r = HelpCheck.Inspect(Files(("docs/help/help.json", deep), ("docs/help/en.html", En)), Id);
        Assert.Contains(r.Problems, p => p.Contains("not valid JSON"));
    }

    [Fact]
    public void No_help_at_all()
    {
        var r = HelpCheck.Inspect(Files(("docs/manual.pdf", "x")), Id);
        Assert.False(r.Present);
        Assert.Empty(r.Problems);
        Assert.Equal("", r.Hash);
    }

    [Fact]
    public void Help_folder_without_help_json_is_invalid()
    {
        var r = HelpCheck.Inspect(Files(("docs/help/en.html", En)), Id);
        Assert.False(r.Present);
        Assert.Contains(r.Problems, p => p.Contains("no help.json"));
    }

    [Theory]
    [InlineData("{\"format\":\"other\",\"version\":1,\"addon\":\"com.example.demo\",\"langs\":[\"en\"]}", "\"format\"")]
    [InlineData("{\"format\":\"tungsten-addon-help\",\"version\":2,\"addon\":\"com.example.demo\",\"langs\":[\"en\"]}", "\"version\"")]
    [InlineData("{\"format\":\"tungsten-addon-help\",\"version\":1,\"addon\":\"com.other\",\"langs\":[\"en\"]}", "\"addon\"")]
    [InlineData("{\"format\":\"tungsten-addon-help\",\"version\":1,\"addon\":\"com.example.demo\",\"langs\":[\"de\"]}", "must contain \"en\"")]
    [InlineData("{\"format\":\"tungsten-addon-help\",\"version\":1,\"addon\":\"com.example.demo\",\"langs\":\"en\"}", "\"langs\" must be an array")]
    [InlineData("not json", "not valid JSON")]
    [InlineData("[1,2]", "not a JSON object")]
    public void Broken_help_json(string json, string expected)
    {
        var r = HelpCheck.Inspect(Files(("docs/help/help.json", json), ("docs/help/en.html", En), ("docs/help/de.html", En)), Id);
        Assert.True(r.Present);
        Assert.Contains(r.Problems, p => p.Contains(expected));
    }

    [Theory]
    [InlineData("[\"en\",\"zh-Hans\"]", "", "zh-Hans")]
    [InlineData("[\"en\",\"../x\"]", "", "../x")]
    [InlineData("[\"en\"]", ",\"topics\":[]", "\"topics\"")]
    [InlineData("[\"en\"]", ",\"topics\":{\"Bad Name\":\"overview\"}", "Bad Name")]
    public void Entries_the_window_skips_are_warnings_not_errors(string langs, string extra, string expected)
    {
        // the client's parser (store/helpcore.h) skips these silently and still shows the help
        var r = HelpCheck.Inspect(Files(("docs/help/help.json", Json(langs, extra: extra)), ("docs/help/en.html", En)), Id);
        Assert.Empty(r.Problems);
        Assert.Contains(r.Ignored, i => i.Contains(expected));
        Assert.Equal(new[] { "en" }, r.Langs);
    }

    [Fact]
    public void Listed_language_needs_its_page()
    {
        var r = HelpCheck.Inspect(Files(("docs/help/help.json", Json("[\"en\",\"fr\"]")), ("docs/help/en.html", En)), Id);
        Assert.Contains(r.Problems, p => p.Contains("docs/help/fr.html is missing"));
        Assert.Equal(new[] { "en" }, r.Langs);
    }

    [Fact]
    public void Limits_of_the_help_window()
    {
        var files = Files(("docs/help/help.json", Json("[\"en\"]")), ("docs/help/en.html", En));
        files["docs/help/img/big.png"] = new byte[HelpCheck.MaxImageBytes + 1];
        files["docs/help/de.html"] = null;   // larger than could be read
        var r = HelpCheck.Inspect(files, Id);
        Assert.Contains(r.Problems, p => p.Contains("big.png") && p.Contains("2 MB"));
        var big = Files(("docs/help/help.json", Json("[\"en\"]")));
        big["docs/help/en.html"] = null;
        Assert.Contains(HelpCheck.Inspect(big, Id).Problems, p => p.Contains("en.html is larger than 4 MB"));
        var hugeJson = Files(("docs/help/en.html", En));
        hugeJson["docs/help/help.json"] = new byte[HelpCheck.MaxJsonBytes + 1];
        Assert.Contains(HelpCheck.Inspect(hugeJson, Id).Problems, p => p.Contains("256 KB"));
    }

    [Fact]
    public void Topics_must_point_to_existing_chapters()
    {
        var r = HelpCheck.Inspect(Files(("docs/help/help.json", Json("[\"en\"]", extra: ",\"topics\":{\"settings\":\"settings\",\"overview\":\"overview\"}")),
                                        ("docs/help/en.html", En)), Id);
        Assert.Empty(r.Problems);
        Assert.Equal(new[] { "settings" }, r.UnknownTopics);
    }

    [Theory]
    [InlineData("<h2>1.3.0</h2>", "1.3.0", true)]
    [InlineData("<h2>Version 1.3</h2>", "1.3.0", true)]
    [InlineData("<h2>1.3.0</h2>", "1.3.1", false)]
    [InlineData("<h2>11.3.0</h2>", "1.3.0", false)]
    [InlineData("<h2>1.3.0.5</h2>", "1.3.0", false)]
    [InlineData("<h2>2026.4.0.3</h2>", "2026.4.0.3", true)]
    [InlineData("", "1.0.0", false)]
    public void Version_in_the_history(string text, string version, bool expected) =>
        Assert.Equal(expected, HelpCheck.MentionsVersion(text, version));

    [Fact]
    public void Hash_depends_on_content_not_order_or_case_of_names()
    {
        var a = Files(("docs/help/help.json", "1"), ("docs/help/en.html", "2"));
        var b = Files(("docs/help/EN.html", "2"), ("docs/help/help.json", "1"));
        var c = Files(("docs/help/help.json", "1"), ("docs/help/en.html", "3"));
        Assert.Equal(HelpCheck.HashOf(a), HelpCheck.HashOf(b));
        Assert.NotEqual(HelpCheck.HashOf(a), HelpCheck.HashOf(c));
        Assert.Equal("", HelpCheck.HashOf(Files(("docs/other.txt", "x"))));
    }

    [Fact]
    public void Stored_package_gives_the_same_hash()
    {
        var path = Path.Combine(Path.GetTempPath(), "helpcheck-" + Guid.NewGuid().ToString("N") + ".ppak");
        try
        {
            var files = Files(("docs/help/help.json", Json("[\"en\"]")), ("docs/help/en.html", En));
            using (var z = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                foreach (var f in files)
                    using (var s = z.CreateEntry(f.Key).Open()) s.Write(f.Value!);
                using (var s = new StreamWriter(z.CreateEntry("manifest.json").Open())) s.Write("{}");
            }
            var read = HelpCheck.ReadFromPackage(path);
            Assert.Equal(2, read.Count);
            Assert.Equal(HelpCheck.HashOf(files), HelpCheck.HashOf(read));
            Assert.Empty(HelpCheck.ReadFromPackage(path + ".missing"));
        }
        finally { File.Delete(path); }
    }
}
