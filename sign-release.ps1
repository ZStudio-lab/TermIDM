[CmdletBinding()]
param(
    [Parameter()]
    [string] $CertificateThumbprint,

    [Parameter()]
    [string] $PfxPath,

    [Parameter()]
    [string] $ExecutablePath,

    [Parameter()]
    [string] $PackagePath,

    [Parameter()]
    [switch] $AllowSelfSigned,

    [Parameter()]
    [string] $TimestampUrl = 'http://timestamp.acs.microsoft.com'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$codeSigningEku = '1.3.6.1.5.5.7.3.3'
$rootExe = Join-Path $PSScriptRoot 'TermIDM.exe'
$releaseExe = Join-Path $PSScriptRoot 'release\TermIDM.exe'
if ($ExecutablePath) {
    $targetExe = (Resolve-Path -LiteralPath $ExecutablePath).Path
}
elseif (Test-Path -LiteralPath $releaseExe -PathType Leaf) {
    $targetExe = (Resolve-Path -LiteralPath $releaseExe).Path
}
elseif (Test-Path -LiteralPath $rootExe -PathType Leaf) {
    $targetExe = (Resolve-Path -LiteralPath $rootExe).Path
}
else {
    throw 'TermIDM.exe was not found. Build it first or pass -ExecutablePath.'
}

$sitePath = Join-Path $PSScriptRoot 'website'
if (-not (Test-Path -LiteralPath (Join-Path $sitePath 'downloads') -PathType Container)) {
    $sitePath = Join-Path $PSScriptRoot 'TermIDM'
}
$defaultZip = Join-Path $sitePath 'downloads\TermIDM-Windows-x64.zip'
$releaseZip = $null
if ($PackagePath) {
    $releaseZip = (Resolve-Path -LiteralPath $PackagePath).Path
}
elseif (Test-Path -LiteralPath $defaultZip -PathType Leaf) {
    $releaseZip = (Resolve-Path -LiteralPath $defaultZip).Path
}

function Find-SignTool {
    $localTools = Get-ChildItem -LiteralPath $PSScriptRoot -Directory -Filter 'SignTool-*' -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName 'signtool.exe' } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }
    if ($localTools) { return ($localTools | Select-Object -First 1) }

    $onPath = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $sdkRoot) {
        $sdkTool = Get-ChildItem -LiteralPath $sdkRoot -Filter 'signtool.exe' -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.Directory.Name -eq 'x64' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($sdkTool) { return $sdkTool.FullName }
    }
    throw 'SignTool was not found. Install the Windows SDK, or place SignTool in a local SignTool-* folder.'
}

function Test-CodeSigningCertificate([System.Security.Cryptography.X509Certificates.X509Certificate2] $Certificate) {
    if (-not $Certificate.HasPrivateKey) { return $false }
    $now = Get-Date
    if ($Certificate.NotBefore -gt $now -or $Certificate.NotAfter -le $now) { return $false }
    $eku = $Certificate.Extensions |
        Where-Object { $_.Oid.Value -eq '2.5.29.37' } |
        Select-Object -First 1
    if (-not $eku) { return $false }
    $eku = [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$eku
    return [bool]($eku.EnhancedKeyUsages | Where-Object { $_.Value -eq $codeSigningEku })
}

if ($CertificateThumbprint -and $PfxPath) {
    throw 'Specify either -CertificateThumbprint or -PfxPath, not both.'
}

$certificate = $null
if ($PfxPath) {
    $resolvedPfx = (Resolve-Path -LiteralPath $PfxPath).Path
    $pfxPassword = Read-Host 'Enter the CA-issued PFX password (input is hidden)' -AsSecureString
    try {
        $imported = Import-PfxCertificate -FilePath $resolvedPfx `
            -CertStoreLocation 'Cert:\CurrentUser\My' -Password $pfxPassword -ErrorAction Stop
    }
    finally {
        $pfxPassword = $null
    }
    $certificate = $imported |
        Where-Object { Test-CodeSigningCertificate $_ } |
        Select-Object -First 1
    if (-not $certificate) {
        throw 'The PFX did not import a valid, currently usable code-signing certificate with its private key.'
    }
}
else {
    $certificates = @(Get-ChildItem -Path 'Cert:\CurrentUser\My' |
        Where-Object { Test-CodeSigningCertificate $_ })
    if ($CertificateThumbprint) {
        $needle = ($CertificateThumbprint -replace '\s', '').ToUpperInvariant()
        $certificate = $certificates |
            Where-Object { ($_.Thumbprint -replace '\s', '').ToUpperInvariant() -eq $needle } |
            Select-Object -First 1
        if (-not $certificate) {
            throw 'That thumbprint is not a valid code-signing certificate with a private key in CurrentUser\My.'
        }
    }
    elseif ($certificates.Count -eq 1) {
        $certificate = $certificates[0]
    }
    elseif ($certificates.Count -gt 1) {
        for ($index = 0; $index -lt $certificates.Count; $index++) {
            Write-Host ("[{0}] {1} | Expires {2:yyyy-MM-dd} | {3}" -f `
                ($index + 1), $certificates[$index].GetNameInfo('SimpleName', $false), `
                $certificates[$index].NotAfter, $certificates[$index].Thumbprint)
        }
        $choice = 0
        if (-not [int]::TryParse((Read-Host 'Choose the CA-issued signing certificate number'), [ref]$choice) -or
            $choice -lt 1 -or $choice -gt $certificates.Count) {
            throw 'Invalid certificate selection.'
        }
        $certificate = $certificates[$choice - 1]
    }
    else {
        throw 'No valid code-signing certificate with a private key was found in CurrentUser\My. Supply a CA-issued PFX with -PfxPath or install the certificate first.'
    }
}

$isSelfSigned = $certificate.Subject -eq $certificate.Issuer
if ($isSelfSigned -and -not $AllowSelfSigned) {
    throw "The certificate is self-signed ($($certificate.Subject)). Add -AllowSelfSigned only if you accept that public users will not see a trusted publisher."
}
if (-not $isSelfSigned) {
    $chain = [System.Security.Cryptography.X509Certificates.X509Chain]::new()
    $chain.ChainPolicy.ApplicationPolicy.Add(
        [System.Security.Cryptography.Oid]::new($codeSigningEku)) | Out-Null
    $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::Online
    $chain.ChainPolicy.RevocationFlag = [System.Security.Cryptography.X509Certificates.X509RevocationFlag]::ExcludeRoot
    if (-not $chain.Build($certificate)) {
        $chainErrors = ($chain.ChainStatus | ForEach-Object { $_.StatusInformation.Trim() } |
            Where-Object { $_ }) -join '; '
        throw "Windows could not build a trusted code-signing chain: $chainErrors"
    }
}

Write-Host "Publisher in certificate: $($certificate.GetNameInfo('SimpleName', $false))"
Write-Host "Certificate subject: $($certificate.Subject)"
Write-Host "Certificate issuer:  $($certificate.Issuer)"
Write-Host "Certificate expires: $($certificate.NotAfter.ToString('yyyy-MM-dd'))"
if ($certificate.Subject -match 'YourPublisherName') {
    Write-Warning 'The certificate subject is the sample placeholder YourPublisherName; this name can appear in signature details.'
}
if ($isSelfSigned) {
    Write-Warning 'This self-signed signature does not establish a trusted publisher for other users or bypass antivirus/SmartScreen.'
}
else {
    $confirmation = Read-Host 'Sign the release with this verified publisher? Type YES to continue'
    if ($confirmation -cne 'YES') { throw 'Signing cancelled.' }
}

$signTool = Find-SignTool
Write-Host "Using SignTool: $signTool"
$temporaryExe = Join-Path (Split-Path -Parent $targetExe) (
    '.' + [System.IO.Path]::GetFileNameWithoutExtension($targetExe) + '.' +
    [guid]::NewGuid().ToString('N') + '.signed.exe')
try {
    Copy-Item -LiteralPath $targetExe -Destination $temporaryExe
    & $signTool sign /sha1 $certificate.Thumbprint /s My /fd SHA256 /tr $TimestampUrl /td SHA256 $temporaryExe
    if ($LASTEXITCODE -ne 0) { throw "SignTool failed to sign the executable (exit code $LASTEXITCODE)." }

    $signature = Get-AuthenticodeSignature -LiteralPath $temporaryExe
    if ($isSelfSigned) {
        $untrustedRootOnly = $signature.Status -eq 'UnknownError' -and
            $signature.StatusMessage -match 'root certificate which is not trusted by the trust provider'
        if ($signature.Status -notin @('Valid', 'NotTrusted') -and -not $untrustedRootOnly -or
            -not $signature.SignerCertificate -or
            $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
            throw "The self-signed file signature did not validate correctly: $($signature.Status) - $($signature.StatusMessage)"
        }
        if ($untrustedRootOnly) {
            Write-Warning 'The signature and file digest are present, but Windows does not trust this self-signed certificate.'
        }
    }
    else {
        & $signTool verify /pa /v $temporaryExe
        if ($LASTEXITCODE -ne 0) { throw "SignTool could not verify the signed executable (exit code $LASTEXITCODE)." }
    }
    if (-not $isSelfSigned -and $signature.Status -ne 'Valid') {
        throw "Windows reports the new signature as $($signature.Status): $($signature.StatusMessage)"
    }
    Move-Item -LiteralPath $temporaryExe -Destination $targetExe -Force
}
catch {
    if (Test-Path -LiteralPath $temporaryExe) { Remove-Item -LiteralPath $temporaryExe -Force }
    throw
}

# Synchronize the convenient root copy only after SignTool confirms the signature.
if ($targetExe -ne ( [System.IO.Path]::GetFullPath($rootExe) )) {
    Copy-Item -LiteralPath $targetExe -Destination $rootExe -Force
}

# Rebuild the Windows ZIP through a temporary archive so a failed update does not
# leave the published package half-written. No SignTool, PFX, or private key is added.
if ($releaseZip) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zipTemp = Join-Path (Split-Path -Parent $releaseZip) ('.TermIDM-Windows-x64.' + [guid]::NewGuid().ToString('N') + '.tmp')
    $sourceArchive = $null
    $targetArchive = $null
    try {
        $sourceArchive = [System.IO.Compression.ZipFile]::OpenRead($releaseZip)
        $requiredEntries = @('TermIDM.exe', 'TermIDM.Engine.exe', 'VERSION', 'libcurl-x64.dll',
            'libcurl-4.dll', 'libc++.dll', 'libunwind.dll')
        foreach ($required in $requiredEntries) {
            if (-not ($sourceArchive.Entries | Where-Object { $_.FullName -ceq $required })) {
                throw "The release ZIP is missing required entry: $required"
            }
        }

        $targetArchive = [System.IO.Compression.ZipFile]::Open(
            $zipTemp, [System.IO.Compression.ZipArchiveMode]::Create)
        foreach ($entry in $sourceArchive.Entries) {
            if ($entry.FullName -ceq 'TermIDM.exe') { continue }
            $newEntry = $targetArchive.CreateEntry(
                $entry.FullName, [System.IO.Compression.CompressionLevel]::Optimal)
            $inputStream = $entry.Open()
            $outputStream = $newEntry.Open()
            try { $inputStream.CopyTo($outputStream) }
            finally { $outputStream.Dispose(); $inputStream.Dispose() }
        }
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $targetArchive, $targetExe, 'TermIDM.exe',
            [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    finally {
        if ($targetArchive) { $targetArchive.Dispose() }
        if ($sourceArchive) { $sourceArchive.Dispose() }
    }

    try {
        Move-Item -LiteralPath $zipTemp -Destination $releaseZip -Force
    }
    catch {
        if (Test-Path -LiteralPath $zipTemp) { Remove-Item -LiteralPath $zipTemp -Force }
        throw
    }
}

if ($releaseZip) { Write-Host 'Signed executable and refreshed Windows release ZIP.' }
else { Write-Host 'Signed executable. No Windows release ZIP was found, so no package was changed.' }
if ($isSelfSigned) { Write-Warning 'The release is cryptographically signed, but other users will see it as untrusted unless they independently trust this certificate.' }
Write-Host 'The source archive remains source-only and unsigned; SignTool and certificate files are not distributed.'
