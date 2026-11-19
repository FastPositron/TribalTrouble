# Builds TribalTrouble_RUS.exe from the translations folder of this repo.
# Nothing to install: uses csc.exe from the .NET Framework that ships with Windows.
# (ASCII only on purpose: Windows PowerShell 5.1 misreads UTF-8 scripts without a BOM.)
param(
    [string]$Src = "$PSScriptRoot\translations",
    [string]$Out = "$PSScriptRoot\dist\TribalTrouble_RUS.exe"
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Src)) { throw "Translation folder not found: $Src" }
$Src = (Resolve-Path $Src).Path
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zipPath = Join-Path $PSScriptRoot "payload.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Confirm:$false }

$zip = [IO.Compression.ZipFile]::Open($zipPath, 'Create')
try {
    $files = Get-ChildItem $Src -Recurse -File | Where-Object { $_.Name -like '*_ru.properties' }
    foreach ($f in $files) {
        $name = $f.FullName.Substring($Src.Length + 1).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, $name, 'Optimal')
    }
    "Files in payload: $($files.Count)"
} finally { $zip.Dispose() }

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:winexe /optimize+ /codepage:65001 `
    "/out:$Out" "/resource:$zipPath,payload.zip" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
    (Join-Path $PSScriptRoot "Rusifier.cs")
if ($LASTEXITCODE -ne 0) { throw "csc failed" }

Get-Item $Out | Select-Object FullName, Length
