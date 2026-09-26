[CmdletBinding()]
param(
    [string] $CommitMessage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Push-Location $root
try {
    $git = Get-Command git -ErrorAction Stop
    if (-not (Test-Path -LiteralPath (Join-Path $root '.git'))) { throw 'Initialize the repository with git init before releasing.' }
    $remotes = @(& $git.Source remote)
    if ($LASTEXITCODE -ne 0 -or $remotes -notcontains 'origin') { throw 'Configure a GitHub remote named origin before releasing: git remote add origin <repository-url>' }
    $originUrl = (& $git.Source remote get-url origin).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $originUrl) { throw 'Could not read origin. Configure it with: git remote set-url origin <repository-url>' }

    # Prefer GitHub CLI when it is installed and authenticated. Otherwise use
    # the GitHub REST API with GITHUB_TOKEN; never prompt for or print a token.
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    $releaseMethod = $null
    if ($gh) {
        & $gh.Source auth status *> $null
        if ($LASTEXITCODE -eq 0) { $releaseMethod = 'gh' }
    }
    if (-not $releaseMethod -and -not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) {
        $releaseMethod = 'api'
    }
    if (-not $releaseMethod) {
        throw 'No authenticated GitHub release method is available. Install/authenticate GitHub CLI (winget install --id GitHub.cli; then gh auth login), or set GITHUB_TOKEN to a token with repository Contents and Releases write access. No version or files were changed.'
    }

    $owner = $null
    $repo = $null
    if ($originUrl -match '^https?://github\.com/([^/]+)/([^/]+?)(?:\.git)?/?$') {
        $owner = $Matches[1]; $repo = $Matches[2]
    } elseif ($originUrl -match '^git@github\.com:([^/]+)/([^/]+?)(?:\.git)?$') {
        $owner = $Matches[1]; $repo = $Matches[2]
    } elseif ($releaseMethod -eq 'api') {
        throw "GITHUB_TOKEN release publishing requires a GitHub origin URL. Current origin: $originUrl"
    }
    $branch = (& $git.Source branch --show-current).Trim()
    if (-not $branch) { throw 'Check out a named branch before releasing.' }

    $version = [IO.File]::ReadAllText((Join-Path $root 'VERSION')).Trim()
    $parts = $version.Split('.')
    if ($parts.Length -ne 3 -or $version -notmatch '^\d+\.\d+\.\d+$') { throw 'VERSION must contain major.minor.patch.' }
    $nextVersion = '{0}.{1}.{2}' -f $parts[0], $parts[1], ([long]$parts[2] + 1)
    $tag = "v$nextVersion"
    & $git.Source rev-parse -q --verify "refs/tags/$tag" *> $null
    if ($LASTEXITCODE -eq 0) { throw "Tag $tag already exists; update VERSION before attempting another release." }

    & (Join-Path $root 'build.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    & (Join-Path $root 'build-installer.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
    $builtVersion = [IO.File]::ReadAllText((Join-Path $root 'VERSION')).Trim()
    if ($builtVersion -ne $nextVersion) { throw "Expected version $nextVersion, build produced $builtVersion." }
    $archive = Join-Path $root "release\TermIDM-v$builtVersion-Windows-x64.zip"
    $stableArchive = Join-Path $root 'release\TermIDM-Windows-x64.zip'
    $setupExe = Join-Path $root "release\TermIDM-Setup-v$builtVersion-Windows-x64.exe"
    $stableSetupExe = Join-Path $root 'release\TermIDM-Setup-Windows-x64.exe'
    if (-not (Test-Path -LiteralPath $archive) -or -not (Test-Path -LiteralPath $stableArchive) -or -not (Test-Path -LiteralPath $setupExe) -or -not (Test-Path -LiteralPath $stableSetupExe)) { throw 'Expected release archives or setup programs were not produced.' }

    & $git.Source add --all
    if ($LASTEXITCODE -ne 0) { throw 'git add failed.' }
    $message = if ([string]::IsNullOrWhiteSpace($CommitMessage)) { "Release TermIDM v$builtVersion" } else { $CommitMessage }
    & $git.Source commit -m $message
    if ($LASTEXITCODE -ne 0) { throw 'git commit failed. Resolve the repository state before retrying.' }
    & $git.Source tag -a $tag -m "TermIDM v$builtVersion"
    if ($LASTEXITCODE -ne 0) { throw "Could not create release tag $tag." }
    & $git.Source push origin $branch
    if ($LASTEXITCODE -ne 0) { throw 'Branch push failed. The release commit and tag remain local; correct the remote and retry the push.' }
    & $git.Source push origin $tag
    if ($LASTEXITCODE -ne 0) { throw 'Tag push failed. The release tag remains local; correct the remote and retry the tag push.' }
    if ($releaseMethod -eq 'gh') {
        & $gh.Source release create $tag $archive $stableArchive $setupExe $stableSetupExe --title "TermIDM v$builtVersion" --generate-notes
        if ($LASTEXITCODE -ne 0) { throw 'GitHub release creation failed. The commit, tag, and archives are available locally.' }
    } else {
        $headers = @{
            Authorization = "Bearer $env:GITHUB_TOKEN"
            Accept = 'application/vnd.github+json'
            'X-GitHub-Api-Version' = '2022-11-28'
            'User-Agent' = 'TermIDM-release-script'
        }
        $payload = @{
            tag_name = $tag
            name = "TermIDM v$builtVersion"
            target_commitish = $branch
            generate_release_notes = $true
        } | ConvertTo-Json
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$owner/$repo/releases" -Method Post -Headers $headers -ContentType 'application/json' -Body $payload
        $uploadBase = $release.upload_url -replace '\{.*$', ''
        foreach ($asset in @($archive, $stableArchive, $setupExe, $stableSetupExe)) {
            $assetName = [Uri]::EscapeDataString([IO.Path]::GetFileName($asset))
            $contentType = if ($asset.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) { 'application/zip' } else { 'application/vnd.microsoft.portable-executable' }
            Invoke-RestMethod -Uri "$uploadBase`?name=$assetName" -Method Post -Headers $headers -ContentType $contentType -InFile $asset | Out-Null
        }
    }
    Write-Host "Released TermIDM v$builtVersion."
}
finally { Pop-Location }
