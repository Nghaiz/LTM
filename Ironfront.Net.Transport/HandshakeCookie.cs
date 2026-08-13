using System;
using System.Security.Cryptography;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Transport
{
    /// <summary>
    /// Derives the server salt for a handshake from the client's address instead of
    /// remembering it. protocol-spec.md section 3.1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The problem this replaces.</b> The server used to answer CONNECT_REQUEST by storing a
    /// <c>PendingChallenge</c> — a record, a cloned endpoint and a CSPRNG draw — keyed on the
    /// source address. Nothing about a UDP source address is trustworthy before a round trip has
    /// completed, so a flood of spoofed sources filled that table to its 2048-entry cap and the
    /// eviction policy then started throwing out <i>legitimate</i> clients mid-handshake. Those
    /// clients were unrecoverable: a connection already in the Challenged state refuses a second
    /// challenge, so they had to time out and start over. protocol-spec.md section 3.1 states the
    /// rule this violated in as many words — "the server allocates no resources".
    /// </para>
    /// <para>
    /// <b>The replacement is the standard SYN-cookie construction.</b> The server salt is a
    /// keyed hash of the things that identify this handshake attempt, so it can be recomputed on
    /// demand and never stored:
    /// </para>
    /// <code>
    /// serverSalt = HMAC-SHA256(secretKey, address ‖ port ‖ clientSalt ‖ epoch)[0..8]
    /// </code>
    /// <para>
    /// A spoofed CONNECT_REQUEST now costs the server one HMAC and one datagram, and the reply
    /// goes to the forged address rather than to the attacker — which is the whole point: the
    /// client can only produce a correct CONNECT_RESPONSE if it actually received the challenge,
    /// which proves it holds the address it claims.
    /// </para>
    /// <para>
    /// <b>The epoch is what bounds replay.</b> The key alone would make a cookie valid forever.
    /// Mixing in a coarse time bucket means a captured cookie stops verifying once the bucket
    /// rolls over, and accepting the previous bucket as well stops a handshake that straddles a
    /// boundary from failing for no reason. That is a <see cref="EpochSeconds"/>-to-
    /// <c>2 × EpochSeconds</c> window, which is far longer than a handshake and far shorter than
    /// a session.
    /// </para>
    /// </remarks>
    public sealed class HandshakeCookie : IDisposable
    {
        /// <summary>
        /// Seconds per epoch bucket. A cookie is valid for between one and two of these.
        /// </summary>
        /// <remarks>
        /// 30 s against a handshake that completes in one round trip. Short enough that a
        /// captured cookie is useless almost immediately; long enough that nothing legitimate
        /// ever races the rollover, because the previous bucket is accepted too.
        /// </remarks>
        public const int EpochSeconds = 30;

        private readonly HMACSHA256 _hmac;
        private bool _disposed;

        /// <summary>Generates a random key. The key never leaves this process.</summary>
        public HandshakeCookie()
            : this(NewKey())
        {
        }

        private static byte[] NewKey()
        {
            var key = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(key);
            return key;
        }

        /// <param name="key">Fixed key, for tests that need a reproducible cookie.</param>
        public HandshakeCookie(byte[] key)
        {
            if (key is null) throw new ArgumentNullException(nameof(key));
            _hmac = new HMACSHA256(key);
        }

        /// <summary>The server salt for this handshake attempt, in the current epoch.</summary>
        public ulong Derive(uint address, ushort port, ulong clientSalt, double nowMs)
            => Derive(address, port, clientSalt, EpochOf(nowMs));

        /// <summary>
        /// Whether <paramref name="challengeResponse"/> answers a challenge this server would
        /// have issued to this address.
        /// </summary>
        /// <remarks>
        /// Checks the current epoch and the one before it, so a handshake that crosses a bucket
        /// boundary still completes. Both comparisons are ordinary equality rather than
        /// <see cref="CryptographicOperations.FixedTimeEquals"/>: the attacker-controlled value
        /// is being compared against a derived one, and learning the salt by timing the compare
        /// would still require the key to forge a cookie for any OTHER address. The ticket HMAC,
        /// where the secret really is on the line, does use the constant-time compare.
        /// </remarks>
        public bool Verify(
            uint address, ushort port, ulong clientSalt, ulong challengeResponse, double nowMs)
        {
            long epoch = EpochOf(nowMs);

            for (long candidate = epoch; candidate >= epoch - 1; candidate--)
            {
                ulong serverSalt = Derive(address, port, clientSalt, candidate);
                if (challengeResponse == (clientSalt ^ serverSalt)) return true;
            }

            return false;
        }

        /// <summary>The server salt this address would have been challenged with in an epoch.</summary>
        public ulong Derive(uint address, ushort port, ulong clientSalt, long epoch)
        {
            ThrowIfDisposed();

            Span<byte> input = stackalloc byte[4 + 2 + 8 + 8];
            Endian.WriteU32BE(input, 0, address);
            Endian.WriteU16LE(input, 4, port);
            Endian.WriteU64LE(input, 6, clientSalt);
            Endian.WriteU64LE(input, 14, unchecked((ulong)epoch));

            Span<byte> mac = stackalloc byte[32];
            lock (_hmac)
            {
                if (!_hmac.TryComputeHash(input, mac, out _))
                    throw new InvalidOperationException("HMAC failed");
            }

            // A salt of 0 would make the XOR challenge degenerate to "echo your own salt back",
            // which any observer could answer. Vanishingly unlikely; cheap to exclude.
            ulong salt = Endian.ReadU64LE(mac, 0);
            return salt == 0 ? 1UL : salt;
        }

        private static long EpochOf(double nowMs) => (long)(nowMs / (EpochSeconds * 1000.0));

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _hmac.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HandshakeCookie));
        }
    }
}
