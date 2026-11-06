<#
  AddonSetup.ps1 - manual installation of one add-on from the Add-on Store for
  Tungsten Power PDF, without the store client (S1.30.0).

  The same script ships with every add-on; everything specific comes from
  package.json next to it. It does what the store client does:
    - checks the package against the store's signature (ECDSA P-256) and its SHA-256,
    - copies <Name>.zxt and the folder <Name>\ into Power PDF's Plug-Ins folder
      (one administrator step, from an admin-only staging folder),
    - adds the add-on's group to the shared "Enhanced Features" tab of the user's
      ribbon layout (user step; Power PDF merges it only into a profile that does
      not know the shared tab yet), for other users at their next sign-in,
    - registers an entry under "Apps and features" for the removal.

  install.bat / uninstall.bat start it; /quiet (or -Quiet) runs without questions,
  for software distribution (Intune, SCCM, ...).

  Exit codes: 0 done, 1 unexpected error, 2 package damaged, 3 signature not
  trusted, 4 administrator rights refused, 5 Power PDF not found, 6 the name
  belongs to another add-on, 7 a newer version is installed, 8 package invalid,
  9 copying failed, 11 not installed.
#>
[CmdletBinding()]
param(
    [ValidateSet('Install', 'Uninstall', 'MachineInstall', 'MachineUninstall', 'UserLayout')]
    [string]$Action = 'Install',
    [switch]$Quiet,
    [string]$Source = ''
)

$ErrorActionPreference = 'Stop'
# modules only from the PowerShell folder (admin-only): the elevated step must not load one from the user's Documents
$env:PSModulePath = Join-Path $PSHOME 'Modules'
if (-not $Source) { $Source = $PSScriptRoot }
$Source = $Source.TrimEnd('\')
# the text this process runs: what is kept for the removal, never the file again (it lies in a folder the user can write)
$SelfText = $MyInvocation.MyCommand.ScriptContents

$De = (Get-UICulture).TwoLetterISOLanguageName -eq 'de'
function T([string]$de, [string]$en) { if ($De) { $de } else { $en } }
# byte[] + byte[] gives object[] in PowerShell; this keeps it a byte array
function Join-Bytes([byte[]]$a, [byte[]]$b) { $r = New-Object byte[] ($a.Length + $b.Length); [Array]::Copy($a, $r, $a.Length); [Array]::Copy($b, 0, $r, $a.Length, $b.Length); return , $r }
# the last value a function wrote is its result; anything else it emitted must not become the exit code
function Get-Code($out) { $last = @($out)[-1]; if ($last -is [int]) { return $last } else { return 1 } }
function Say([string]$text, [string]$color = 'Gray') { if (-not $Quiet -or $color -eq 'Red') { Write-Host $text -ForegroundColor $color } }

# --- the store's signing keys (public keys only, X||Y base64; the same as the store client) ---------------------
$BuiltInKeys = @{
    'f729dd9553a86cd1' = 'H2GP5IfEAXH1x8RafnXP2bb3ZgrvNA8NpsMHVKytweKjTLTgBgUpf72MbzQgOLC//5gG7Wl+937JR+f8iqkP5g=='   # offline recovery key
    '6d2e832af60f35aa' = 'Si6zOG8/Us2ZtBgEOWpTkkvdcbrL33xowWFepHvJAFJiKbuDB+uwhVzA1fP/gGfaoTAp83VvO9RyrXWZSH+8VQ=='   # https://addon.power-pdf.de
}
$PolicyKey = 'SOFTWARE\Kofax\PDF\Tungsten Power PDF\PluginStore\Policies\Store'

function Get-MachineValue([string]$path, [string]$name) {
    foreach ($view in 'Registry64', 'Registry32') {
        try {
            $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', $view)
            $k = $base.OpenSubKey($path)
            if ($k) { $v = $k.GetValue($name); $k.Close(); if ($null -ne $v) { return $v } }
        } catch { }
    }
    return $null
}

function Test-Signature($pkg) {
    $allow = Get-MachineValue $PolicyKey 'AllowUnsigned'
    if ($allow -is [int] -and $allow -ne 0) { return $true }   # the same policy as the store client (test servers)
    $parts = ([string]$pkg.signature).Split(':')
    if ($parts.Count -ne 2) { return $false }
    $raw = $BuiltInKeys[$parts[0]]
    if (-not $raw) {
        # a company's own store instance: HKLM policy TrustedSigningKeys ("keyId:base64(X||Y)", ';' or one per line)
        $list = Get-MachineValue $PolicyKey 'TrustedSigningKeys'
        foreach ($item in (@($list) -join ';').Split(@(';', "`r", "`n"), 'RemoveEmptyEntries')) {
            $kv = $item.Trim().Split(':')
            if ($kv.Count -eq 2 -and $kv[0] -eq $parts[0]) { $raw = $kv[1] }
        }
    }
    if (-not $raw) { return $false }
    try {
        $xy = [Convert]::FromBase64String($raw)
        $sig = [Convert]::FromBase64String($parts[1])
        if ($xy.Length -ne 64 -or $sig.Length -ne 64) { return $false }
        # BCRYPT_ECCPUBLIC_BLOB: magic "ECS1", key length 32, then X||Y
        $blob = Join-Bytes ([byte[]](0x45, 0x43, 0x53, 0x31, 0x20, 0x00, 0x00, 0x00)) $xy
        $key = [System.Security.Cryptography.CngKey]::Import($blob, [System.Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
        $ec = New-Object System.Security.Cryptography.ECDsaCng($key)
        $ec.HashAlgorithm = [System.Security.Cryptography.CngAlgorithm]::Sha256
        $msg = [Text.Encoding]::UTF8.GetBytes("addonstore-pkg-v2`n$($pkg.id)`n$($pkg.version)`n$($pkg.sha256)`n$($pkg.zxtName)")
        return $ec.VerifyData($msg, $sig)
    } catch { return $false }
}

# --- package.json -------------------------------------------------------------------------------------------------
function Read-Package([string]$dir) {
    $file = Join-Path $dir 'package.json'
    if (-not (Test-Path -LiteralPath $file)) { return $null }
    try { $p = [IO.File]::ReadAllText($file, [Text.Encoding]::UTF8) | ConvertFrom-Json } catch { return $null }
    if ([string]$p.format -ne 'addonstore-manual-v1') { return $null }
    if ([string]$p.id -notmatch '^[a-z0-9][a-z0-9._-]{2,100}$') { return $null }
    if ([string]$p.version -notmatch '^[0-9]+(\.[0-9]+){1,3}([-+][0-9A-Za-z.-]{1,40})?$') { return $null }
    if ([string]$p.zxtName -notmatch '^[A-Za-z0-9_-]{1,64}$' -or [string]$p.zxtName -eq 'PluginStore') { return $null }
    if ([string]$p.sha256 -notmatch '^[0-9a-f]{64}$') { return $null }
    # name, author and page are not covered by the signature: they only become display texts, cleaned and capped
    $name = Get-CleanText $p.name 120; if (-not $name) { $name = [string]$p.id }
    $page = [string]$p.page
    if ($page.Length -gt 300 -or $page -notmatch '^https://[A-Za-z0-9.-]+(:[0-9]{1,5})?(/[A-Za-z0-9._~%/?#=&+-]*)?$') { $page = '' }
    return [pscustomobject]@{ format = [string]$p.format; id = [string]$p.id; version = [string]$p.version; name = $name
                              author = (Get-CleanText $p.author 120); zxtName = [string]$p.zxtName; sha256 = [string]$p.sha256
                              signature = [string]$p.signature; page = $page }
}

function Get-CleanText($value, [int]$max) {
    $s = ([string]$value) -replace '[\p{Cc}\p{Cf}]', ''
    $s = $s.Trim()
    if ($s.Length -gt $max) { $s = $s.Substring(0, $max) }
    return $s
}

# --- Power PDF ------------------------------------------------------------------------------------------------------
function Find-PowerPdf {
    $candidates = New-Object System.Collections.Generic.List[string]
    $candidates.Add([string](Get-MachineValue 'SOFTWARE\Kofax\PDF\V1' 'InstallPath'))   # the current installation
    $app = 'SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\PowerPDF.exe'
    $bin = [string](Get-MachineValue $app 'Path')
    if (-not $bin) { $exe = [string](Get-MachineValue $app ''); if ($exe) { $bin = Split-Path $exe.Trim('"') -Parent } }
    if ($bin) { $candidates.Add((Split-Path $bin.TrimEnd('\') -Parent)) }
    $candidates.Add([string](Get-MachineValue 'SOFTWARE\Kofax\PDF' 'InstallDir'))
    $candidates.Add([string](Get-MachineValue 'SOFTWARE\Kofax\PDF' 'InstallPath'))
    foreach ($view in 'Registry64', 'Registry32') {
        try {
            $u = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', $view).OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall')
            if ($u) {
                foreach ($n in $u.GetSubKeyNames()) {
                    $k = $u.OpenSubKey($n)
                    if ($k -and [string]$k.GetValue('DisplayName') -like '*Power PDF*') { $candidates.Add([string]$k.GetValue('InstallLocation')) }
                }
            }
        } catch { }
    }
    foreach ($c in $candidates) {
        if (-not $c) { continue }
        $r = $c.Trim('"').TrimEnd('\')
        if (Test-Path -LiteralPath (Join-Path $r 'bin\PowerPDF.exe')) { return $r }
    }
    return ''
}

function Compare-Version([string]$a, [string]$b) {
    $x = @((($a -split '[-+]')[0]) -split '\.'); $y = @((($b -split '[-+]')[0]) -split '\.')
    for ($i = 0; $i -lt 4; $i++) {
        $va = 0; $vb = 0
        if ($i -lt $x.Count) { [void][long]::TryParse($x[$i], [ref]$va) }
        if ($i -lt $y.Count) { [void][long]::TryParse($y[$i], [ref]$vb) }
        if ($va -ne $vb) { if ($va -lt $vb) { return -1 } else { return 1 } }
    }
    return 0
}

function Get-InstalledManifest([string]$plugins, [string]$name) {
    $f = Join-Path $plugins "$name\manifest.json"
    if (-not (Test-Path -LiteralPath $f)) { return $null }
    try { return [IO.File]::ReadAllText($f, [Text.Encoding]::UTF8) | ConvertFrom-Json } catch { return $null }
}

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
function Test-System { return [Security.Principal.WindowsIdentity]::GetCurrent().IsSystem }

function Test-PowerPdfOpen {
    # only in this Windows session: on a terminal server other users' Power PDF must not hold the installation
    $me = (Get-Process -Id $PID).SessionId
    return [bool](@(Get-Process -Name PowerPDF -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $me }).Count)
}

function Wait-PowerPdfClosed {
    if ($Quiet) { return }
    while (Test-PowerPdfOpen) {
        Say (T 'Power PDF ist geöffnet. Bitte schließen Sie Power PDF und drücken Sie dann die Eingabetaste (abbrechen mit Strg+C).' `
               'Power PDF is open. Please close Power PDF, then press Enter (Ctrl+C cancels).') 'Yellow'
        [void](Read-Host)
    }
}

# --- the user's ribbon layout (port of the store client's fplayout::EnsureAddonGroups) ------------------------------
$Ord = [StringComparison]::Ordinal

function Read-Layout([string]$path) {
    $raw = [IO.File]::ReadAllBytes($path)
    if ($raw.Length -gt 8MB -or $raw.Length -lt 2) { return $null }
    if ($raw[0] -eq 0xFF -and $raw[1] -eq 0xFE) { return @{ Text = [Text.Encoding]::Unicode.GetString($raw, 2, $raw.Length - 2); Utf16 = $true; Bom = $true } }
    $skip = 0; if ($raw.Length -ge 3 -and $raw[0] -eq 0xEF -and $raw[1] -eq 0xBB -and $raw[2] -eq 0xBF) { $skip = 3 }
    return @{ Text = (New-Object Text.UTF8Encoding($false)).GetString($raw, $skip, $raw.Length - $skip); Utf16 = $false; Bom = ($skip -eq 3) }
}

function Write-Layout([string]$path, $layout, [string]$text) {
    if ($layout.Utf16) { $bytes = Join-Bytes ([byte[]](0xFF, 0xFE)) ([Text.Encoding]::Unicode.GetBytes($text)) }
    else { $bytes = Join-Bytes ((New-Object Text.UTF8Encoding($layout.Bom)).GetPreamble()) ((New-Object Text.UTF8Encoding($false)).GetBytes($text)) }
    $bak = "$path.fp2-original"   # one pristine copy, the same name the store client uses
    if (-not (Test-Path -LiteralPath $bak)) { Copy-Item -LiteralPath $path -Destination $bak }
    $tmp = "$path.addonstore-tmp"
    [IO.File]::WriteAllBytes($tmp, $bytes)
    # swapped in whole: a crash never leaves a cut-off layout ([NullString]: a plain $null arrives as "" and fails)
    [IO.File]::Replace($tmp, $path, [NullString]::Value)
}

function Get-Attr([string]$tag, [string]$attr) {
    $m = [regex]::Match($tag, ' ' + $attr + '="([^"]*)"')
    if ($m.Success) { return $m.Groups[1].Value } else { return '' }
}
function Test-Atom([string]$s) { return $s.Length -ge 14 -and $s.Length -le 200 -and $s -cmatch '^FeaturePack::[A-Za-z0-9:_.-]+$' }

# The groups the add-on's own layout puts on the shared tab, rebuilt from checked atom names only.
function Get-AddonGroups([string]$text) {
    $out = @()
    $tb = $text.IndexOf('<toolbar name="FeaturePack"', $Ord); if ($tb -lt 0) { return $out }
    $tbEnd = $text.IndexOf('</toolbar>', $tb, $Ord); if ($tbEnd -lt 0) { return $out }
    $g = $text.IndexOf('<PFFGroup', $tb, $Ord)
    while ($g -ge 0 -and $g -lt $tbEnd) {
        $gt = $text.IndexOf('>', $g, $Ord)
        if ($gt -lt 0 -or $gt -gt $tbEnd) { break }
        if ($text[$gt - 1] -ne '/') {
            $gEnd = $text.IndexOf('</PFFGroup>', $gt, $Ord)
            if ($gEnd -lt 0 -or $gEnd -gt $tbEnd) { break }
            $name = Get-Attr $text.Substring($g, $gt - $g) 'name'
            if (Test-Atom $name) {
                $buttons = @()
                $b = $text.IndexOf('<PFFButton', $gt, $Ord)
                while ($b -ge 0 -and $b -lt $gEnd -and $buttons.Count -lt 40) {
                    $bt = $text.IndexOf('>', $b, $Ord); if ($bt -lt 0 -or $bt -gt $gEnd) { break }
                    $tag = $text.Substring($b, $bt - $b)
                    $n = Get-Attr $tag 'name'; $m = Get-Attr $tag 'IconMode'
                    if ($m -notmatch '^[1-9]$') { $m = '4' }
                    if (Test-Atom $n) { $buttons += , @($n, $m) }
                    $b = $text.IndexOf('<PFFButton', $b + 1, $Ord)
                }
                if ($buttons.Count -gt 0 -and $out.Count -lt 200) { $out += , @{ Name = $name; Buttons = $buttons } }
            }
        }
        $g = $text.IndexOf('<PFFGroup', $g + 1, $Ord)
    }
    return $out
}

function Get-LayoutName([string]$text) {
    $p = $text.IndexOf('<GaaihoLayout', $Ord); if ($p -lt 0) { return '' }
    $gt = $text.IndexOf('>', $p, $Ord); if ($gt -lt 0) { return '' }
    return Get-Attr $text.Substring($p, $gt - $p) 'name'
}

# Adds the groups (and buttons missing from a group) to the shared tab of the viewer layout; $null = unchanged.
function Add-Groups([string]$text, $groups, [string]$nl) {
    if ($groups.Count -eq 0 -or (Get-LayoutName $text) -ne 'Publish') { return $null }
    $changed = $false
    if ($text.IndexOf('<toolbar name="FeaturePack"', $Ord) -lt 0) {
        $at = $text.IndexOf('<toolbar name="AddonStore"', $Ord)
        if ($at -lt 0) { $top = $text.IndexOf('<Top', $Ord); if ($top -ge 0) { $at = $text.IndexOf('</Top>', $top, $Ord) } }
        if ($at -lt 0) { return $null }
        $text = $text.Insert($at, '<toolbar name="FeaturePack" shortKey="U">' + $nl + '</toolbar>' + $nl)
        $changed = $true
    }
    $tb = $text.IndexOf('<toolbar name="FeaturePack"', $Ord)
    $gt = $text.IndexOf('>', $tb, $Ord); if ($gt -lt 0) { return $null }
    if ($text[$gt - 1] -eq '/') {
        $slash = $gt - 1; while ($slash -gt $tb -and $text[$slash - 1] -eq ' ') { $slash-- }
        $text = $text.Remove($slash, $gt + 1 - $slash).Insert($slash, '>' + $nl + '</toolbar>')
        $changed = $true
    }
    foreach ($g in $groups) {
        $tbEnd = $text.IndexOf('</toolbar>', $tb, $Ord); if ($tbEnd -lt 0) { break }
        $grpTag = '<PFFGroup name="' + $g.Name + '"'
        $grp = $text.IndexOf($grpTag, $Ord)
        if ($grp -lt 0) {
            $block = $grpTag + ' GroupType="PFFTitleBlock">' + $nl
            foreach ($b in $g.Buttons) { $block += '<PFFButton name="' + $b[0] + '" IconMode="' + $b[1] + '"/>' + $nl }
            $text = $text.Insert($tbEnd, $block + '</PFFGroup>' + $nl)
            $changed = $true
            continue
        }
        $open = $text.IndexOf('>', $grp, $Ord)
        if ($open -lt 0 -or $text[$open - 1] -eq '/') { continue }   # a self-closing group is left alone
        $grpEnd = $text.IndexOf('</PFFGroup>', $open, $Ord); if ($grpEnd -lt 0) { continue }
        foreach ($b in $g.Buttons) {
            $needle = '<PFFButton name="' + $b[0] + '"'
            $f = $text.IndexOf($needle, $open, $Ord)
            if ($f -ge 0 -and $f -lt $grpEnd) { continue }
            $ins = $needle + ' IconMode="' + $b[1] + '"/>' + $nl
            $text = $text.Insert($grpEnd, $ins); $grpEnd += $ins.Length
            $changed = $true
        }
    }
    if ($changed) { return $text } else { return $null }
}

# Removes whole groups by exact name (never by pattern: Power PDF has its own atoms); $null = unchanged.
function Remove-Groups([string]$text, $groups) {
    $changed = $false
    foreach ($g in $groups) {
        $tag = '<PFFGroup name="' + $g.Name + '"'
        while (($p = $text.IndexOf($tag, $Ord)) -ge 0) {
            $open = $text.IndexOf('>', $p, $Ord); if ($open -lt 0) { break }
            if ($text[$open - 1] -eq '/') { $end = $open + 1 }
            else { $end = $text.IndexOf('</PFFGroup>', $p, $Ord); if ($end -lt 0) { break }; $end += 11 }
            $start = $p; while ($start -gt 0 -and " `t`r`n".IndexOf($text[$start - 1]) -ge 0) { $start-- }
            $text = $text.Remove($start, $end - $start)
            $changed = $true
        }
    }
    if ($changed) { return $text } else { return $null }
}

# Every layout file of the user that carries a ribbon (<Top>), one folder level deep.
function Get-UserLayoutFiles {
    $dir = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Kofax\PDF\PowerPDF\UILayout'
    if (-not (Test-Path -LiteralPath $dir)) { return @() }
    $files = @(Get-ChildItem -LiteralPath $dir -Filter '*.xml' -File -ErrorAction SilentlyContinue)
    foreach ($sub in @(Get-ChildItem -LiteralPath $dir -Directory -ErrorAction SilentlyContinue)) {
        $files += @(Get-ChildItem -LiteralPath $sub.FullName -Filter '*.xml' -File -ErrorAction SilentlyContinue)
    }
    return $files
}

function Get-OwnGroups([string]$plugins, [string]$name) {
    $own = Join-Path $plugins "$name\UILayout\Publish Mode.xml"
    if (-not (Test-Path -LiteralPath $own)) { return @() }
    $l = Read-Layout $own; if (-not $l) { return @() }
    return @(Get-AddonGroups $l.Text)
}

function Update-UserLayout([string]$plugins, [string]$name, [bool]$remove, $groups = $null) {
    if ($null -eq $groups) { $groups = @(Get-OwnGroups $plugins $name) }
    if ($groups.Count -eq 0) { return 0 }
    $written = 0
    foreach ($f in Get-UserLayoutFiles) {
        try {
            $lay = Read-Layout $f.FullName
            if (-not $lay -or $lay.Text.IndexOf('<Top', $Ord) -lt 0) { continue }
            $nl = "`n"; if ($lay.Text.IndexOf("`r`n", $Ord) -ge 0) { $nl = "`r`n" }
            if ($remove) { $new = Remove-Groups $lay.Text $groups } else { $new = Add-Groups $lay.Text $groups $nl }
            if ($null -ne $new) { Write-Layout $f.FullName $lay $new; $written++ }
        } catch { }
    }
    return $written
}

# --- administrator part ----------------------------------------------------------------------------------------------
function Clear-Bin([string]$dir, [string]$plugins) {
    if (-not (Test-Path -LiteralPath $dir)) { return }
    foreach ($f in @(Get-ChildItem -LiteralPath $dir -Force -Recurse -File)) {
        try { Remove-Item -LiteralPath $f.FullName -Force }
        catch { Move-Item -LiteralPath $f.FullName -Destination (Join-Path $plugins ('.psold-' + [guid]::NewGuid().ToString('N') + '-' + $f.Name)) -Force }
    }
    Remove-Item -LiteralPath $dir -Recurse -Force
}

# SYSTEM and Administrators only, nothing inherited (the same as the store client); $readers may read and run.
function New-AdminFolder([string]$path, [string[]]$readers) {
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule((New-Object System.Security.Principal.SecurityIdentifier $sid), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    foreach ($sid in @('S-1-3-4') + $readers) {   # OWNER RIGHTS: whoever owns it gets no implicit WRITE_DAC
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule((New-Object System.Security.Principal.SecurityIdentifier $sid), 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    [void][System.IO.Directory]::CreateDirectory($path, $acl)
    return $path
}

function New-Stage([string]$plugins) { return New-AdminFolder (Join-Path $plugins ('.psstage-' + [guid]::NewGuid().ToString('N'))) @() }

# package.json for the removal, from the CHECKED values (the file next to the zip can have changed since)
function Get-PackageJson($pkg) {
    return ([pscustomobject]@{ format = $pkg.format; id = $pkg.id; version = $pkg.version; name = $pkg.name; author = $pkg.author
                               zxtName = $pkg.zxtName; sha256 = $pkg.sha256; signature = $pkg.signature; page = $pkg.page } | ConvertTo-Json)
}

function Get-EntryKey($pkg) { return 'AddonStore_' + $pkg.id }
function Test-Entry($pkg) {
    try { $k = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64').OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' + (Get-EntryKey $pkg)) } catch { return $false }
    if ($k) { $k.Close(); return $true } else { return $false }
}

function Invoke-MachineInstall($pkg) {
    $root = Find-PowerPdf; if (-not $root) { return 5 }
    $plugins = Join-Path $root 'bin\Plug-Ins'
    $name = $pkg.zxtName
    $have = Get-InstalledManifest $plugins $name
    if ($have -and $have.id -and $have.id -ne $pkg.id) { return 6 }
    if ($have -and $have.version -and (Compare-Version $pkg.version $have.version) -lt 0) { return 7 }
    Get-ChildItem -LiteralPath $plugins -Filter '.psold-*' -File -Force -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    Get-ChildItem -LiteralPath $plugins -Filter '*.zxt.old-*' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stage = New-Stage $plugins
    try {
        # checked again in the admin-only folder, so nothing can swap the file after the check
        $ppak = Join-Path $stage 'package.ppak'
        Copy-Item -LiteralPath (Join-Path $Source 'package.ppak') -Destination $ppak -Force
        if ((Get-FileHash -LiteralPath $ppak -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pkg.sha256) { return 2 }
        if (-not (Test-Signature $pkg)) { return 3 }
        $tmp = Join-Path $stage 'x'
        [System.IO.Compression.ZipFile]::ExtractToDirectory($ppak, $tmp)
        # x64 binary on every machine: Power PDF on Windows on ARM runs ARM64EC and loads x64 plug-ins
        $bin = Join-Path $tmp "x64\$name.zxt"
        if (-not (Test-Path -LiteralPath $bin) -or -not (Test-Path -LiteralPath (Join-Path $tmp 'manifest.json'))) { return 8 }
        try {
            $target = Join-Path $plugins "$name.zxt"
            if (Test-Path -LiteralPath $target) {
                try { Remove-Item -LiteralPath $target -Force } catch { Rename-Item -LiteralPath $target ("$name.zxt.old-" + [guid]::NewGuid().ToString('N')) }
            }
            Copy-Item -LiteralPath $bin -Destination $target -Force
            $data = Join-Path $plugins $name
            New-Item -ItemType Directory -Force -Path $data | Out-Null
            Copy-Item -LiteralPath (Join-Path $tmp 'manifest.json') -Destination (Join-Path $data 'manifest.json') -Force
            foreach ($extra in @('assets', 'docs', 'UILayout')) {
                $s = Join-Path $tmp $extra; $d = Join-Path $data $extra
                if (Test-Path -LiteralPath $s) { if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force }; Copy-Item -LiteralPath $s -Destination $d -Recurse -Force }
            }
            Clear-Bin (Join-Path $data 'bin') $plugins
            $dlls = @(Get-ChildItem -LiteralPath (Join-Path $tmp 'bin') -Filter '*.dll' -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -eq '.dll' })
            if ($dlls.Count -gt 0) {
                New-Item -ItemType Directory -Force -Path (Join-Path $data 'bin') | Out-Null
                foreach ($f in $dlls) { Copy-Item -LiteralPath $f.FullName -Destination (Join-Path $data "bin\$($f.Name)") -Force }
            }
            # this script and package.json stay for the removal and for other users, in a folder of its own that only
            # administrators can change (even if an installer widened the rights of Plug-Ins): Active Setup runs it for
            # every user, and an administrator runs it for the removal
            $setup = Join-Path $data 'setup'
            if (Test-Path -LiteralPath $setup) { Remove-Item -LiteralPath $setup -Recurse -Force }
            [void](New-AdminFolder $setup @('S-1-5-32-545'))
            $utf8Bom = New-Object Text.UTF8Encoding($true)
            [IO.File]::WriteAllText((Join-Path $setup 'AddonSetup.ps1'), $SelfText, $utf8Bom)
            [IO.File]::WriteAllText((Join-Path $setup 'package.json'), (Get-PackageJson $pkg), (New-Object Text.UTF8Encoding($false)))
        } catch { return 9 }
        Register-Entries $pkg $root $data
        return 0
    } finally { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
}

function Register-Entries($pkg, [string]$root, [string]$data) {
    $script = Join-Path $data 'setup\AddonSetup.ps1'
    $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $call = '"' + $ps + '" -NoProfile -ExecutionPolicy Bypass -File "' + $script + '" -Source "' + (Join-Path $data 'setup') + '"'
    $hklm = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
    try {
        # "Apps and features": removal without the zip
        $u = $hklm.CreateSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' + (Get-EntryKey $pkg))
        $u.SetValue('DisplayName', [string]$pkg.name + ' (Power PDF add-on)')
        $u.SetValue('DisplayVersion', [string]$pkg.version)
        $u.SetValue('Publisher', [string]$pkg.author)
        $u.SetValue('InstallLocation', $data)
        $u.SetValue('DisplayIcon', (Join-Path $root 'bin\PowerPDF.exe') + ',0')
        $u.SetValue('UninstallString', $call + ' -Action Uninstall')
        $u.SetValue('QuietUninstallString', $call + ' -Action Uninstall -Quiet')
        if ([string]$pkg.page) { $u.SetValue('URLInfoAbout', [string]$pkg.page) }
        $u.SetValue('NoModify', 1, 'DWord'); $u.SetValue('NoRepair', 1, 'DWord')
        $size = (Get-ChildItem -LiteralPath $data -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
        $u.SetValue('EstimatedSize', [int](($size + (Get-Item -LiteralPath (Join-Path (Split-Path $data -Parent) "$($pkg.zxtName).zxt")).Length) / 1KB), 'DWord')
        $u.Close()
        # Active Setup: the ribbon group for every other user at their next sign-in (a new version runs it again)
        $a = $hklm.CreateSubKey('SOFTWARE\Microsoft\Active Setup\Installed Components\' + (Get-EntryKey $pkg))
        $a.SetValue('', [string]$pkg.name)
        $a.SetValue('StubPath', ($call -replace '-ExecutionPolicy Bypass', '-ExecutionPolicy Bypass -WindowStyle Hidden') + ' -Action UserLayout -Quiet')
        $a.SetValue('Version', (Get-Date -Format 'yyyy,MMdd,HHmm,ss'))
        $a.SetValue('IsInstalled', 1, 'DWord')
        $a.Close()
    } catch { }
}

function Invoke-MachineUninstall($pkg) {
    $root = Find-PowerPdf; if (-not $root) { return 5 }
    $plugins = Join-Path $root 'bin\Plug-Ins'
    $name = $pkg.zxtName
    $have = Get-InstalledManifest $plugins $name
    $hklm = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
    if (-not $have -or $have.id -ne $pkg.id) {
        # nothing of ours there (any more): only stale entries go
        try { $hklm.DeleteSubKeyTree('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' + (Get-EntryKey $pkg), $false) } catch { }
        try { $hklm.DeleteSubKeyTree('SOFTWARE\Microsoft\Active Setup\Installed Components\' + (Get-EntryKey $pkg), $false) } catch { }
        if ($have) { return 6 } else { return 11 }
    }
    Set-Location -LiteralPath $env:SystemRoot   # never stand in the folder that goes
    try {
        $zxt = Join-Path $plugins "$name.zxt"
        if (Test-Path -LiteralPath $zxt) { try { Remove-Item -LiteralPath $zxt -Force } catch { Rename-Item -LiteralPath $zxt ("$name.zxt.old-" + [guid]::NewGuid().ToString('N')) } }
        $data = Join-Path $plugins $name
        Clear-Bin (Join-Path $data 'bin') $plugins
        if (Test-Path -LiteralPath $data) { Remove-Item -LiteralPath $data -Recurse -Force }
    } catch { return 9 }
    try { $hklm.DeleteSubKeyTree('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' + (Get-EntryKey $pkg), $false) } catch { }
    try { $hklm.DeleteSubKeyTree('SOFTWARE\Microsoft\Active Setup\Installed Components\' + (Get-EntryKey $pkg), $false) } catch { }
    return 0
}

# One administrator step: in this process when it already has the rights, else ONE UAC prompt.
function Invoke-AdminStep([string]$act, $pkg) {
    if (Test-Admin) {
        if ($act -eq 'MachineInstall') { return Get-Code (Invoke-MachineInstall $pkg) } else { return Get-Code (Invoke-MachineUninstall $pkg) }
    }
    $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $arg = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $PSCommandPath + '" -Action ' + $act + ' -Source "' + $Source + '" -Quiet'
    try { $p = Start-Process -FilePath $ps -ArgumentList $arg -Verb RunAs -WindowStyle Hidden -Wait -PassThru } catch { return 4 }
    return $p.ExitCode
}

function Get-Message([int]$rc) {
    switch ($rc) {
        2 { T 'Das Paket ist beschädigt (Prüfsumme stimmt nicht). Bitte laden Sie es neu herunter.' 'The package is damaged (checksum does not match). Please download it again.' }
        3 { T 'Das Paket trägt keine gültige Signatur des Add-on Store und wird nicht installiert.' 'The package does not carry a valid signature of the Add-on Store and is not installed.' }
        4 { T 'Ohne Administratorrechte kann nichts installiert oder entfernt werden.' 'Nothing can be installed or removed without administrator rights.' }
        5 { T 'Tungsten Power PDF wurde auf diesem Computer nicht gefunden.' 'Tungsten Power PDF was not found on this computer.' }
        6 { T 'Unter diesem Namen ist bereits ein anderes Add-on installiert. Es wurde nichts geändert.' 'Another add-on is installed under this name. Nothing was changed.' }
        7 { T 'Es ist bereits eine neuere Version installiert. Es wurde nichts geändert.' 'A newer version is already installed. Nothing was changed.' }
        8 { T 'Das Paket ist unvollständig oder ungültig.' 'The package is incomplete or invalid.' }
        9 { T 'Die Dateien konnten nicht kopiert oder entfernt werden.' 'The files could not be copied or removed.' }
        11 { T 'Das Add-on ist nicht installiert.' 'The add-on is not installed.' }
        default { T "Unerwarteter Fehler (Code $rc)." "Unexpected error (code $rc)." }
    }
}

function Finish([int]$rc) {
    if (-not $Quiet) { Write-Host ''; [void](Read-Host (T 'Eingabetaste drücken zum Schließen' 'Press Enter to close')) }
    exit $rc
}

# --- main ------------------------------------------------------------------------------------------------------------
try {
    $pkg = Read-Package $Source
    if (-not $pkg) {
        if ($Action -like 'Machine*' -or $Action -eq 'UserLayout') { exit 8 }
        Say (Get-Message 8) 'Red'; Finish 8
    }

    switch ($Action) {
        'MachineInstall' { exit (Get-Code (Invoke-MachineInstall $pkg)) }
        'MachineUninstall' { exit (Get-Code (Invoke-MachineUninstall $pkg)) }
        'UserLayout' {
            # Active Setup at sign-in: the group for this user, nothing else
            $root = Find-PowerPdf
            if ($root -and (Get-InstalledManifest (Join-Path $root 'bin\Plug-Ins') $pkg.zxtName)) { [void](Update-UserLayout (Join-Path $root 'bin\Plug-Ins') $pkg.zxtName $false) }
            exit 0
        }
        'Install' {
            Say ((T 'Add-on installieren: ' 'Installing add-on: ') + "$($pkg.name) $($pkg.version)") 'Cyan'
            if (-not (Test-Path -LiteralPath (Join-Path $Source 'package.ppak'))) { Say (Get-Message 8) 'Red'; Finish 8 }
            if ((Get-FileHash -LiteralPath (Join-Path $Source 'package.ppak') -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pkg.sha256) { Say (Get-Message 2) 'Red'; Finish 2 }
            if (-not (Test-Signature $pkg)) { Say (Get-Message 3) 'Red'; Finish 3 }
            Say (T 'Signatur und Prüfsumme in Ordnung.' 'Signature and checksum are fine.')
            $root = Find-PowerPdf; if (-not $root) { Say (Get-Message 5) 'Red'; Finish 5 }
            Wait-PowerPdfClosed
            $rc = Get-Code (Invoke-AdminStep 'MachineInstall' $pkg)
            if ($rc -ne 0) { Say (Get-Message $rc) 'Red'; Finish $rc }
            if (-not (Test-System)) { [void](Update-UserLayout (Join-Path $root 'bin\Plug-Ins') $pkg.zxtName $false) }
            Say (T 'Fertig. Das Add-on ist nach dem nächsten Start von Power PDF verfügbar.' 'Done. The add-on is available after the next start of Power PDF.') 'Green'
            Say (T 'Hinweis: Manuell installierte Add-ons erhalten keine automatischen Updates.' 'Note: add-ons installed manually receive no automatic updates.') 'Yellow'
            Finish 0
        }
        'Uninstall' {
            Say ((T 'Add-on entfernen: ' 'Removing add-on: ') + "$($pkg.name)") 'Cyan'
            $root = Find-PowerPdf; if (-not $root) { Say (Get-Message 5) 'Red'; Finish 5 }
            $plugins = Join-Path $root 'bin\Plug-Ins'
            $have = Get-InstalledManifest $plugins $pkg.zxtName
            $mine = $have -and $have.id -eq $pkg.id
            if (-not $have -and -not (Test-Entry $pkg)) { Say (Get-Message 11) 'Yellow'; Finish 11 }   # nothing to do: no UAC prompt
            if ($have -and -not $mine) { Say (Get-Message 6) 'Red'; Finish 6 }
            Wait-PowerPdfClosed
            # the group names come from the add-on's own layout, which goes with it: read them now, take them out
            # of the user's ribbon only once the files are really gone (a refused prompt changes nothing)
            $groups = @(); if ($mine) { $groups = @(Get-OwnGroups $plugins $pkg.zxtName) }
            $rc = Get-Code (Invoke-AdminStep 'MachineUninstall' $pkg)
            if ($rc -eq 0 -and -not (Test-System)) {
                [void](Update-UserLayout $plugins $pkg.zxtName $true $groups)
                # the add-on's settings key of this user (as the store client does) and the Active Setup mark
                Remove-Item -LiteralPath ('HKCU:\Software\Kofax\PDF\Tungsten Power PDF\' + $pkg.zxtName) -Recurse -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath ('HKCU:\Software\Microsoft\Active Setup\Installed Components\' + (Get-EntryKey $pkg)) -Recurse -Force -ErrorAction SilentlyContinue
            }
            if ($rc -ne 0) { Say (Get-Message $rc) 'Red'; Finish $rc }
            Say (T 'Fertig. Das Add-on wurde entfernt.' 'Done. The add-on was removed.') 'Green'
            Finish 0
        }
    }
} catch {
    Say ((T 'Unerwarteter Fehler: ' 'Unexpected error: ') + $_.Exception.Message) 'Red'
    if ($Action -like 'Machine*' -or $Action -eq 'UserLayout' -or $Quiet) { exit 1 }
    Finish 1
}
