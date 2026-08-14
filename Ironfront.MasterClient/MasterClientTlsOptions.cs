using System;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Ironfront.MasterClient
{
    /// <summary>
    /// How the client validates the master server's certificate (phase 03 task 2,
    /// criterion 4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The interesting decision here is what to do about a <b>self-signed</b> certificate,
    /// which is what a VPS with an IP address and no domain can offer. The tempting answer is
    /// <c>(sender, cert, chain, errors) =&gt; true</c>, and it is the wrong one: it does not
    /// weaken validation, it removes it, so any machine on the path can present its own
    /// certificate and read and rewrite the whole session. Encrypted-to-the-attacker looks
    /// exactly like encrypted-to-the-server from the inside.
    /// </para>
    /// <para>
    /// <b>Pinning</b> is the answer instead: the client is built knowing one specific
    /// certificate's SHA-256 fingerprint and accepts that certificate and nothing else. It is
    /// stricter than the public CA path, not weaker — a mis-issued certificate from any CA on
    /// earth still fails. The cost is that rotating the certificate means shipping a new
    /// client, which for a 14-week capstone with a 365-day certificate is not a cost at all.
    /// </para>
    /// <para>
    /// <see cref="AllowAnyCertificate"/> exists for LAN development and is compiled out of a
    /// release build (<see cref="InsecureCertificatesPermittedByBuild"/>). Setting it in a
    /// release build does nothing, which is the property criterion 4 is asking about.
    /// </para>
    /// </remarks>
    public sealed class MasterClientTlsOptions
    {
        /// <summary>
        /// Whether the insecure escape hatch is honoured at all. <c>true</c> only in a DEBUG
        /// build. A release client cannot be talked into skipping validation by configuration,
        /// an environment variable, or a command-line flag, because the code that would do it
        /// is not in the binary.
        /// </summary>
        public const bool InsecureCertificatesPermittedByBuild =
#if DEBUG
            true;
#else
            false;
#endif

        /// <summary>Wrap the connection in TLS.</summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// The name presented for SNI and checked against the certificate. Defaults to the
        /// host passed to <c>ConnectAsync</c>.
        /// </summary>
        public string? TargetHost { get; set; }

        /// <summary>
        /// SHA-256 fingerprint, hex, of the one certificate this client accepts when the chain
        /// does not validate normally. Case and separators are ignored.
        /// </summary>
        public string? PinnedFingerprintSha256 { get; set; }

        /// <summary>
        /// Development only: accept any certificate. Ignored in a release build — see
        /// <see cref="InsecureCertificatesPermittedByBuild"/>.
        /// </summary>
        public bool AllowAnyCertificate { get; set; }

        /// <summary>
        /// The validation decision, factored out of the callback so it can be tested without a
        /// live handshake.
        /// </summary>
        /// <remarks>
        /// The order matters. A normally-valid chain passes first; only then does pinning get
        /// a say, so a client with a pin still works after the operator moves to a real
        /// Let's Encrypt certificate. The insecure branch is last and is reachable only in a
        /// DEBUG build.
        /// </remarks>
        internal static bool ValidateCertificate(
            X509Certificate? certificate,
            SslPolicyErrors errors,
            string? pinnedFingerprintSha256,
            bool allowAnyCertificate)
        {
            if (errors == SslPolicyErrors.None) return true;
            if (certificate is null) return false;

            if (!string.IsNullOrWhiteSpace(pinnedFingerprintSha256))
            {
                return FingerprintMatches(certificate, pinnedFingerprintSha256!);
            }

            return allowAnyCertificate && InsecureCertificatesPermittedByBuild;
        }

        private static bool FingerprintMatches(X509Certificate certificate, string expected)
        {
            string normalizedExpected = Normalize(expected);
            string actual = certificate.GetCertHashString(HashAlgorithmName.SHA256);

            if (normalizedExpected.Length != actual.Length) return false;

            // FixedTimeEquals on the raw bytes rather than string ==. A fingerprint check is
            // not obviously a timing target — the attacker already knows the certificate they
            // presented — but a constant-time compare here costs nothing and removes the need
            // for anyone to reason about it again later.
            if (!TryParseHex(normalizedExpected, out byte[] expectedBytes)) return false;
            if (!TryParseHex(actual, out byte[] actualBytes)) return false;

            return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }

        /// <summary>
        /// Hand-rolled because this assembly targets netstandard2.1 for Unity's sake, and
        /// <c>Convert.FromHexString</c> only exists from .NET 5.
        /// </summary>
        private static bool TryParseHex(string text, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (text.Length == 0 || (text.Length & 1) != 0) return false;

            var parsed = new byte[text.Length / 2];
            for (int i = 0; i < parsed.Length; i++)
            {
                if (!TryParseNibble(text[i * 2], out int high) ||
                    !TryParseNibble(text[(i * 2) + 1], out int low))
                {
                    // A pin that is not hex is a configuration mistake, and the safe reading
                    // of a configuration mistake in a security check is "does not match".
                    return false;
                }

                parsed[i] = (byte)((high << 4) | low);
            }

            bytes = parsed;
            return true;
        }

        private static bool TryParseNibble(char c, out int value)
        {
            if (c >= '0' && c <= '9') { value = c - '0'; return true; }
            if (c >= 'A' && c <= 'F') { value = c - 'A' + 10; return true; }
            if (c >= 'a' && c <= 'f') { value = c - 'a' + 10; return true; }
            value = 0;
            return false;
        }

        private static string Normalize(string fingerprint)
            => fingerprint.Replace(":", string.Empty, StringComparison.Ordinal)
                          .Replace(" ", string.Empty, StringComparison.Ordinal)
                          .Replace("-", string.Empty, StringComparison.Ordinal)
                          .ToUpperInvariant();
    }
}
