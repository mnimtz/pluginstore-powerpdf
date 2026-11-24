using System.IO.Compression;
using System.Text;
using AddonStore.Web.Auth;
using AddonStore.Web.Data;
using AddonStore.Web.Services;
using AddonStore.Web.Validation;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace AddonStore.Web.Tests;

/// <summary>
/// S2.8.2: the automatic review aid of an upload package (.ppak + source ZIP) waits until the source of the same
/// submission is stored, and runs again when the source arrives after the aid (also a later PUT .../source).
/// Before, the AI worker could read the version in the 1-2 seconds between the two writes and reported
/// "Source code: not deposited for this version." (com.tungsten.accessibilityreview 0.1.2, Nov 24, 2026).
/// </summary>
[Collection("Settings cache")]
public class ReviewAidSourceTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly string _root;
    private ServiceProvider _sp = null!;

    public ReviewAidSourceTests()
    {
        SettingsService.ClearCache();
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _root = Path.Combine(Path.GetTempPath(), "aidsrc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Build();
    }

    public void Dispose()
    {
        _sp?.Dispose(); _conn.Dispose(); SettingsService.ClearCache();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class Env : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "AddonStore.Web";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Development";
    }

    private sealed class NoMail : IAppEmailSender
    {
        public Task<MailResult> SendAsync(string to, string subject, string html, string eventKey) => throw new InvalidOperationException("no mail in tests");
    }

    private void Build()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[] { new KeyValuePair<string, string?>("Storage:Root", Path.Combine(_root, "packages")) }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IWebHostEnvironment>(new Env { ContentRootPath = _root });
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(_conn));
        services.AddIdentityCore<AppUser>(IdentitySetup.Apply).AddRoles<Microsoft.AspNetCore.Identity.IdentityRole>().AddEntityFrameworkStores<AppDbContext>();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddHttpClient();
        services.AddHttpContextAccessor();
        services.AddMemoryCache();
        services.AddScoped<IAppEmailSender, NoMail>();
        services.AddScoped<SettingsService>();
        services.AddScoped<AuditService>();
        services.AddScoped<NotificationService>();
        services.AddScoped<CategoryService>();
        services.AddScoped<SubmissionService>();
        services.AddScoped<SourceService>();
        services.AddScoped<AiService>();
        services.AddScoped<AiAssist>();
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
    }

    private async Task<T> Scoped<T>(Func<IServiceProvider, Task<T>> f)
    {
        using var scope = _sp.CreateScope();
        return await f(scope.ServiceProvider);
    }

    private static byte[] Zip(params (string Name, string Text)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in files)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(Encoding.UTF8.GetBytes(text));
            }
        return ms.ToArray();
    }

    private static readonly (string, string)[] SourceTree =
    {
        ("src/Plugin.cpp", "#include \"Plugin.h\"\nint Answer() { return 42; }\n"),
        ("src/Plugin.h", "#pragma once\nint Answer();\n"),
        ("src/Panel.cpp", "#include \"Plugin.h\"\nvoid Show() { Answer(); }\n"),
    };

    /// <summary>An upload package as make-ppak.ps1 writes it: one ZIP with the .ppak and the source ZIP of the version.</summary>
    private string UploadPackage(string id, string version)
    {
        var ppak = Zip(("manifest.json", Manifest(id, version)));
        var path = Path.Combine(_root, $"{id}-{version}-upload.zip");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        using (var s = zip.CreateEntry($"{id}-{version}.ppak").Open()) s.Write(ppak);
        using (var s = zip.CreateEntry($"{id}-{version}-source.zip").Open()) s.Write(Zip(SourceTree));
        return path;
    }

    private static string Manifest(string id, string version) =>
        $"{{\"id\":\"{id}\",\"version\":\"{version}\",\"name\":{{\"en\":\"Test add-on\"}},\"changelog\":{{\"en\":\"First version.\"}}}}";

    /// <summary>
    /// What SubmissionService.SubmitAsync(..., sourceFollows: true) stores for a checked package: the .ppak in the
    /// storage root and a submitted version, held back from the review aid before the row is saved.
    /// </summary>
    private Task<PackageVersion> StoreVersionAsync(string id, string version, string ppakPath, bool sourceFollows) => Scoped(async sp =>
    {
        var db = sp.GetRequiredService<AppDbContext>();
        var svc = sp.GetRequiredService<SubmissionService>();
        var user = await db.Users.FirstOrDefaultAsync();
        if (user is null)
        {
            user = new AppUser { UserName = "dev@example.com", Email = "dev@example.com", DisplayName = "Dev", Status = UserStatus.Active };
            db.Users.Add(user);
        }
        if (!await db.Packages.AnyAsync(p => p.Id == id)) db.Packages.Add(new Package { Id = id, OwnerId = user.Id });
        var rel = Path.Combine(id, $"{id}-{version}.ppak");
        Directory.CreateDirectory(Path.Combine(svc.StorageRoot, id));
        File.Copy(ppakPath, Path.Combine(svc.StorageRoot, rel), overwrite: true);
        var v = new PackageVersion
        {
            PackageId = id, Version = version, Status = VersionStatus.Submitted, Changelog = "First version.",
            ManifestJson = Manifest(id, version), FilePath = rel, Sha256 = "00", SizeBytes = new FileInfo(ppakPath).Length,
            SubmittedBy = "Dev", SubmittedById = user.Id, SubmittedVia = "api:test", ValidationReportJson = new ValidationReport().ToJson(),
        };
        if (sourceFollows) ReviewHold.Hold(id, version);
        db.PackageVersions.Add(v);
        await db.SaveChangesAsync();
        return v;
    });

    private Task<ValidationReport> StoreSourceAsync(int versionId, string zipPath) => Scoped(async sp =>
    {
        var db = sp.GetRequiredService<AppDbContext>();
        var v = await db.PackageVersions.FirstAsync(x => x.Id == versionId);
        var user = await db.Users.FirstAsync();
        return await sp.GetRequiredService<SourceService>().UploadAsync(v, zipPath, user, actorIsAdmin: false);
    });

    private Task<List<string>> Due() => Scoped(async sp =>
        (await AiWorker.DueForReviewAsync(sp.GetRequiredService<AppDbContext>(), DateTime.UtcNow)).Select(v => v.PackageId + " " + v.Version).ToList());

    private Task<string> Facts(int versionId) => Scoped(async sp =>
    {
        var v = await sp.GetRequiredService<AppDbContext>().PackageVersions.AsNoTracking().FirstAsync(x => x.Id == versionId);
        return await sp.GetRequiredService<AiAssist>().ReviewInputAsync(v, reviewSource: true);
    });

    [Fact]
    public async Task Upload_package_aid_waits_for_the_source_and_names_its_files()
    {
        const string id = "com.example.aidsource", ver = "0.1.2";
        using var bundle = UploadBundle.TryUnpack(UploadPackage(id, ver));
        Assert.NotNull(bundle?.SourcePath);

        var v = await StoreVersionAsync(id, ver, bundle!.PpakPath, sourceFollows: true);
        try
        {
            // between the version row and its source: the worker leaves the version alone
            Assert.DoesNotContain($"{id} {ver}", await Due());

            Assert.True((await StoreSourceAsync(v.Id, bundle.SourcePath!)).Passed);
        }
        finally { ReviewHold.Release(id, ver); }   // what POST /api/packages does after the source step

        Assert.Contains($"{id} {ver}", await Due());
        var facts = await Facts(v.Id);
        Assert.DoesNotContain("not deposited", facts);
        Assert.Contains("Source files: 3; added 3, removed 0, changed 0", facts);
        Assert.Contains("Plugin.cpp", facts);
    }

    [Fact]
    public async Task Source_stored_after_the_aid_makes_the_aid_due_again()
    {
        const string id = "com.example.aidlater", ver = "1.0.0";
        var ppak = Path.Combine(_root, "later.ppak");
        File.WriteAllBytes(ppak, Zip(("manifest.json", Manifest(id, ver))));
        var v = await StoreVersionAsync(id, ver, ppak, sourceFollows: false);
        Assert.Contains("Source code: not deposited for this version.", await Facts(v.Id));

        // an aid was built before the source came (bare .ppak, source by PUT .../source later)
        await Scoped(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var row = await db.PackageVersions.FirstAsync(x => x.Id == v.Id);
            row.AiReviewJson = "{}"; row.AiReviewAt = DateTime.UtcNow.AddSeconds(-5);
            return await db.SaveChangesAsync();
        });
        Assert.DoesNotContain($"{id} {ver}", await Due());

        var src = Path.Combine(_root, "later-source.zip");
        File.WriteAllBytes(src, Zip(SourceTree));
        Assert.True((await StoreSourceAsync(v.Id, src)).Passed);
        Assert.Contains($"{id} {ver}", await Due());
        Assert.Contains("Source files: 3", await Facts(v.Id));

        // an aid built after the source (the worker's next round, or "build again") settles it
        await Scoped(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var row = await db.PackageVersions.FirstAsync(x => x.Id == v.Id);
            row.AiReviewAt = row.SourceUploadedAt!.Value.AddSeconds(1);
            return await db.SaveChangesAsync();
        });
        Assert.DoesNotContain($"{id} {ver}", await Due());
    }

    [Fact]
    public void A_hold_that_is_never_released_lapses()
    {
        ReviewHold.Hold("com.example.lapse", "1.0.0");
        try
        {
            Assert.True(ReviewHold.IsHeld("com.example.lapse", "1.0.0", DateTime.UtcNow));
            Assert.False(ReviewHold.IsHeld("com.example.lapse", "1.0.0", DateTime.UtcNow.AddMinutes(11)));
            Assert.False(ReviewHold.IsHeld("com.example.lapse", "1.0.1", DateTime.UtcNow));
        }
        finally { ReviewHold.Release("com.example.lapse", "1.0.0"); }
    }
}
