# SliceArc one-click packaging script
# Builds the portable single-file EXE and optionally the Inno Setup installer.
#
# Usage:
#   .\tools\package.ps1              — portable (open/green) build
#   .\tools\package.ps1 -Installer   — portable + installer
#
# Prerequisites for -Installer:
#   Inno Setup 6 installed (https://jrsoftware.org/isdl.php)

param(
    [switch]$Installer
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$portableDir = "$root\dist\portable"

# A leftover GUI process locks the publish output (MSB4018); a COM Surrogate
# (dllhost) keeps SliceArc.ShellExt.dll loaded after any right-click.
Get-Process SliceArc.App -ErrorAction SilentlyContinue | Stop-Process -Force
foreach ($p in Get-Process dllhost -ErrorAction SilentlyContinue) {
    try {
        foreach ($m in $p.Modules) {
            if ($m.FileName -like "*SliceArc.ShellExt*") { Stop-Process -Id $p.Id -Force; break }
        }
    } catch { } # elevated or exiting processes deny module enumeration
}

Write-Host "=== Building portable single-file EXE ===" -ForegroundColor Cyan
dotnet publish "$root\src\SliceArc.App\SliceArc.App.csproj" -c Release -p:PublishProfile=Portable
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$exe = "$portableDir\SliceArc.App.exe"
$size = (Get-Item $exe).Length / 1MB
Write-Host "Portable EXE: $exe ($([math]::Round($size, 1)) MB)" -ForegroundColor Green

# ---------------------------------------------------------------------
# Modern (Windows 11) context menu: COM extension + signed sparse MSIX.
# The MSIX is packed and signed here at build time, so end users need
# neither the Windows SDK nor the signing private key.
# ---------------------------------------------------------------------
Write-Host "`n=== Building ShellExt (COM host, framework-dependent) ===" -ForegroundColor Cyan
dotnet publish "$root\src\SliceArc.ShellExt\SliceArc.ShellExt.csproj" -c Release -r win-x64 --self-contained false -o $portableDir
if ($LASTEXITCODE -ne 0) { throw "ShellExt publish failed" }

Write-Host "`n=== Packing + signing sparse MSIX ===" -ForegroundColor Cyan
$sdkBin = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64"
$makeAppx = Join-Path $sdkBin "makeappx.exe"
$signTool = Join-Path $sdkBin "signtool.exe"
if (-not (Test-Path $makeAppx)) { throw "makeappx.exe not found: $makeAppx" }
if (-not (Test-Path $signTool)) { throw "signtool.exe not found: $signTool" }

$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq "CN=SliceArc" -and $_.HasPrivateKey } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1
if (-not $cert) { throw "Signing certificate CN=SliceArc with private key not found in CurrentUser\My" }
Write-Host "Signing with certificate $($cert.Thumbprint) (expires $($cert.NotAfter.ToString('yyyy-MM-dd')))"

$staging = Join-Path $env:TEMP "SliceArcMsix"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path "$staging\Assets" -Force | Out-Null
Copy-Item "$root\src\SliceArc.ShellExt\Package.appxmanifest" "$staging\AppxManifest.xml"
Copy-Item "$root\src\SliceArc.ShellExt\Assets\Logo.png" "$staging\Assets\Logo.png"

$msix = "$portableDir\SliceArc.ContextMenu.msix"
if (Test-Path $msix) { Remove-Item $msix -Force }
& $makeAppx pack /d $staging /p $msix /nv | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed" }

& $signTool sign /fd SHA256 /sha1 $cert.Thumbprint $msix | Out-Null
if ($LASTEXITCODE -ne 0) { throw "signtool sign failed" }

$cerFile = "$portableDir\slicearc-sparse.cer"
Export-Certificate -Cert $cert -FilePath $cerFile -Force | Out-Null
Remove-Item $staging -Recurse -Force
Write-Host "Sparse MSIX: $msix" -ForegroundColor Green
Write-Host "Public cert: $cerFile" -ForegroundColor Green

if ($Installer) {
    Write-Host "`n=== Building installer ===" -ForegroundColor Cyan

    # Find ISCC.exe
    $iscc = Get-Command iscc -ErrorAction SilentlyContinue
    if (-not $iscc) {
        $candidates = @(
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
        )
        foreach ($c in $candidates) {
            if (Test-Path $c) { $iscc = $c; break }
        }
    }
    if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found. Install from https://jrsoftware.org/isdl.php" }

    & $iscc "$root\tools\SliceArc.iss"
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed (SliceArc.iss)" }

    Get-ChildItem "$root\dist\SliceArcSetup-*.exe" |
        Sort-Object LastWriteTime -Descending |
        ForEach-Object { Write-Host "Installer: $($_.FullName) ($([math]::Round($_.Length / 1MB, 1)) MB)" -ForegroundColor Green }
}

Write-Host "`nDone." -ForegroundColor Green
