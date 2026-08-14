<#
.SYNOPSIS
    Generates a self-signed TLS certificate for the master server and prints the SHA-256
    fingerprint the client must pin (phase 03 task 2).

.DESCRIPTION
    A VPS with an IP address and no domain cannot get a Let's Encrypt certificate, and a LAN
    test run does not want one. The alternative to a self-signed certificate is not "a real
    certificate" — it is plaintext, which puts every password hash and session token on the
    wire in the clear.

    The output is a PKCS#12 bundle plus a fingerprint. The server gets the bundle; the client
    gets the fingerprint and accepts that certificate and nothing else. That is STRICTER than
    normal CA validation, not weaker: a mis-issued certificate from any CA on earth still
    fails the pin.

    Never solve a self-signed certificate with a callback that returns true. That does not
    weaken validation, it removes it — and encrypted-to-an-attacker is indistinguishable from
    encrypted-to-the-server from the inside.

.PARAMETER Subject
    The DNS name clients connect to. Use the VPS hostname, or 'localhost' for a LAN run.

.PARAMETER AlsoValidFor
    Extra DNS names or IP addresses to place in the subject-alternative-name extension.

.PARAMETER OutputPath
    Destination .pfx path. Defaults to ./certs/ironfront-master.pfx.

.EXAMPLE
    ./tools/new-dev-cert.ps1 -Subject localhost -AlsoValidFor 127.0.0.1

.EXAMPLE
    ./tools/new-dev-cert.ps1 -Subject ironfront.example.com -AlsoValidFor 203.0.113.10
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]   $Subject,
    [string[]] $AlsoValidFor = @(),
    [string]   $OutputPath = "./certs/ironfront-master.pfx",
    [int]      $ValidDays = 365
)

$ErrorActionPreference = 'Stop'

$directory = Split-Path -Parent $OutputPath
if ($directory -and -not (Test-Path $directory)) {
    New-Item -ItemType Directory -Path $directory | Out-Null
}

# A random password, printed once. A fixed one here would end up copied into a deployment
# script, and from there into git.
$passwordBytes = [byte[]]::new(24)
[System.Security.Cryptography.RandomNumberGenerator]::Fill($passwordBytes)
$password = [Convert]::ToHexString($passwordBytes)

$key = [System.Security.Cryptography.RSA]::Create(2048)
try {
    $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
        "CN=$Subject", $key,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)

    $request.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
    $request.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor
            [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment, $true))

    $serverAuth = [System.Security.Cryptography.OidCollection]::new()
    $serverAuth.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1')) | Out-Null
    $request.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($serverAuth, $false))

    $sanBuilder = [System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
    $sanBuilder.AddDnsName($Subject)
    foreach ($name in $AlsoValidFor) {
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        $parsed = [System.Net.IPAddress]::Any
        if ([System.Net.IPAddress]::TryParse($name, [ref] $parsed)) { $sanBuilder.AddIpAddress($parsed) }
        else { $sanBuilder.AddDnsName($name) }
    }
    $request.CertificateExtensions.Add($sanBuilder.Build())

    $notBefore = [DateTimeOffset]::UtcNow.AddDays(-1)
    $notAfter  = [DateTimeOffset]::UtcNow.AddDays($ValidDays)
    $certificate = $request.CreateSelfSigned($notBefore, $notAfter)

    [System.IO.File]::WriteAllBytes(
        $OutputPath,
        $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $password))

    $fingerprint = $certificate.GetCertHashString([System.Security.Cryptography.HashAlgorithmName]::SHA256)
}
finally {
    $key.Dispose()
}

Write-Host ""
Write-Host "Certificate written to $OutputPath" -ForegroundColor Green
Write-Host "  subject      CN=$Subject"
Write-Host "  also valid   $($AlsoValidFor -join ', ')"
Write-Host "  expires      $($notAfter.UtcDateTime.ToString('yyyy-MM-dd'))"
Write-Host ""
Write-Host "SERVER — put these in .env (never commit them):" -ForegroundColor Yellow
Write-Host "  IRONFRONT_TLS_CERT_PATH=$OutputPath"
Write-Host "  IRONFRONT_TLS_CERT_PASSWORD=$password"
Write-Host ""
Write-Host "CLIENT — pin this fingerprint:" -ForegroundColor Yellow
Write-Host "  $fingerprint"
Write-Host ""
Write-Host "The password is printed once and is not recoverable. Re-run this script to" -ForegroundColor DarkGray
Write-Host "mint a new certificate if you lose it — and re-pin the client when you do." -ForegroundColor DarkGray
