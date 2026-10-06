using System.IO.Compression;

namespace AddonStore.Web.Services;

/// <summary>
/// Upload package (S1.0.7): ONE ZIP that carries the .ppak and the source ZIP of
/// the same version, for a single manual upload on the website (or the API).
/// It is the fallback when an assistant cannot reach the store itself: it builds
/// this file, the user uploads it under Plug-ins, "Submit a package"; the server
/// submits the .ppak and stores the source at the new version.
///
/// Recognized by content: a ZIP without manifest.json at its root that holds
/// exactly one *.ppak and at most one other *.zip (the source). Folders inside
/// the bundle are allowed (some ZIP tools add one); macOS metadata is ignored.
/// </summary>
public static class UploadBundle
{
    public const long MaxPackageBytes = 200L * 1024 * 1024;
    public const long MaxSourceBytes = 100L * 1024 * 1024;

    public record Result(string PpakPath, string? SourcePath, string? SourceName) : IDisposable
    {
        public void Dispose()
        {
            try { File.Delete(PpakPath); } catch { }
            if (SourcePath is not null) try { File.Delete(SourcePath); } catch { }
        }
    }

    /// <summary>
    /// Null when the file is not an upload package (a plain .ppak or no ZIP at all:
    /// the normal checks handle it). Throws <see cref="InvalidDataException"/> with a
    /// user-facing English message when it looks like one but is malformed.
    /// </summary>
    public static Result? TryUnpack(string zipPath)
    {
        if (UploadLimits.DeclaredEntries(zipPath) > 20000) return null;   // not a bundle; the package checks refuse it (audit S1.3.1)
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(zipPath); }
        catch (InvalidDataException) { return null; }
        catch (IOException) { return null; }
        using (zip)
        {
            var entries = zip.Entries
                .Where(e => !e.FullName.EndsWith('/') && !e.FullName.StartsWith("__MACOSX/", StringComparison.Ordinal)
                            && !Path.GetFileName(e.FullName).StartsWith("._", StringComparison.Ordinal))
                .ToList();
            if (entries.Any(e => e.FullName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))) return null;
            var ppaks = entries.Where(e => e.FullName.EndsWith(".ppak", StringComparison.OrdinalIgnoreCase)).ToList();
            var sources = entries.Where(e => e.FullName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
            if (ppaks.Count == 0 && sources.Count == 0) return null;   // not a bundle; the package checks explain the rest

            if (ppaks.Count != 1)
                throw new InvalidDataException(ppaks.Count == 0
                    ? "The upload package holds no .ppak file."
                    : "The upload package holds more than one .ppak file; put exactly one in it.");
            if (sources.Count > 1)
                throw new InvalidDataException("The upload package holds more than one ZIP file; put only the source ZIP of this version next to the .ppak.");
            if (ppaks[0].Length > MaxPackageBytes)
                throw new InvalidDataException("The .ppak in the upload package is larger than 200 MB.");
            if (sources.Count == 1 && sources[0].Length > MaxSourceBytes)
                throw new InvalidDataException("The source ZIP in the upload package is larger than 100 MB.");

            var ppak = Extract(ppaks[0], MaxPackageBytes);
            string? src = null;
            try { if (sources.Count == 1) src = Extract(sources[0], MaxSourceBytes); }
            catch { try { File.Delete(ppak); } catch { } throw; }
            return new Result(ppak, src, sources.Count == 1 ? Path.GetFileName(sources[0].FullName) : null);
        }
    }

    /// <summary>Copies one entry to a temp file, never more than <paramref name="cap"/> bytes (declared sizes can lie).</summary>
    private static string Extract(ZipArchiveEntry entry, long cap)
    {
        var path = Path.Combine(Path.GetTempPath(), "bundle-" + Guid.NewGuid().ToString("N") + Path.GetExtension(entry.FullName));
        using var input = entry.Open();
        using var output = File.Create(path);
        var buffer = new byte[81920];
        long total = 0;
        int n;
        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += n;
            if (total > cap)
            {
                output.Dispose();
                try { File.Delete(path); } catch { }
                throw new InvalidDataException("A file in the upload package is larger than allowed.");
            }
            output.Write(buffer, 0, n);
        }
        return path;
    }
}
