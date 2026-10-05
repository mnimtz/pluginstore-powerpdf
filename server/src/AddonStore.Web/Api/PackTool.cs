namespace AddonStore.Web.Api;

/// <summary>
/// make-ppak.ps1 (S1.0.7): the official offline packer, served at
/// /api/tools/make-ppak.ps1. Windows PowerShell 5.1, ASCII only, no network,
/// no token: it fills architectures/files/sha256 into manifest.json, zips the
/// package tree with forward-slash entry names (Compress-Archive in 5.1 writes
/// backslashes) and optionally writes the source ZIP without build output.
/// The upload itself happens on the website (Plug-ins, "Submit a package") or
/// through the API; the server's checks stay authoritative.
/// </summary>
public static class PackTool
{
    public const string FileName = "make-ppak.ps1";

    public const string Script = """"
<#
.SYNOPSIS
  Packs a Tungsten Power PDF plugin into a .ppak for the Add-on Store.
  Offline: no network, no token. Windows PowerShell 5.1 or later.

.DESCRIPTION
  -Package <folder> is the package tree:
      manifest.json            all fields except architectures, files and sha256
                               (this script fills those in)
      x64\<Name>.zxt           required (Release build)
      arm64\<Name>.zxt         optional
      UILayout\Publish Mode.xml
      UILayout\NameAndTitle.xml and UILayout\<LANG>\NameAndTitle.xml for
                               ENU DEU FRA ITA ESP NLD PTB DAN FIN NOR SVE PLK CSY HUN RUS TRK
      assets\icon.png          recommended
      LICENSES.md              license texts of all third-party code
      docs\...                 optional

  -Source <folder> (optional) also writes <id>-<version>-source.zip of the
  source tree, without build output and without key or credential files, and
  the upload package <id>-<version>-upload.zip (.ppak + source ZIP).

  Then upload on the website: Plug-ins > "Submit a package". The upload
  package submits the .ppak and stores the source code in one step. The
  store's checks are authoritative; fix every error with its hint.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File make-ppak.ps1 -Package .\ppak -Source . -Out .\dist
#>
param(
    [Parameter(Mandatory = $true)][string]$Package,
    [string]$Source,
    [string]$Out = (Get-Location).Path
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$warnings = New-Object System.Collections.Generic.List[string]
function Warn([string]$text) { $warnings.Add($text); Write-Host "warning: $text" -ForegroundColor Yellow }
function Fail([string]$text) { Write-Host "error: $text" -ForegroundColor Red; exit 1 }

$Package = (Resolve-Path -LiteralPath $Package).Path.TrimEnd('\')
if (-not (Test-Path -LiteralPath $Out)) { New-Item -ItemType Directory -Path $Out | Out-Null }
$Out = (Resolve-Path -LiteralPath $Out).Path

# ---- manifest ---------------------------------------------------------------
$manifestPath = Join-Path $Package 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) { Fail "manifest.json is missing in $Package" }
$m = [IO.File]::ReadAllText($manifestPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
foreach ($f in 'id', 'version', 'name', 'description', 'changelog', 'category', 'ribbonAtomNamespace', 'thirdParty', 'complianceAudit') {
    if ($null -eq $m.$f) { if ($f -in 'id', 'version') { Fail "manifest field '$f' is missing" } else { Warn "manifest field '$f' is missing" } }
}
if ($m.version -notmatch '^\d+\.\d+\.\d+([-+][0-9A-Za-z.-]+)?$') { Fail "version '$($m.version)' is not SemVer (e.g. 1.2.3)" }
if ($m.id -notmatch '^[a-z][a-z0-9]*(\.[a-z0-9]+)+$') { Fail "id '$($m.id)' is not reverse-DNS lowercase (e.g. com.example.myplugin)" }
foreach ($f in 'author', 'contactEmail', 'minPowerPdfVersion') { if (-not $m.$f) { Warn "manifest field '$f' is not set" } }
$langs = 'en', 'de', 'fr', 'it', 'es', 'nl', 'pt', 'da', 'fi', 'nb', 'sv', 'pl', 'cs', 'hu', 'ru', 'tr'
foreach ($f in 'description', 'changelog') {
    if ($m.$f -is [psobject] -and -not ($m.$f -is [string])) {
        $missing = @($langs | Where-Object { -not $m.$f.$_ })
        if ($missing.Count -gt 0) { Warn "$f is missing languages: $($missing -join ' ')" }
    }
}

# ---- binaries, hashes -------------------------------------------------------
$archs = New-Object System.Collections.Generic.List[string]
$files = [ordered]@{}
$hashes = [ordered]@{}
foreach ($arch in 'x64', 'arm64') {
    $dir = Join-Path $Package $arch
    if (-not (Test-Path -LiteralPath $dir)) { continue }
    $zxt = @(Get-ChildItem -LiteralPath $dir -Filter '*.zxt' -File)
    if ($zxt.Count -ne 1) { Fail "$arch\ must hold exactly one .zxt (found $($zxt.Count))" }
    $archs.Add($arch)
    $files[$arch] = "$arch/$($zxt[0].Name)"
    $hashes[$arch] = (Get-FileHash -LiteralPath $zxt[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
if (-not $files.Contains('x64')) { Fail 'x64\<Name>.zxt is missing (x64 is required; it also covers Windows on ARM)' }
if ($files.Contains('arm64') -and ($files['arm64'] -split '/')[1] -ne ($files['x64'] -split '/')[1]) { Fail 'x64 and arm64 .zxt must have the same file name' }
$m | Add-Member -NotePropertyName architectures -NotePropertyValue ([string[]]$archs) -Force
$m | Add-Member -NotePropertyName files -NotePropertyValue ([pscustomobject]$files) -Force
$m | Add-Member -NotePropertyName sha256 -NotePropertyValue ([pscustomobject]$hashes) -Force

# ---- layout and assets (the store checks the details) ------------------------
$layout = Join-Path $Package 'UILayout'
if (-not (Test-Path -LiteralPath (Join-Path $layout 'Publish Mode.xml'))) { Warn 'UILayout\Publish Mode.xml is missing' }
if (-not (Test-Path -LiteralPath (Join-Path $layout 'NameAndTitle.xml'))) { Fail 'UILayout\NameAndTitle.xml is missing (required next to the 16 language folders; LANGS_INCOMPLETE)' }
$noLang = @('ENU', 'DEU', 'FRA', 'ITA', 'ESP', 'NLD', 'PTB', 'DAN', 'FIN', 'NOR', 'SVE', 'PLK', 'CSY', 'HUN', 'RUS', 'TRK' |
    Where-Object { -not (Test-Path -LiteralPath (Join-Path $layout "$_\NameAndTitle.xml")) })
if ($noLang.Count -gt 0) { Fail "UILayout language folders without NameAndTitle.xml: $($noLang -join ' ') (all 16 are required; LANGS_INCOMPLETE)" }
if (-not (Test-Path -LiteralPath (Join-Path $Package 'assets\icon.png'))) { Warn 'assets\icon.png is missing (recommended)' }
if (-not (Test-Path -LiteralPath (Join-Path $Package 'LICENSES.md'))) { Warn 'LICENSES.md is missing' }

# ---- zip helper: forward slashes, no junk -----------------------------------
$junk = @('Thumbs.db', 'desktop.ini', '.DS_Store')
function Add-Tree($zip, [string]$root, [scriptblock]$keep) {
    $n = 0
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File -Force) {
        $rel = $file.FullName.Substring($root.Length + 1)
        if ($junk -contains $file.Name) { continue }
        if (-not (& $keep $rel $file)) { continue }
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, ($rel -replace '\\', '/'), [IO.Compression.CompressionLevel]::Optimal)
        $n++
    }
    return $n
}
function New-Zip([string]$path) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    return [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
}

# ---- .ppak ------------------------------------------------------------------
$ppak = Join-Path $Out "$($m.id)-$($m.version).ppak"
$zip = New-Zip $ppak
try {
    $entry = $zip.CreateEntry('manifest.json', [IO.Compression.CompressionLevel]::Optimal)
    $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes(($m | ConvertTo-Json -Depth 20))
    $s = $entry.Open(); $s.Write($bytes, 0, $bytes.Length); $s.Dispose()
    $count = Add-Tree $zip $Package { param($rel, $file) $rel -ne 'manifest.json' -and $file.Extension -notin '.pdb', '.ilk', '.exp', '.lib', '.obj' }
} finally { $zip.Dispose() }
Write-Host ("ppak:   {0}  ({1} files + manifest, {2:N0} bytes)" -f $ppak, $count, (Get-Item -LiteralPath $ppak).Length)

# ---- source ZIP (optional) --------------------------------------------------
if ($Source) {
    $Source = (Resolve-Path -LiteralPath $Source).Path.TrimEnd('\')
    $skipDirs = @('.vs', '.git', '.svn', 'x64', 'arm64', 'ARM64', 'ARM64EC', 'Win32', 'Debug', 'Release', 'bin', 'obj', 'ipch', 'packages', 'node_modules', 'dist')
    $skipExt = @('.pdb', '.obj', '.ilk', '.iobj', '.ipdb', '.pch', '.idb', '.tlog', '.zxt', '.dll', '.exe', '.lib', '.exp', '.ppak',
                 '.user', '.aps', '.sdf', '.opensdf', '.db', '.log', '.res', '.pfx', '.p12', '.key', '.pem', '.snk')
    $srcZip = Join-Path $Out "$($m.id)-$($m.version)-source.zip"
    $outFull = $Out.TrimEnd('\') + '\'
    $zip = New-Zip $srcZip
    try {
        $count = Add-Tree $zip $Source {
            param($rel, $file)
            if ($file.FullName.StartsWith($outFull, [StringComparison]::OrdinalIgnoreCase) -and $Out -ne $Source) { return $false }
            $parts = $rel -split '\\'
            if ($parts.Count -gt 1) { foreach ($p in $parts[0..($parts.Count - 2)]) { if ($skipDirs -contains $p) { return $false } } }
            if ($skipExt -contains $file.Extension.ToLowerInvariant()) { return $false }
            if ($file.Name -like '.env*' -or $file.Name -like '*credential*' -or $file.Name -like '*secret*') { return $false }
            return $true
        }
    } finally { $zip.Dispose() }
    Write-Host ("source: {0}  ({1} files, {2:N0} bytes)" -f $srcZip, $count, (Get-Item -LiteralPath $srcZip).Length)

    # upload package: .ppak + source ZIP in one file, for one manual upload
    $upload = Join-Path $Out "$($m.id)-$($m.version)-upload.zip"
    $zip = New-Zip $upload
    try {
        foreach ($f in $ppak, $srcZip) {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f, (Split-Path -Leaf $f), [IO.Compression.CompressionLevel]::NoCompression)
        }
    } finally { $zip.Dispose() }
    Write-Host ("upload: {0}  ({1:N0} bytes)" -f $upload, (Get-Item -LiteralPath $upload).Length) -ForegroundColor Green
}

Write-Host ''
if ($Source) {
    Write-Host "Next: sign in on the Add-on Store website, Plug-ins > 'Submit a package', upload"
    Write-Host "  $upload"
    Write-Host "The store submits the .ppak and stores the source code at version $($m.version) in one step."
} else {
    Write-Host "Next: sign in on the Add-on Store website, Plug-ins > 'Submit a package', upload the .ppak."
    Write-Host "Then upload the source ZIP on the plug-in page (or run again with -Source for one upload package)."
}
if ($warnings.Count -gt 0) { Write-Host "$($warnings.Count) warning(s) above; the store reports every finding with a hint." }
"""";
}
