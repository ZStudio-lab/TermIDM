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
    $gh = Get-Command gh -ErrorAction Stop
    if (-not (Test-Path -LiteralPath (Join-Path $root '.git'))) { throw 'Initialize the repository with git init before releasing.' }
    $remotes = @(& $git.Source remote)
    if ($LASTEXITCODE -ne 0 -or $remotes -notcontains 'origin') { throw 'Configure a GitHub remote named origin before releasing: git remote add origin <repository-url>' }
    & $gh.Source auth status
    if ($LASTEXITCODE -ne 0) { throw 'Authenticate the GitHub CLI first with: gh auth login' }
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
    $builtVersion = [IO.File]::ReadAllText((Join-Path $root 'VERSION')).Trim()
    if ($builtVersion -ne $nextVersion) { throw "Expected version $nextVersion, build produced $builtVersion." }
    $archive = Join-Path $root "release\TermIDM-v$builtVersion-Windows-x64.zip"
    $stableArchive = Join-Path $root 'release\TermIDM-Windows-x64.zip'
    if (-not (Test-Path -LiteralPath $archive) -or -not (Test-Path -LiteralPath $stableArchive)) { throw 'Expected release archives were not produced.' }

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
    & $gh.Source release create $tag $archive $stableArchive --title "TermIDM v$builtVersion" --generate-notes
    if ($LASTEXITCODE -ne 0) { throw 'GitHub release creation failed. The commit, tag, and archives are available locally.' }
    Write-Host "Released TermIDM v$builtVersion."
}
finally { Pop-Location }
