using Ironfront.MasterServer.Auth;
using Ironfront.MasterServer.Data;
using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.MasterServer.Tests
{
    /// <summary>
    /// The register form's display-name field, which is labelled "(optional)" and was not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect these pin, found by playing the game on 2026-09-03.</b> Account creation
    /// failed one hundred per cent of the time, and said "Wrong username or password." — a
    /// sentence about credentials, on a screen where there are no credentials to be wrong yet.
    /// <c>AuthService.Register</c> rejected a blank display name and reported that rejection
    /// with <see cref="ErrorCode.WrongCredentials"/>, while the Canvas labels the field
    /// <c>"Display name (optional)"</c> (<c>BuildMenuCanvas.cs:273</c>) and
    /// <c>MenuRegisterScreen</c>'s own docstring promises "Left blank, the master applies its
    /// own rule." There was no such rule. Two correct-looking halves, disagreeing.
    /// </para>
    /// <para>
    /// <b>Why 2,103 green tests did not catch it.</b> Every existing caller of
    /// <c>Register</c> — <c>PhaseOneTwoServiceTests</c>, the room harnesses,
    /// <c>Ironfront.Tools.E2E</c> — passes a non-empty display name, because whoever wrote them
    /// knew what the field was for. The untested input was the one every real player uses
    /// first: nothing. A test suite can only be wrong about the inputs it never sends.
    /// </para>
    /// </remarks>
    public sealed class RegisterDisplayNameTests
    {
        // A valid SHA-256 of something; AuthService only checks the shape.
        private const string PasswordHash =
            "5e884898da28047151d0e56f8dc6292773603d0d6aabbdd62a11ef721d1542d8";

        private static SqliteDatabase CreateDatabase() => new SqliteDatabase(":memory:");

        [Fact]
        public void ABlankDisplayNameIsAcceptedAndBecomesTheUsername()
        {
            using var database = CreateDatabase();
            var auth = new AuthService(database);

            RegisterResult result = auth.Register("playerone", PasswordHash, string.Empty);

            Assert.True(result.Ok);
            Assert.Equal(ErrorCode.Ok, result.ErrorCode);

            // The name is the point, not merely the acceptance: a blank that registered but left
            // the player nameless in the lobby would pass an Ok-only assertion and still be the
            // bug one screen later.
            AuthResult login = auth.Login("playerone", PasswordHash, 0x7f000001);
            Assert.True(login.Ok);
            Assert.Equal("playerone", login.Session!.DisplayName);
        }

        [Theory]
        [InlineData("   ")]
        [InlineData("\t")]
        public void WhitespaceCountsAsBlankRatherThanAsAName(string displayName)
        {
            using var database = CreateDatabase();
            var auth = new AuthService(database);

            Assert.True(auth.Register("playerone", PasswordHash, displayName).Ok);

            AuthResult login = auth.Login("playerone", PasswordHash, 0x7f000001);
            Assert.Equal("playerone", login.Session!.DisplayName);
        }

        [Fact]
        public void AnOverlongDisplayNameSaysSoRatherThanBlamingTheCredentials()
        {
            using var database = CreateDatabase();
            var auth = new AuthService(database);

            RegisterResult result = auth.Register("playerone", PasswordHash, new string('x', 33));

            Assert.False(result.Ok);

            // The whole finding in one assertion. WrongCredentials sends the player back to
            // re-type a password that was never the problem; InvalidDisplayName names the field
            // they can actually fix. Same reasoning the TeamsWouldUnbalance remark records.
            Assert.Equal(ErrorCode.InvalidDisplayName, result.ErrorCode);
        }

        [Fact]
        public void AMalformedPasswordHashIsStillACredentialProblem()
        {
            using var database = CreateDatabase();
            var auth = new AuthService(database);

            // The guard against over-correcting: the display-name split must not swallow the
            // case WrongCredentials genuinely describes. A client that sent a raw password
            // instead of a SHA-256 is wrong about the credential, and should hear so.
            RegisterResult result = auth.Register("playerone", "hunter2", "Player One");

            Assert.False(result.Ok);
            Assert.Equal(ErrorCode.WrongCredentials, result.ErrorCode);
        }
    }
}
