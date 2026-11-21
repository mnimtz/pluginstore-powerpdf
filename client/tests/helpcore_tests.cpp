// helpcore_tests.cpp - the central help's handling of foreign data (store/helpcore.h, C2.1.0):
// JSON reader, help.json rules, HTML allow list, links, pictures, file limits, reparse points and the
// list of installed helps. Build and run: client\tests\run_tests.cmd (console program, no Power PDF).

#include "../store/helpcore.h"
#include <cstdio>
#include <string>

using namespace pshelpcore;

static int g_failed = 0, g_checks = 0;
#define CHECK(cond) do { ++g_checks; if (!(cond)) { ++g_failed; std::printf("FAIL %s:%d  %s\n", __FILE__, __LINE__, #cond); } } while (0)

static bool Has(const std::string& s, const char* x) { return s.find(x) != std::string::npos; }

static std::wstring g_tmp;
static void Put(const std::wstring& rel, const std::string& data)
{
    std::wstring p = g_tmp + L"\\" + rel;
    for (size_t i = g_tmp.size() + 1; i < p.size(); ++i) if (p[i] == L'\\') CreateDirectoryW(p.substr(0, i).c_str(), NULL);
    HANDLE f = CreateFileW(p.c_str(), GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
    DWORD w = 0;
    if (f != INVALID_HANDLE_VALUE) { WriteFile(f, data.data(), (DWORD)data.size(), &w, NULL); CloseHandle(f); }
}

// cmd.exe by its full path (CreateProcessW, no shell lookup), waits for it
static void RunCmd(const std::wstring& args)
{
    wchar_t sys[MAX_PATH] = { 0 };
    GetSystemDirectoryW(sys, MAX_PATH);
    std::wstring exe = std::wstring(sys) + L"\\cmd.exe";
    std::wstring line = L"\"" + exe + L"\" " + args;
    STARTUPINFOW si = { sizeof(si) };
    si.dwFlags = STARTF_USESHOWWINDOW; si.wShowWindow = SW_HIDE;
    PROCESS_INFORMATION pi = {};
    if (CreateProcessW(exe.c_str(), &line[0], NULL, NULL, FALSE, CREATE_NO_WINDOW, NULL, NULL, &si, &pi))
    {
        WaitForSingleObject(pi.hProcess, 30000);
        CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
    }
}

// removes a test folder; a junction inside is removed as a link, never followed
static void DeleteTree(const std::wstring& dir)
{
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW((dir + L"\\*").c_str(), &fd);
    if (h != INVALID_HANDLE_VALUE)
    {
        do
        {
            std::wstring n = fd.cFileName;
            if (n == L"." || n == L"..") continue;
            std::wstring p = dir + L"\\" + n;
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) RemoveDirectoryW(p.c_str());
            else if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) DeleteTree(p);
            else DeleteFileW(p.c_str());
        } while (FindNextFileW(h, &fd));
        FindClose(h);
    }
    RemoveDirectoryW(dir.c_str());
}

static void TestJson()
{
    JVal v;
    CHECK(ParseJson("{\"a\":\"x\",\"n\":1,\"b\":[true,false,null]}", v) && v.type == JVal::TObj && v.Str(L"a") == L"x");
    CHECK(ParseJson("\xEF\xBB\xBF{}", v));                                   // BOM
    CHECK(ParseJson("{\"s\":\"\\u00e4\\ud83d\\ude00\\n\"}", v) && v.Str(L"s") == std::wstring(L"\u00e4\xD83D\xDE00\n"));
    CHECK(!ParseJson("{\"s\":\"\\ud83d\"}", v));                             // lone surrogate
    CHECK(!ParseJson("{\"a\":1,}", v));
    CHECK(!ParseJson("{\"a\":1} x", v));
    CHECK(!ParseJson("", v));
    CHECK(!ParseJson("<html>error</html>", v));
    CHECK(!ParseJson("{\"a\":\"\x01\"}", v));                                 // control character
    CHECK(!ParseJson(std::string(100000, '['), v));                          // depth bomb: refused, no stack overflow
    CHECK(!ParseJson(std::string(50, '[') + std::string(50, ']'), v));       // deeper than 32
    CHECK(ParseJson(std::string(20, '[') + std::string(20, ']'), v));
    CHECK(!ParseJson("{\"a\":01x}", v));
    CHECK(ParseJson("[-1.5e3,0]", v) && v.a.size() == 2 && v.a[0].n == -1500);
    CHECK(!ParseJson("{\"a\":\"\xff\xfe\"}", v));                             // invalid UTF-8
}

static void TestMeta()
{
    HelpMeta m;
    const char* ok = "{\"format\":\"tungsten-addon-help\",\"version\":1,\"addon\":\"com.tungsten.mailmerge\","
                     "\"langs\":[\"en\",\"de\",\"DE\",\"x\",\"de\"],\"topics\":{\"mail\":\"e-mail\",\"Bad\":\"x\",\"evil\":\"../x\",\"n\":3}}";
    CHECK(ParseHelpMeta(ok, m));
    CHECK(m.addon == L"com.tungsten.mailmerge" && m.langs.size() == 2 && m.langs[1] == L"de");
    CHECK(m.topics.size() == 1 && m.topics[0].first == L"mail" && m.topics[0].second == L"e-mail");
    CHECK(ResolveTopic(m, L"mail") == L"e-mail");
    CHECK(ResolveTopic(m, L"some-chapter") == L"some-chapter");
    CHECK(ResolveTopic(m, L"..\\x").empty());
    CHECK(ResolveTopic(m, L"").empty());
    CHECK(PickLang(m, L"de-AT") == L"de");
    CHECK(PickLang(m, L"fr-FR") == L"en");
    CHECK(PickLang(m, L"") == L"en");
    CHECK(!ParseHelpMeta("{\"format\":\"tungsten-addon-help\",\"version\":2,\"addon\":\"a.b.c\",\"langs\":[\"en\"]}", m));
    CHECK(!ParseHelpMeta("{\"format\":\"other\",\"version\":1,\"addon\":\"a.b.c\",\"langs\":[\"en\"]}", m));
    CHECK(!ParseHelpMeta("{\"format\":\"tungsten-addon-help\",\"version\":1,\"addon\":\"a.b.c\",\"langs\":[\"de\"]}", m));   // en missing
    CHECK(!ParseHelpMeta("{\"format\":\"tungsten-addon-help\",\"version\":1,\"addon\":\"..\\\\x\",\"langs\":[\"en\"]}", m));
    CHECK(!ParseHelpMeta("{\"format\":\"tungsten-addon-help\",\"version\":\"1\",\"addon\":\"a.b.c\",\"langs\":[\"en\"]}", m));
    CHECK(!ParseHelpMeta("[]", m));
    CHECK(!ParseHelpMeta("{\"format\":1,\"version\":1,\"addon\":2,\"langs\":\"en\"}", m));   // wrong types: refused, no throw
    CHECK(IsAddonId(L"com.tungsten.pluginstore") && !IsAddonId(L"Com.x.y") && !IsAddonId(L".abc") && !IsAddonId(L"ab"));
    CHECK(IsTopicName(L"store") && !IsTopicName(L"Store") && !IsTopicName(std::wstring(65, L'a')) && !IsTopicName(L"a b"));
}

static void TestClean()
{
    // only the body; scripts, styles and their text go
    CleanResult r = CleanHtml("<html><head><title>T</title><script>alert(1)</script></head><body><h1 id=\"x\">A</h1>"
                              "<script>alert(2)</script><style>p{}</style><p>B</p></body></html>");
    CHECK(r.html == "<h1 id=\"x\">A</h1><p>B</p>");
    // event attributes, styles, javascript: links, foreign classes
    r = CleanHtml("<p onclick=\"x()\" style=\"color:red\" class=\"callout evil steps\">t</p><a href=\"javascript:alert(1)\">j</a>"
                  "<a href=\"JaVaScRiPt:alert(1)\">k</a><a href=\"&#106;avascript:x\">l</a><a href=\"#ok-id\">m</a>");
    CHECK(!Has(r.html, "onclick") && !Has(r.html, "style") && !Has(r.html, "javascript") && !Has(r.html, "evil"));
    CHECK(Has(r.html, "class=\"callout steps\"") && Has(r.html, "href=\"#ok-id\"") && Has(r.html, "<a>j</a>"));
    // https links are kept and collected; other schemes are not
    r = CleanHtml("<a href=\"https://www.tungstenautomation.com/x?a=1&amp;b=2\">t</a><a href=\"http://x.example\">h</a>"
                  "<a href=\"https://user@evil.example/\">u</a><a href=\"https://x.example\\@y\">b</a><a href=\"data:text/html,x\">d</a>");
    CHECK(r.links.size() == 1 && r.links[0] == "https://www.tungstenautomation.com/x?a=1&b=2");
    CHECK(Has(r.html, "href=\"https://www.tungstenautomation.com/x?a=1&amp;b=2\""));
    CHECK(!Has(r.html, "http://") && !Has(r.html, "evil.example") && !Has(r.html, "data:"));
    // forms, frames, svg go with their content; unknown tags go, their text stays
    r = CleanHtml("<form><input value=x>F</form><iframe src=x>I</iframe><svg><script>s</script></svg><custom>C</custom>"
                  "<object data=x>O</object><template><b>T</b></template>");
    CHECK(r.html == "C");
    // nested drop elements are counted
    r = CleanHtml("<svg><svg>a</svg>b</svg>c");
    CHECK(r.html == "c");
    // unbalanced and stray tags are balanced, a lone "<" or "&" is escaped, entities stay
    r = CleanHtml("<ul><li>a<li>b</ul></p>1 < 2 &amp; 3 & 4 &#228; &nbsp;<b>x");
    CHECK(Has(r.html, "1 &lt; 2 &amp; 3 &amp; 4 &#228; &nbsp;") && Has(r.html, "<b>x</b>"));
    CHECK(!Has(r.html, "</p>"));
    // comments and doctype go; attributes without quotes; ids are checked
    r = CleanHtml("<!-- c --><!doctype html><h2 id=a-b_1>x</h2><h2 id=\"bad id\">y</h2><td colspan=2 rowspan=999>z</td>");
    CHECK(r.html == "<h2 id=\"a-b_1\">x</h2><h2>y</h2><td colspan=\"2\">z</td>");
    // attribute quotes cannot break out
    r = CleanHtml("<p id='x\"onload=y'>t</p><img src=\"a.png\" onerror=\"x\">");
    CHECK(!Has(r.html, "onload") && !Has(r.html, "onerror") && !Has(r.html, "<img"));
    // pictures: only relative PNG/JPEG paths through the resolver, data URI out
    int asked = 0;
    auto res = [&](const std::string& rel) -> std::string { ++asked; return rel == "img/ok.png" ? std::string("data:image/png;base64,AAAA") : std::string(); };
    r = CleanHtml("<img src=\"img/ok.png\" alt=\"A&quot;\"><img src=\"../x.png\"><img src=\"https://x/y.png\"><img src=\"c:/x.png\">"
                  "<img src=\"x.svg\"><img src=\"img/missing.png\">", res);
    CHECK(Has(r.html, "<img src=\"data:image/png;base64,AAAA\" alt=\"A&quot;\">"));
    CHECK(asked == 2);   // only the two allowed paths reach the resolver
    CHECK(r.html.find("<img", r.html.find("<img") + 1) == std::string::npos);
    // nesting deeper than the limit cannot blow anything up
    std::string deep;
    for (int i = 0; i < 5000; ++i) deep += "<div>";
    r = CleanHtml(deep + "x");
    CHECK(Has(r.html, "x"));
    // a document without body works too
    r = CleanHtml("<p>only</p>");
    CHECK(r.html == "<p>only</p>");
    // NUL bytes are dropped
    r = CleanHtml(std::string("<p>a\0b</p>", 10));
    CHECK(r.html == "<p>ab</p>");
    CHECK(IsHttpsUrl("https://a.b/c#d") && !IsHttpsUrl("https://a b/") && !IsHttpsUrl("https://") && !IsHttpsUrl("https://a.b/\"x"));
    CHECK(IsHelpImagePath("a/b.JPG") && !IsHelpImagePath("/a.png") && !IsHelpImagePath("a\\b.png") && !IsHelpImagePath("a..png"));
}

static const char* kMeta(const char* id)
{
    static std::string s;
    s = std::string("{\"format\":\"tungsten-addon-help\",\"version\":1,\"addon\":\"") + id + "\",\"langs\":[\"en\",\"de\"],\"topics\":{\"t\":\"ch\"}}";
    return s.c_str();
}

static void TestFiles()
{
    wchar_t tmp[MAX_PATH];
    GetTempPathW(MAX_PATH, tmp);
    g_tmp = std::wstring(tmp) + L"helpcore_test_" + std::to_wstring(GetCurrentProcessId());
    CreateDirectoryW(g_tmp.c_str(), NULL);
    std::wstring P = g_tmp + L"\\Plug-Ins";
    CreateDirectoryW(P.c_str(), NULL);

    // the client's own help
    Put(L"Plug-Ins\\PluginStore\\docs\\help\\help.json", kMeta("com.tungsten.pluginstore"));
    Put(L"Plug-Ins\\PluginStore\\docs\\help\\en.html", "<body><h1 id=\"a\">Own</h1></body>");
    // a valid add-on with picture
    Put(L"Plug-Ins\\Good.zxt", "x");
    Put(L"Plug-Ins\\Good\\manifest.json", "{\"id\":\"com.x.good\",\"name\":{\"en\":\"Good add-on\",\"de\":\"Gutes Add-on\"}}");
    Put(L"Plug-Ins\\Good\\docs\\help\\help.json", kMeta("com.x.good"));
    Put(L"Plug-Ins\\Good\\docs\\help\\en.html", "<body><h1 id=\"ch\">English</h1><img src=\"p/i.png\"><img src=\"p/fake.png\"></body>");
    Put(L"Plug-Ins\\Good\\docs\\help\\de.html", "<body><h1 id=\"ch\">Deutsch</h1><img src=\"p/i.png\"></body>");
    Put(L"Plug-Ins\\Good\\docs\\help\\p\\i.png", std::string("\x89PNG\r\n\x1a\n0000", 12));
    Put(L"Plug-Ins\\Good\\docs\\help\\p\\fake.png", "<script>no png</script>");
    // id mismatch, no manifest, no zxt, bad json, too large html
    Put(L"Plug-Ins\\Liar.zxt", "x");
    Put(L"Plug-Ins\\Liar\\manifest.json", "{\"id\":\"com.x.liar\"}");
    Put(L"Plug-Ins\\Liar\\docs\\help\\help.json", kMeta("com.tungsten.pluginstore"));
    Put(L"Plug-Ins\\Liar\\docs\\help\\en.html", "<h1>x</h1>");
    Put(L"Plug-Ins\\NoMan.zxt", "x");
    Put(L"Plug-Ins\\NoMan\\docs\\help\\help.json", kMeta("com.x.noman"));
    Put(L"Plug-Ins\\NoZxt\\manifest.json", "{\"id\":\"com.x.nozxt\"}");
    Put(L"Plug-Ins\\NoZxt\\docs\\help\\help.json", kMeta("com.x.nozxt"));
    Put(L"Plug-Ins\\BadJson.zxt", "x");
    Put(L"Plug-Ins\\BadJson\\manifest.json", "{\"id\":\"com.x.badjson\"}");
    Put(L"Plug-Ins\\BadJson\\docs\\help\\help.json", "{\"format\":");
    Put(L"Plug-Ins\\Big.zxt", "x");
    Put(L"Plug-Ins\\Big\\manifest.json", "{\"id\":\"com.x.big\",\"name\":\"Big\"}");
    Put(L"Plug-Ins\\Big\\docs\\help\\help.json", kMeta("com.x.big"));
    Put(L"Plug-Ins\\Big\\docs\\help\\en.html", std::string((size_t)kMaxHtmlBytes + 1, 'a'));
    // a junction instead of the docs folder (needs no admin rights): never followed
    Put(L"Plug-Ins\\Link.zxt", "x");
    Put(L"Plug-Ins\\Link\\manifest.json", "{\"id\":\"com.x.link\"}");
    Put(L"Elsewhere\\help\\help.json", kMeta("com.x.link"));
    Put(L"Elsewhere\\help\\en.html", "<h1>secret</h1>");
    RunCmd(L"/c mklink /J \"" + P + L"\\Link\\docs\" \"" + g_tmp + L"\\Elsewhere\"");
    bool junction = (GetFileAttributesW((P + L"\\Link\\docs").c_str()) & FILE_ATTRIBUTE_REPARSE_POINT) != 0;

    std::vector<HelpSource> list = FindHelps(P, L"de-DE", L"com.tungsten.pluginstore", L"Add-on Store");
    std::vector<std::wstring> ids;
    for (const auto& s : list) ids.push_back(s.id);
    CHECK(list.size() == 3);
    CHECK(!list.empty() && list[0].id == L"com.tungsten.pluginstore" && list[0].name == L"Add-on Store" && list[0].lang == L"de");
    bool good = false, big = false, other = false;
    for (const auto& s : list)
    {
        if (s.id == L"com.x.good") { good = true; CHECK(s.name == L"Gutes Add-on" && s.lang == L"de"); }
        else if (s.id == L"com.x.big") big = true;
        else if (s.id != L"com.tungsten.pluginstore") other = true;
    }
    CHECK(good && big && !other);
    CHECK(junction);   // the test really built a junction

    for (const auto& s : list)
    {
        HelpDoc d = LoadDoc(s);
        if (s.id == L"com.x.good")
        {
            CHECK(Has(d.html, "Deutsch") && Has(d.html, "data:image/png;base64,"));
            HelpSource en = s; en.lang = L"en";
            HelpDoc e = LoadDoc(en);
            CHECK(Has(e.html, "English") && e.html.find("<img") == e.html.rfind("<img"));   // the fake PNG is gone
        }
        if (s.id == L"com.x.big") CHECK(d.html.empty());                                    // over 4 MB: not read
        if (s.id == L"com.tungsten.pluginstore") CHECK(Has(d.html, "Own"));                 // de missing: English
    }
    // ReadFileLimited refuses a link AT the file; the folders on the way are SafeDir's job
    std::wstring dir;
    CHECK(!SafeDir(P + L"\\Link", { L"docs", L"help" }, dir));
    CHECK(SafeDir(P + L"\\Good", { L"docs", L"help" }, dir));
    CHECK(ImageDataUri(P + L"\\Good\\docs\\help", "p/i.png").compare(0, 22, "data:image/png;base64,") == 0);
    CHECK(ImageDataUri(P + L"\\Good\\docs\\help", "p/fake.png").empty());
    CHECK(ImageDataUri(P + L"\\Good\\docs\\help", "../manifest.json").empty());
    CHECK(FindHelps(L"", L"en", L"a.b.c", L"x").empty());
    CHECK(FindHelps(g_tmp + L"\\missing", L"en", L"a.b.c", L"x").empty());
    RemoveDirectoryW((P + L"\\Link\\docs").c_str());   // the junction itself, never what it points to
    DeleteTree(g_tmp);
}

int main()
{
    TestJson();
    TestMeta();
    TestClean();
    TestFiles();
    std::printf("helpcore_tests: %d checks, %d failed\n", g_checks, g_failed);
    return g_failed ? 1 : 0;
}
