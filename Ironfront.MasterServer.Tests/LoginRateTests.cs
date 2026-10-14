using Ironfront.MasterServer.Auth;
using Ironfront.MasterServer.Data;
using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.MasterServer.Tests
{
    /// <summary>
    /// The per-address sign-in budget counts failures; a separate, larger cap counts every attempt.
    /// </summary>
    /// <remarks>
    /// On 2026-10-14 the game went public to a class behind one school address. The budget was 5
    /// attempts a minute per address, every attempt counted, so the class was told to wait from the
    /// sixth correct sign-in on. A guess is a failed attempt; a correct password is not one.
    /// </remarks>
    public sealed class LoginRateTests
    {
        private const string RightPassword = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private const string WrongPassword = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
        private const uint School = 0xC6336407; // 198.51.100.7, one address for the whole class

        [Fact]
        public void AClassBehindOneAddressCanAllSignIn()
        {
            using var database = new SqliteDatabase(":memory:");
            var auth = new AuthService(database);
            const int Pupils = 30;
            for (int i = 0; i < Pupils; i++) Assert.True(auth.Register($"pupil{i}", RightPassword, $"Pupil {i}").Ok);

            for (int i = 0; i < Pupils; i++)
                Assert.Equal(ErrorCode.Ok, auth.Login($"pupil{i}", RightPassword, School).ErrorCode);
        }

        [Fact]
        public void ATypoOrTwoDoesNotLockTheRestOfTheClassOut()
        {
            using var database = new SqliteDatabase(":memory:");
            var auth = new AuthService(database);
            for (int i = 0; i < 20; i++) Assert.True(auth.Register($"pupil{i}", RightPassword, $"Pupil {i}").Ok);

            for (int i = 0; i < 20; i++)
            {
                // Four of them mistype once, under the failure budget of five.
                if (i % 5 == 0) Assert.Equal(ErrorCode.WrongCredentials, auth.Login($"pupil{i}", WrongPassword, School).ErrorCode);
                Assert.Equal(ErrorCode.Ok, auth.Login($"pupil{i}", RightPassword, School).ErrorCode);
            }
        }

        [Fact]
        public void FiveWrongPasswordsStillCloseTheAddressEvenToTheRightOne()
        {
            using var database = new SqliteDatabase(":memory:");
            var auth = new AuthService(database);
            Assert.True(auth.Register("victim", RightPassword, "Victim").Ok);

            for (int attempt = 0; attempt < AuthService.DefaultRatePerMinute; attempt++)
                Assert.Equal(ErrorCode.WrongCredentials, auth.Login("victim", WrongPassword, School).ErrorCode);

            AuthResult refused = auth.Login("victim", RightPassword, School);
            Assert.Equal(ErrorCode.RateLimited, refused.ErrorCode);
            Assert.InRange(refused.RetryAfterSeconds, 50, 60);
        }

        [Fact]
        public void CorrectSignInsAreStillCappedPerAddress()
        {
            // Each sign-in runs bcrypt: a correct password in a loop must not be free either.
            using var database = new SqliteDatabase(":memory:");
            var auth = new AuthService(database, AuthService.DefaultRatePerMinute, attemptsPerMinute: 3);
            Assert.True(auth.Register("looper", RightPassword, "Looper").Ok);

            for (int attempt = 0; attempt < 3; attempt++)
                Assert.Equal(ErrorCode.Ok, auth.Login("looper", RightPassword, School).ErrorCode);
            Assert.Equal(ErrorCode.RateLimited, auth.Login("looper", RightPassword, School).ErrorCode);
        }
    }
}
