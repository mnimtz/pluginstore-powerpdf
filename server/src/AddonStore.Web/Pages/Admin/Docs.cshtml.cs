using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AddonStore.Web.Pages.Admin;

/// <summary>
/// One place for every document of the store (S1.0.12): the PDFs, the rules,
/// the guides AI assistants read and the API tools. Everyone signed in reads it;
/// every link opens in a new tab.
/// </summary>
public class DocsModel : PageModel
{
    public record DocLink(string Icon, string Title, string Text, string Url, string? Badge = null);
    public record DocGroup(string Key, string Title, string Intro, DocLink[] Links);

    public static readonly DocGroup[] Groups =
    {
        new("pdf", "Documents (PDF)", "In English, generated from the live rules of this store.", new DocLink[]
        {
            new("pdf", "Mandatory requirements for add-ons", "Every condition a plug-in must meet for import and approval, in compact form.", "/docs/AddonStore-Requirements.pdf", "PDF"),
            new("book", "Manual: API, rules and publishing guide", "The complete reference: principles, developer and agent guide, every rule by area, API and manifest.", "/docs/AddonStore-Manual.pdf", "PDF"),
        }),
        new("rules", "Rules and conditions", "What the store checks on every upload and what reviewers confirm at approval.", new DocLink[]
        {
            new("shield", "Rules", "All rules by area, the mandatory conditions and the rules of this store; admins edit them here.", "/Admin/Rules"),
            new("check", "Pre-flight checklist", "The hard rules as a short checklist to work through before an upload.", "/agent-guide/checklist"),
            new("data", "Rules as data", "Every rule and condition as JSON, for tools and AI assistants.", "/api/rules", "JSON"),
        }),
        new("ai", "For AI assistants", "Readable without signing in; works with Claude, ChatGPT, Gemini, Copilot and others.", new DocLink[]
        {
            new("robot", "Agent guide", "The full guide AI assistants follow to build, check and publish an add-on.", "/agent-guide"),
            new("download", "Agent guide as a file", "The same guide as Markdown, to save in the plug-in project.", "/api/agent-guide?download=1", "MD"),
            new("package", "Manual upload package", "Fallback when an assistant cannot reach the store: it builds the finished upload ZIP.", "/api/agent-guide/manual-upload", "MD"),
            new("compass", "Entry point for AI (llms.txt)", "Short overview that points an assistant to everything else.", "/llms.txt", "TXT"),
            new("file", "AGENTS.md", "Instructions for coding assistants that read AGENTS.md (Codex, Copilot, Cursor, Gemini CLI).", "/api/agents-md", "MD"),
            new("spark", "Claude skill (SKILL.md)", "The same rules as a skill for Claude Code.", "/api/skill", "MD"),
        }),
        new("api", "API and tools", "For developers and scripts.", new DocLink[]
        {
            new("key", "API page", "Tokens, prompt templates for publishing and the most important endpoints.", "/Developer"),
            new("inbox", "Problem report queue", "Reports from the store window with attachments; an AI assistant works through them with your token.", "/Issues"),
            new("chart", "Dashboard", "Downloads, versions, Power PDF versions, ratings and reports of your add-ons.", "/Insights"),
            new("list", "All API endpoints", "The short list of every endpoint with method and purpose.", "/api", "TXT"),
            new("code", "OpenAPI description", "The machine-readable API description for code generators and tools.", "/api/openapi.json", "JSON"),
            new("tool", "Offline packer (make-ppak.ps1)", "Builds the .ppak, the source ZIP and the upload package without network or token.", "/api/tools/make-ppak.ps1", "PS1"),
        }),
    };

    private readonly IWebHostEnvironment _env;
    public DocsModel(IWebHostEnvironment env) => _env = env;

    /// <summary>Size and date of a PDF in wwwroot/docs, for the card.</summary>
    public (long Kb, DateTime? Date) FileInfoOf(string url)
    {
        if (!url.StartsWith("/docs/", StringComparison.Ordinal)) return (0, null);
        var f = new FileInfo(Path.Combine(_env.WebRootPath, "docs", Path.GetFileName(url)));
        return f.Exists ? ((f.Length + 1023) / 1024, f.LastWriteTimeUtc) : (0, null);
    }

    public void OnGet() { }
}
