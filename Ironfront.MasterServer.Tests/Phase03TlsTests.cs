using System;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Ironfront.MasterClient;
using Ironfront.MasterServer.Security;
using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.MasterServer.Tests
{
    /// <summary>
    /// Phase 03 criteria 2, 3 and 4: TLS works, framing still works over it, and a release
    /// client does not skip certificate validation.
    /// </summary>
    public sealed class Phase03TlsTests
    {
        private const string PasswordHash =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        [Fact]
        public async Task AClientThatPinsTheCertificateCompletesTheHandshakeAndLogsIn()
        {
            await using var server = new Phase03ServerHarness(tls: true);
            using var client = new MasterClient.MasterClient();

            await client.ConnectAsync("127.0.0.1", server.Port, new MasterClientTlsOptions
            {
                Enabled                 = true,
                TargetHost              = "localhost",
                PinnedFingerprintSha256 = server.CertificateFingerprint,
            });

            Assert.True(client.IsTls);
            Assert.True(server.Host.TlsEnabled);

            RegisterResult registered = await PumpAsync(
                client.RegisterAsync("tlsuser", PasswordHash, "TLS User"), client);
            Assert.True(registered.Ok);

            LoginResult login = await PumpAsync(client.LoginAsync("tlsuser", PasswordHash), client);
            Assert.True(login.Ok);
            Assert.NotEqual(string.Empty, login.SessionToken);
        }

        /// <summary>
        /// Criterion 3, and the point the report makes about TLS: <b>encryption is not
        /// framing</b>.
        /// </summary>
        /// <remarks>
        /// The two hard cases from phase 00 are replayed through <c>SslStream</c> — three
        /// requests written in one call, and one request written a byte at a time. If TLS had
        /// somehow supplied message boundaries, both would work with a naive one-read-one-
        /// message parser. They do not; the reader is what makes them work, exactly as before.
        /// </remarks>
        [Fact]
        public async Task FramingSurvivesGluedAndSplitWritesOverSslStream()
        {
            await using var server = new Phase03ServerHarness(tls: true);
            using var socket = new TcpClient();
            await socket.ConnectAsync("127.0.0.1", server.Port);

            using var ssl = new SslStream(
                socket.GetStream(),
                leaveInnerStreamOpen: false,
                (_, certificate, _, errors) => MasterClientTlsOptions.ValidateCertificate(
                    certificate, errors, server.CertificateFingerprint, false));

            await ssl.AuthenticateAsClientAsync("localhost", null, SslProtocols.None, false);

            // Three ROOM_LIST_REQ frames in ONE write. Unauthenticated, so each earns an
            // ERROR_PUSH — three of them, which is what proves all three frames were seen.
            byte[] one = Frame(MspMessageType.RoomListRequest, "{}");
            var glued = new byte[one.Length * 3];
            Buffer.BlockCopy(one, 0, glued, 0, one.Length);
            Buffer.BlockCopy(one, 0, glued, one.Length, one.Length);
            Buffer.BlockCopy(one, 0, glued, one.Length * 2, one.Length);
            await ssl.WriteAsync(glued);
            await ssl.FlushAsync();

            Assert.True(
                await MasterHostHarness.WaitUntilAsync(() => server.Dispatcher.Errors.Total >= 3),
                $"expected 3 errors from 3 glued frames, saw {server.Dispatcher.Errors.Total}");

            long before = server.Dispatcher.Errors.Total;

            // One frame, one byte per write. The reader has to hold 5 partial states and
            // produce exactly one frame at the end.
            for (int i = 0; i < one.Length; i++)
            {
                await ssl.WriteAsync(one.AsMemory(i, 1));
                await ssl.FlushAsync();
            }

            Assert.True(
                await MasterHostHarness.WaitUntilAsync(() => server.Dispatcher.Errors.Total >= before + 1),
                "a frame split across single-byte TLS writes was not reassembled");
        }

        [Fact]
        public async Task APlaintextClientAgainstATlsServerIsCountedAsAHandshakeFailure()
        {
            await using var server = new Phase03ServerHarness(tls: true);
            using var socket = new TcpClient();
            await socket.ConnectAsync("127.0.0.1", server.Port);

            // Valid MSP, invalid TLS. The server cannot read it as a ClientHello, which is
            // exactly what a client that forgot --tls looks like.
            byte[] frame = Frame(MspMessageType.RoomListRequest, "{}");
            await socket.GetStream().WriteAsync(frame);
            await socket.GetStream().FlushAsync();

            Assert.True(
                await MasterHostHarness.WaitUntilAsync(() => server.Host.TotalTlsHandshakeFailures >= 1),
                "a plaintext connection to a TLS listener should be recorded as a handshake failure");
        }

        [Fact]
        public async Task AClientPinningTheWrongFingerprintIsRefused()
        {
            await using var server = new Phase03ServerHarness(tls: true);
            using var client = new MasterClient.MasterClient();

            // A syntactically valid SHA-256 that is not this server's.
            string wrongPin = new string('A', 64);

            await Assert.ThrowsAnyAsync<AuthenticationException>(() =>
                client.ConnectAsync("127.0.0.1", server.Port, new MasterClientTlsOptions
                {
                    Enabled                 = true,
                    TargetHost              = "localhost",
                    PinnedFingerprintSha256 = wrongPin,
                }));
        }

        /// <summary>
        /// Criterion 4. The insecure branch is not a runtime setting — it is compiled out, so
        /// a release client cannot be configured into skipping validation at all.
        /// </summary>
        [Fact]
        public void AReleaseBuildCannotBeTalkedIntoAcceptingAnUnvalidatedCertificate()
        {
#if DEBUG
            Assert.True(MasterClientTlsOptions.InsecureCertificatesPermittedByBuild);
#else
            Assert.False(MasterClientTlsOptions.InsecureCertificatesPermittedByBuild);
#endif

            using X509Certificate2 certificate = TlsCertificates.CreateSelfSigned("localhost");

            // No pin, AllowAnyCertificate set: honoured only in DEBUG, and CI builds Release.
            Assert.Equal(
                MasterClientTlsOptions.InsecureCertificatesPermittedByBuild,
                MasterClientTlsOptions.ValidateCertificate(
                    certificate, SslPolicyErrors.RemoteCertificateChainErrors, null, true));

            // No pin, nothing allowed: refused in every build.
            Assert.False(MasterClientTlsOptions.ValidateCertificate(
                certificate, SslPolicyErrors.RemoteCertificateChainErrors, null, false));
        }

        [Fact]
        public void PinningAcceptsTheMatchingCertificateAndRejectsEverythingElse()
        {
            using X509Certificate2 mine = TlsCertificates.CreateSelfSigned("localhost");
            using X509Certificate2 theirs = TlsCertificates.CreateSelfSigned("localhost");

            string pin = TlsCertificates.FingerprintSha256(mine);

            Assert.True(MasterClientTlsOptions.ValidateCertificate(
                mine, SslPolicyErrors.RemoteCertificateChainErrors, pin, false));

            // A second self-signed certificate for the SAME name. Name-based validation would
            // accept it; a fingerprint pin is what makes it fail, which is the whole reason to
            // pin rather than to "ignore chain errors for localhost".
            Assert.False(MasterClientTlsOptions.ValidateCertificate(
                theirs, SslPolicyErrors.RemoteCertificateChainErrors, pin, false));

            // Colon-separated and lowercase are both what a human copies out of a tool.
            string decorated = string.Join(":", Chunk(pin.ToLowerInvariant()));
            Assert.True(MasterClientTlsOptions.ValidateCertificate(
                mine, SslPolicyErrors.RemoteCertificateChainErrors, decorated, false));

            // A pin that is not hex at all is a configuration error, and the safe reading of
            // one in a security check is "no match".
            Assert.False(MasterClientTlsOptions.ValidateCertificate(
                mine, SslPolicyErrors.RemoteCertificateChainErrors, "not-a-fingerprint", false));
        }

        [Fact]
        public void AValidChainIsAcceptedWithoutAPin()
        {
            using X509Certificate2 certificate = TlsCertificates.CreateSelfSigned("localhost");

            // SslPolicyErrors.None means the OS already validated it — a client carrying a
            // stale pin must not start failing the day the operator switches to Let's Encrypt.
            Assert.True(MasterClientTlsOptions.ValidateCertificate(
                certificate, SslPolicyErrors.None, new string('B', 64), false));
        }

        [Fact]
        public void ASelfSignedCertificateCarriesAUsablePrivateKeyAndASha256Fingerprint()
        {
            using X509Certificate2 certificate = TlsCertificates.CreateSelfSigned("localhost", "127.0.0.1");

            // Without the PKCS#12 round trip inside CreateSelfSigned this is false on Windows
            // and the handshake fails with an error that names neither the cause nor the fix.
            Assert.True(certificate.HasPrivateKey);

            string fingerprint = TlsCertificates.FingerprintSha256(certificate);
            Assert.Equal(64, fingerprint.Length);          // SHA-256, not the SHA-1 default
            Assert.True(certificate.NotAfter > DateTime.Now);
        }

        private static string[] Chunk(string text)
        {
            var parts = new string[text.Length / 2];
            for (int i = 0; i < parts.Length; i++) parts[i] = text.Substring(i * 2, 2);
            return parts;
        }

        private static byte[] Frame(MspMessageType type, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            var frame = new byte[MspFrame.FrameSizeFor(body.Length)];
            MspFrame.Write(frame, type, body);
            return frame;
        }

        private static async Task<T> PumpAsync<T>(Task<T> task, MasterClient.MasterClient client)
        {
            while (!task.IsCompleted)
            {
                client.Poll();
                await Task.Delay(5);
            }

            client.Poll();
            return await task;
        }
    }
}
