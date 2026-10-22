# Builds artifacts\Nexus-<version>-windows-x64-setup.exe (Inno Setup 6 required: iscc on PATH
# or in its default install folder).  Usage:  pwsh packaging/windows/build.ps1
#
# Signing (optional): if WINDOWS_CERT_PFX (path) and WINDOWS_CERT_PASSWORD are set, Nexus.exe
# and the installer are Authenticode-signed so SmartScreen doesn't warn.
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$rid = 'win-x64'
$version = (Select-String -Path (Join-Path $root 'Nexus.csproj') -Pattern '<Version>(.+)</Version>').Matches[0].Groups[1].Value
$out = Join-Path $root 'artifacts'
$work = Join-Path $out "work\$rid"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $work, $out | Out-Null

Write-Host ">> publishing $rid ($version)"
dotnet publish (Join-Path $root 'Nexus.csproj') -c Release -r $rid --self-contained true `
    -p:DebugType=none -p:DebugSymbols=false -o (Join-Path $work 'app')
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

function Sign-File($file) {
    if (-not $env:WINDOWS_CERT_PFX) { return }
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" |
        Sort-Object FullName -Descending | Select-Object -First 1
    & $signtool.FullName sign /f $env:WINDOWS_CERT_PFX /p $env:WINDOWS_CERT_PASSWORD `
        /fd sha256 /tr http://timestamp.digicert.com /td sha256 $file
    if ($LASTEXITCODE -ne 0) { throw "signing $file failed" }
}
Sign-File (Join-Path $work 'app\Nexus.exe')

$iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source
if (-not $iscc) { $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" }
if (-not (Test-Path $iscc)) { throw 'Inno Setup 6 (ISCC.exe) not found — install it from https://jrsoftware.org/isdl.php' }

Write-Host '>> building installer'
& $iscc /Q "/DAppVersion=$version" "/DSourceDir=$(Join-Path $work 'app')" "/DOutputDir=$out" `
    (Join-Path $PSScriptRoot 'nexus.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }

$setup = Join-Path $out "Nexus-$version-windows-x64-setup.exe"
Sign-File $setup
Write-Host ">> done: $setup"
