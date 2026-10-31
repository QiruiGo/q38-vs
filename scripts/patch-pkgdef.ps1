# patch-pkgdef.ps1 - inject CodeBase into generated pkgdef (both loose file and inside .vsix zip)
# Why: without CodeBase, VS fusion cannot find Q38Vs.dll when the extension-catalog
#      probing path is not refreshed (observed 2026-10-31: FileNotFoundException on
#      CreateInstance for Q38Package). CodeBase makes loading self-contained.
# Called from Q38Vs.csproj target PatchPkgDefCodeBase (AfterTargets=Build).
param(
    [Parameter(Mandatory = $true)][string]$PkgDefPath,   # bin\Debug\Q38Vs.pkgdef
    [Parameter(Mandatory = $true)][string]$VsixPath,     # bin\Debug\Q38Vs.vsix
    [string]$AssemblyName = "Q38Vs"
)
$ErrorActionPreference = "Stop"

$codeBase = '"CodeBase"="$PackageFolder$\' + $AssemblyName + '.dll"'

function Patch-Text([string]$text) {
    if ($text -match 'CodeBase') { return $text }  # already patched
    $anchor = '"Assembly"="' + [regex]::Escape($AssemblyName) + '[^"]*"'
    $m = [regex]::Match($text, $anchor)
    if (-not $m.Success) { throw "Assembly line not found for $AssemblyName" }
    return $text.Substring(0, $m.Index + $m.Length) + "`r`n" + $codeBase + $text.Substring($m.Index + $m.Length)
}

# 1) loose pkgdef next to the dll (used by local F5 experimental deployment)
if (Test-Path $PkgDefPath) {
    $t = [IO.File]::ReadAllText($PkgDefPath)
    $t2 = Patch-Text $t
    if ($t2 -ne $t) {
        [IO.File]::WriteAllText($PkgDefPath, $t2)
        Write-Host "[patch-pkgdef] patched $PkgDefPath"
    }
} else {
    Write-Host "[patch-pkgdef] WARN pkgdef not found: $PkgDefPath"
}

# 2) pkgdef entry inside the .vsix zip (what VSIXInstaller actually deploys)
if (-not (Test-Path $VsixPath)) { Write-Host "[patch-pkgdef] WARN vsix not found: $VsixPath"; exit 0 }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$entryName = $AssemblyName + ".pkgdef"
$zip = [IO.Compression.ZipFile]::Open($VsixPath, "Update")
try {
    $entry = $zip.GetEntry($entryName)
    if ($null -eq $entry) { Write-Host "[patch-pkgdef] WARN no $entryName in vsix"; exit 0 }
    $sr = New-Object IO.StreamReader($entry.Open(), [Text.Encoding]::Unicode)
    $t = $sr.ReadToEnd(); $sr.Close()
    $t2 = Patch-Text $t
    if ($t2 -ne $t) {
        $entry.Delete()
        $new = $zip.CreateEntry($entryName)
        $sw = New-Object IO.StreamWriter($new.Open(), [Text.Encoding]::Unicode)
        $sw.Write($t2); $sw.Close()
        Write-Host "[patch-pkgdef] patched $entryName inside $VsixPath"
    }
} finally { $zip.Dispose() }
