[CmdletBinding()]
param(
    [string] $CommitMessage
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Push-Location $root

try {
    # Verify git and remote
    $git = Get-Command git -ErrorAction Stop
    $remotes = cmd /c "git remote" 2>$null
    if ($LASTEXITCODE -ne 0 -or $remotes -notcontains 'origin') {
        throw 'Configure a GitHub remote named origin before releasing.'
    }
    $originUrl = (cmd /c "git remote get-url origin" 2>$null).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $originUrl) {
        throw 'Could not read origin URL.'
    }

    # Check GitHub CLI auth
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    $releaseMethod = $null
    if ($gh) {
        cmd /c "gh auth status" *> $null
        if ($LASTEXITCODE -eq 0) { $releaseMethod = 'gh' }
    }
    if (-not $releaseMethod -and -not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) {
        $releaseMethod = 'api'
    }
    if (-not $releaseMethod) {
        throw 'No authenticated GitHub release method available. Run: gh auth login'
    }

    # Parse owner/repo from origin URL
    $owner = $null
    $repo = $null
    if ($originUrl -match '^https?://github\.com/([^/]+)/([^/]+?)(?:\.git)?/?$') {
        $owner = $Matches[1]; $repo = $Matches[2]
    } elseif ($originUrl -match '^git@github\.com:([^/]+)/([^/]+?)(?:\.git)?$') {
        $owner = $Matches[1]; $repo = $Matches[2]
    }

    $branch = (cmd /c "git branch --show-current" 2>$null).Trim()
    if (-not $branch) { throw 'Check out a named branch before releasing.' }

    # Read version and release notes
    $version = [IO.File]::ReadAllText((Join-Path $root 'VERSION')).Trim()
    $parts = $version.Split('.')
    if ($parts.Length -ne 3 -or $version -notmatch '^\d+\.\d+\.\d+$') { throw 'VERSION must be major.minor.patch.' }
    $nextVersion = '{0}.{1}.{2}' -f $parts[0], $parts[1], ([long]$parts[2] + 1)
    $tag = "v$nextVersion"
    $notesFile = Join-Path $root "release-notes\$tag.md"
    if (-not (Test-Path -LiteralPath $notesFile -PathType Leaf)) {
        throw "Add release notes first: $notesFile"
    }

    # Check tag doesn't exist
    cmd /c "git rev-parse -q --verify refs/tags/$tag" *> $null
    if ($LASTEXITCODE -eq 0) { throw "Tag $tag already exists." }

    Write-Host "Building TermIDM v$nextVersion..."

    # Build
    & (Join-Path $root 'build.ps1') 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

    & (Join-Path $root 'build-installer.ps1') 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }

    $builtVersion = [IO.File]::ReadAllText((Join-Path $root 'VERSION')).Trim()
    if ($builtVersion -ne $nextVersion) { throw "Expected $nextVersion, got $builtVersion" }

    $archive = Join-Path $root "release\TermIDM-v$builtVersion-Windows-x64.zip"
    $stableArchive = Join-Path $root 'release\TermIDM-Windows-x64.zip'
    $setupExe = Join-Path $root "release\TermIDM-Setup-v$builtVersion-Windows-x64.exe"
    $stableSetupExe = Join-Path $root 'release\TermIDM-Setup-Windows-x64.exe'
    if (-not (Test-Path -LiteralPath $archive) -or -not (Test-Path -LiteralPath $stableArchive) -or
        -not (Test-Path -LiteralPath $setupExe) -or -not (Test-Path -LiteralPath $stableSetupExe)) {
        throw 'Release archives or setup programs were not produced.'
    }

    Write-Host "Committing and tagging..."

    # Git operations using cmd /c to avoid PowerShell stderr issues
    cmd /c "git add --all 2>&1" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'git add failed.' }

    $message = if ([string]::IsNullOrWhiteSpace($CommitMessage)) { "Release TermIDM v$builtVersion" } else { $CommitMessage }
    cmd /c "git commit -m `"$message`"" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'git commit failed.' }

    cmd /c "git tag -a $tag -m `"TermIDM v$builtVersion`"" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not create tag $tag." }

    Write-Host "Pushing to origin..."
    cmd /c "git push origin $branch" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Branch push failed.' }

    cmd /c "git push origin $tag" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Tag push failed.' }

    Write-Host "Creating GitHub release..."
    if ($releaseMethod -eq 'gh') {
        cmd /c "gh release create $tag $archive $stableArchive $setupExe $stableSetupExe --title `"TermIDM v$builtVersion`" --notes-file `"$notesFile`"" 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'GitHub release creation failed.' }
    } else {
        $headers = @{
            Authorization = "Bearer $env:GITHUB_TOKEN"
            Accept = 'application/vnd.github+json'
            'X-GitHub-Api-Version' = '2022-11-28'
            'User-Agent' = 'TermIDM-release-script'
        }
        $body = [IO.File]::ReadAllText($notesFile)
        $payload = @{
            tag_name = $tag
            name = "TermIDM v$builtVersion"
            target_commitish = $branch
            body = $body
        } | ConvertTo-Json
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$owner/$repo/releases" -Method Post -Headers $headers -ContentType 'application/json' -Body $payload
        $uploadBase = $release.upload_url -replace '\{.*$', ''
        foreach ($asset in @($archive, $stableArchive, $setupExe, $stableSetupExe)) {
            $assetName = [Uri]::EscapeDataString([IO.Path]::GetFileName($asset))
            $contentType = if ($asset.EndsWith('.exe')) { 'application/vnd.microsoft.portable-executable' } else { 'application/zip' }
            Invoke-RestMethod -Uri "$uploadBase`?name=$assetName" -Method Post -Headers $headers -ContentType $contentType -InFile $asset | Out-Null
        }
    }

    Write-Host "Successfully released TermIDM v$builtVersion!"
}
finally {
    Pop-Location
}
