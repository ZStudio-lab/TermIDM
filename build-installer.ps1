[CmdletBinding()]
param(
    [string] $Compiler = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$version = [IO.File]::ReadAllText((Join-Path $root 'VERSION')).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'VERSION must contain major.minor.patch.' }
$appFolder = Join-Path $root "release\TermIDM-v$version-Windows-x64"
$installerSource = Join-Path $root 'installer\TermIDM.iss'
$outputPath = Join-Path $root "release\TermIDM-Setup-v$version-Windows-x64.exe"
$stableOutputPath = Join-Path $root 'release\TermIDM-Setup-Windows-x64.exe'

if (-not (Test-Path -LiteralPath $Compiler -PathType Leaf)) {
    $resolved = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($null -eq $resolved) { throw 'Inno Setup 6 is required. Install it, then rerun build-installer.ps1.' }
    $Compiler = $resolved.Source
}
foreach ($required in @('TermIDM.exe', 'EngineBridge.dll', 'TermIDM.pri', 'App.xbf', 'MainWindow.xbf', 'assets\termidm.ico')) {
    if (-not (Test-Path -LiteralPath (Join-Path $appFolder $required) -PathType Leaf)) {
        throw "Published app is incomplete: $required is missing from $appFolder. Run build.ps1 first."
    }
}

$arguments = @(
    '/Q'
    "/DAppVersion=$version"
    "/DReleaseDir=$appFolder"
    "/DAssetDir=$(Join-Path $root 'assets')"
    "/DProjectRoot=$root"
    "/O$(Join-Path $root 'release')"
    "/FTermIDM-Setup-v$version-Windows-x64"
    $installerSource
)
& $Compiler @arguments
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE." }
if (-not (Test-Path -LiteralPath $outputPath -PathType Leaf)) { throw "Installer output was not created: $outputPath" }
[IO.File]::Copy($outputPath, $stableOutputPath, $true)
Get-ChildItem -LiteralPath (Join-Path $root 'release') -File -Filter 'TermIDM-Setup-v*-Windows-x64.exe' |
    Where-Object FullName -NE $outputPath |
    ForEach-Object { try { [IO.File]::Delete($_.FullName) } catch { Write-Warning "Could not remove old installer $($_.Name): $($_.Exception.Message)" } }
Write-Host "Created TermIDM installer: $outputPath"
