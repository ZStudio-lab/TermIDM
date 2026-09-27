[CmdletBinding()]
param(
    [switch] $NoVersionIncrement,
    [string] $Compiler = 'g++',
    [string] $CurlRoot = (Join-Path $PSScriptRoot 'build-deps\curl\curl-8.22.0_2-win64-mingw')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$utf8 = [System.Text.UTF8Encoding]::new($false)
$versionPath = Join-Path $PSScriptRoot 'VERSION'
$configPath = Join-Path $PSScriptRoot 'build_config.h'
$oldVersion = [IO.File]::ReadAllText($versionPath).Trim()
$pagePath = Join-Path $PSScriptRoot 'index.html'
$oldPage = if (Test-Path -LiteralPath $pagePath) { [IO.File]::ReadAllText($pagePath) } else { $null }
if ($oldVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'VERSION must contain major.minor.patch.' }
$version = $oldVersion
if (-not $NoVersionIncrement) {
    $parts = $oldVersion.Split('.')
    $patch = [long]$parts[2]
    if ($patch -eq [long]::MaxValue) { throw 'Version patch component cannot be incremented.' }
    $version = '{0}.{1}.{2}' -f $parts[0], $parts[1], ($patch + 1)
    [IO.File]::WriteAllText($versionPath, "$version`r`n", $utf8)
    [IO.File]::WriteAllText($configPath, "#pragma once`r`n`r`n#define TERMIDM_VERSION `"$version`"`r`n", $utf8)
    if (Test-Path -LiteralPath $pagePath) {
        $page = [IO.File]::ReadAllText($pagePath)
        $page = [regex]::Replace($page, '(<meta name="termidm-version" content=")[^"]*(">)', [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $m.Groups[1].Value + $version + $m.Groups[2].Value })
        $page = [regex]::Replace($page, '(<span id="version">)[^<]*(</span>)', [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $m.Groups[1].Value + $version + $m.Groups[2].Value })
        [IO.File]::WriteAllText($pagePath, $page, $utf8)
    }
}

$buildDir = Join-Path $PSScriptRoot 'build'
$stage = Join-Path $PSScriptRoot "release\TermIDM-v$version-Windows-x64"
$releaseDir = Join-Path $PSScriptRoot 'release'
$engineExe = Join-Path $buildDir 'TermIDM.Engine.exe'
$nativeResource = Join-Path $buildDir 'app-resource.o'
try {
    New-Item -ItemType Directory -Path $buildDir -Force | Out-Null
    if ([IO.Directory]::Exists($stage)) { [IO.Directory]::Delete($stage, $true) }
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    $windres = Get-Command windres -ErrorAction Stop
    & $windres.Source -i (Join-Path $PSScriptRoot 'app.rc') -o $nativeResource -O coff
    if ($LASTEXITCODE -ne 0) { throw "windres failed with exit code $LASTEXITCODE." }

    $curlInclude = Join-Path $CurlRoot 'include'
    $curlLib = Join-Path $CurlRoot 'lib'
    if (-not (Test-Path (Join-Path $curlInclude 'curl\curl.h')) -or
        -not (Test-Path (Join-Path $curlLib 'libcurl.dll.a'))) { throw "libcurl development files were not found under $CurlRoot." }
    $compilerCommand = Get-Command $Compiler -ErrorAction Stop

    # Compile the EngineBridge.dll (P/Invoke bridge for in-process engine)
    $bridgeDll = Join-Path $buildDir 'EngineBridge.dll'
    & $compilerCommand.Source -std=c++17 -O2 -shared -DENGINEBRIDGE_EXPORTS `
        -I $curlInclude `
        (Join-Path $PSScriptRoot 'TermIDM.Desktop\EngineBridge.cpp') `
        -L $curlLib '-Wl,-Bstatic' '-l:libcurl.dll.a' `
        '-Wl,-Bdynamic' -lws2_32 -lcrypt32 -lwldap32 -o $bridgeDll
    if ($LASTEXITCODE -ne 0) { throw "EngineBridge DLL compilation failed with exit code $LASTEXITCODE." }

    & $compilerCommand.Source -std=c++17 -O2 -Wall -Wextra -Wpedantic -municode -mwindows -I $curlInclude `
        (Join-Path $PSScriptRoot 'main.cpp') $nativeResource -L $curlLib '-Wl,-Bstatic' '-l:libcurl.dll.a' `
        '-Wl,-Bdynamic' -lcomctl32 -lole32 -lshell32 -lgdi32 -luuid -o $engineExe
    if ($LASTEXITCODE -ne 0) { throw "C++ engine compilation failed with exit code $LASTEXITCODE." }

    $desktopProject = Join-Path $PSScriptRoot 'TermIDM.Desktop\TermIDM.Desktop.csproj'
    & dotnet publish $desktopProject -c Release -r win-x64 --self-contained true `
        -p:TermIDMVersion=$version -p:PublishSingleFile=false -p:WindowsAppSDKSelfContained=true `
        -p:DebugType=None -o $stage
    if ($LASTEXITCODE -ne 0) { throw "WinUI 3 publish failed with exit code $LASTEXITCODE." }

    $desktopBuildOutput = Join-Path $PSScriptRoot 'TermIDM.Desktop\bin\Release\net8.0-windows10.0.19041.0\win-x64'
    $appPri = Join-Path $desktopBuildOutput 'TermIDM.pri'
    $compiledXaml = @(Get-ChildItem -LiteralPath $desktopBuildOutput -Filter '*.xbf' -File -ErrorAction SilentlyContinue)
    if (-not (Test-Path -LiteralPath $appPri -PathType Leaf) -or $compiledXaml.Count -eq 0) {
        throw 'WinUI publish is missing its PRI or compiled XAML resources.'
    }
    Copy-Item -LiteralPath $appPri -Destination $stage -Force
    Copy-Item -LiteralPath $compiledXaml.FullName -Destination $stage -Force
    Copy-Item -LiteralPath $engineExe -Destination (Join-Path $stage 'TermIDM.Engine.exe') -Force
    Copy-Item -LiteralPath $bridgeDll -Destination (Join-Path $stage 'EngineBridge.dll') -Force
    Copy-Item -LiteralPath $versionPath -Destination (Join-Path $stage 'VERSION') -Force
    $toolchainBin = Split-Path -Parent $compilerCommand.Source
    $runtimeSources = @{
        'libc++.dll' = Join-Path $toolchainBin 'libc++.dll'
        'libunwind.dll' = Join-Path $toolchainBin 'libunwind.dll'
        'libcurl-x64.dll' = Join-Path $CurlRoot 'bin\libcurl-x64.dll'
        'curl-ca-bundle.crt' = Join-Path $CurlRoot 'bin\curl-ca-bundle.crt'
    }
    foreach ($entry in $runtimeSources.GetEnumerator()) {
        if (-not (Test-Path -LiteralPath $entry.Value -PathType Leaf)) { throw "Required native runtime file is missing: $($entry.Value)" }
        Copy-Item -LiteralPath $entry.Value -Destination (Join-Path $stage $entry.Key) -Force
    }

    $versionedZip = Join-Path $releaseDir "TermIDM-v$version-Windows-x64.zip"
    $stableZip = Join-Path $releaseDir 'TermIDM-Windows-x64.zip'
    foreach ($archivePath in @($versionedZip, $stableZip)) { if ([IO.File]::Exists($archivePath)) { [IO.File]::Delete($archivePath) } }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $versionedZip -CompressionLevel Optimal
    Copy-Item -LiteralPath $versionedZip -Destination $stableZip -Force
    foreach ($oldFolder in Get-ChildItem -LiteralPath $releaseDir -Directory -Filter 'TermIDM-v*-Windows-x64' | Where-Object { $_.FullName -NE $stage }) {
        try { [IO.Directory]::Delete($oldFolder.FullName, $true) } catch { Write-Warning "Could not remove old build folder $($oldFolder.Name): $($_.Exception.Message)" }
    }
    foreach ($pattern in @('TermIDM-v*-Windows-x64.zip', 'TermIDM-Setup-v*-Windows-x64.exe')) {
        $files = Get-ChildItem -LiteralPath $releaseDir -File -Filter $pattern | Where-Object { $_.Name -notin @((Split-Path -Leaf $versionedZip), "TermIDM-Setup-v$version-Windows-x64.exe") }
        foreach ($file in $files) {
            try { [IO.File]::Delete($file.FullName) } catch { Write-Warning "Could not remove old release file $($file.Name): $($_.Exception.Message)" }
        }
    }
    Write-Host "Published unsigned TermIDM v$version to $versionedZip"
    Write-Host 'The ZIP contains only the self-contained WinUI app, native engine, and required runtime files.'
}
catch {
    if (-not $NoVersionIncrement) {
        [IO.File]::WriteAllText($versionPath, "$oldVersion`r`n", $utf8)
        $previousConfig = "#pragma once`r`n`r`n#define TERMIDM_VERSION `"$oldVersion`"`r`n"
        [IO.File]::WriteAllText($configPath, $previousConfig, $utf8)
        if ($null -ne $oldPage) { [IO.File]::WriteAllText($pagePath, $oldPage, $utf8) }
    }
    throw
}
