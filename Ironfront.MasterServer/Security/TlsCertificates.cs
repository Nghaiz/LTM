using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Ironfront.MasterServer.Security
{
    /// <summary>
    /// Loading and minting the X.509 certificate <see cref="Net.ClientConnection"/> presents
    /// when TLS is on (phase 03 task 2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// TLS arrives at M3 rather than M1 (D-AD-6) for one reason: the wire carries a password
    /// hash and a session token, and once the server leaves the LAN, anybody on the path can
    /// read both. Note the limit of client-side hashing while you are here — an eavesdropper
    /// who captures the SHA-256 hash can replay it, because to the server the hash *is* the
    /// password. Client hashing protects the user's original secret, which they reuse
    /// elsewhere. It does not protect this account. Only the transport can.
    /// </para>
    /// <para>
    /// <b>TLS does not replace framing.</b> <c>SslStream</c> is still a byte stream with no
    /// message boundaries: it decrypts records, not messages, and one <c>Read</c> can still
    /// return three glued frames or half of one. <c>MspFrameReader</c> is needed exactly as
    /// before — it simply reads from the <c>SslStream</c> instead of the socket.
    /// </para>
    /// </remarks>
    public static class TlsCertificates
    {
        /// <summary>Days a generated development certificate stays valid.</summary>
        public const int DevelopmentCertificateDays = 365;

        /// <summary>
        /// Loads a PKCS#12 bundle from disk.
        /// </summary>
        /// <remarks>
        /// Throws rather than returning <c>false</c>, and that is consistent with
        /// <c>MasterServerConfig</c> rather than an exception to conventions.md section 3.2:
        /// this runs once at startup on operator-supplied input, not on the packet path. A
        /// server that silently fell back to plaintext because the certificate path had a typo
        /// would be the worst possible outcome — everything would appear to work.
        /// </remarks>
        public static X509Certificate2 LoadPfx(string path, string? password)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Certificate path is required.", nameof(path));

            if (!File.Exists(path))
            {
                throw new InvalidOperationException(
                    $"TLS certificate '{path}' does not exist. Generate one with " +
                    "tools/new-dev-cert.ps1, or clear IRONFRONT_TLS_CERT_PATH to run without TLS.");
            }

            try
            {
                // Exportable so the same object can be re-serialised by the fingerprint tooling
                // and by tests; EphemeralKeySet is deliberately NOT used because it is
                // unsupported for server authentication on Windows.
                return new X509Certificate2(path, password, X509KeyStorageFlags.Exportable);
            }
            catch (CryptographicException ex)
            {
                throw new InvalidOperationException(
                    $"TLS certificate '{path}' could not be opened: {ex.Message}. " +
                    "A wrong IRONFRONT_TLS_CERT_PASSWORD is the usual cause.", ex);
            }
        }

        /// <summary>
        /// Mints a self-signed certificate for development and for an IP-only VPS, where
        /// Let's Encrypt cannot issue anything.
        /// </summary>
        /// <remarks>
        /// The export/re-import round trip is not decoration. On Windows a certificate
        /// produced by <see cref="CertificateRequest.CreateSelfSigned"/> carries a key handle
        /// that <c>SslStream</c> refuses to use for server authentication; going through a
        /// PKCS#12 blob rebinds it to a usable key. Skipping this yields a handshake that
        /// fails only on Windows, which is the kind of bug that gets diagnosed as "the client
        /// is broken".
        /// </remarks>
        public static X509Certificate2 CreateSelfSigned(string subjectName, params string[] alternativeNames)
        {
            if (string.IsNullOrWhiteSpace(subjectName))
                throw new ArgumentException("Subject name is required.", nameof(subjectName));

            using var key = RSA.Create(2048);
            var request = new CertificateRequest(
                $"CN={subjectName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, critical: true));
            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, critical: false));   // serverAuth

            var subjectAlternatives = new SubjectAlternativeNameBuilder();
            subjectAlternatives.AddDnsName(subjectName);
            foreach (string name in alternativeNames)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (System.Net.IPAddress.TryParse(name, out System.Net.IPAddress? ip))
                    subjectAlternatives.AddIpAddress(ip);
                else
                    subjectAlternatives.AddDnsName(name);
            }
            request.CertificateExtensions.Add(subjectAlternatives.Build());

            DateTimeOffset now = DateTimeOffset.UtcNow;
            using X509Certificate2 unusable = request.CreateSelfSigned(
                now.AddDays(-1), now.AddDays(DevelopmentCertificateDays));

            // A random transport password: the blob never leaves this method, and a constant
            // here would be a constant somebody eventually copies into a deployment script.
            Span<byte> passwordBytes = stackalloc byte[24];
            RandomNumberGenerator.Fill(passwordBytes);
            string transportPassword = Convert.ToHexString(passwordBytes);

            return new X509Certificate2(
                unusable.Export(X509ContentType.Pfx, transportPassword),
                transportPassword,
                X509KeyStorageFlags.Exportable);
        }

        /// <summary>
        /// The SHA-256 fingerprint, uppercase hex, as the client pins it.
        /// </summary>
        /// <remarks>
        /// SHA-256 and not SHA-1: <c>X509Certificate.GetCertHashString()</c> with no argument
        /// still defaults to SHA-1, which is collision-broken, and a pin is exactly the place
        /// where a collision matters.
        /// </remarks>
        public static string FingerprintSha256(X509Certificate certificate)
        {
            if (certificate is null) throw new ArgumentNullException(nameof(certificate));
            return certificate.GetCertHashString(HashAlgorithmName.SHA256);
        }
    }
}
