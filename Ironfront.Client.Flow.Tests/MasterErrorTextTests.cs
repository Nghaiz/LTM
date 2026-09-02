using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Unity.Client;
using Xunit;

namespace Ironfront.Client.Flow.Tests
{
    /// <summary>
    /// phase-03 handoff item 2: the error codes the client surfaces, cross-checked against
    /// protocol-spec.md § 13.
    /// </summary>
    public sealed class MasterErrorTextTests
    {
        [Fact]
        public void EveryCodeInTheSpecHasItsOwnSentence()
        {
            // The check that keeps this honest: a code added to the shared enum and forgotten
            // here falls through to the default branch, and this notices.
            var seen = new Dictionary<string, ErrorCode>();

            foreach (ErrorCode code in (ErrorCode[])Enum.GetValues(typeof(ErrorCode)))
            {
                string text = MasterErrorText.Describe(code);

                Assert.False(string.IsNullOrWhiteSpace(text));
                Assert.DoesNotContain("code " + (int)code, text);   // not the default branch

                if (code == ErrorCode.Ok) continue;

                Assert.False(
                    seen.ContainsKey(text),
                    $"{code} and {(seen.TryGetValue(text, out ErrorCode other) ? other : default)} share a message");
                seen[text] = code;
            }
        }

        [Fact]
        public void AWrongPasswordSaysSoInWordsAPlayerCanActOn()
        {
            // phase-03 criterion 7.
            Assert.Equal("Wrong username or password.", MasterErrorText.Describe(ErrorCode.WrongCredentials));
        }

        [Fact]
        public void TheIntOverloadAgreesWithTheEnumOne()
        {
            // The wire carries an int; MasterClient's results expose it as one.
            Assert.Equal(
                MasterErrorText.Describe(ErrorCode.RoomFull),
                MasterErrorText.Describe((int)ErrorCode.RoomFull));
        }

        [Fact]
        public void AnUnknownCodeStaysReportable()
        {
            // A newer master than this build knows. A number the player can read out beats
            // "something went wrong" when the alternative is a support conversation.
            string text = MasterErrorText.Describe(4242);

            Assert.Contains("4242", text);
        }

        /// <summary>
        /// A lockout must not send the player to change a password that was right.
        /// </summary>
        /// <remarks>
        /// The whole cost of collapsing four refusals into <c>WrongCredentials</c> landed here:
        /// the message told a locked-out player their password was wrong, so they changed it,
        /// and the lock — which a password change does not clear — refused them for the rest of
        /// the fifteen minutes. This asserts the two things the message must not do.
        /// </remarks>
        [Fact]
        public void ALockoutDoesNotBlameThePassword()
        {
            string text = MasterErrorText.Describe(ErrorCode.AccountLocked);

            Assert.Contains("locked", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Wrong username or password", text);
        }

        /// <summary>
        /// The rate-limit message must not promise a wait shorter than the window.
        /// </summary>
        /// <remarks>
        /// It said "a few seconds" against sixty. A player who followed it failed again and read
        /// the same sentence, which is a loop rather than an instruction. It must also say
        /// <i>network</i>, because the budget is per source address and two people behind one
        /// router share it — "you" is simply false for the second of them.
        /// </remarks>
        [Fact]
        public void TheLoginRateLimitNamesItsRealWindowAndItsRealScope()
        {
            string text = MasterErrorText.Describe(ErrorCode.RateLimited);

            Assert.Contains("minute", text);
            Assert.Contains("network", text);
            Assert.DoesNotContain("few seconds", text);
        }

        /// <summary>
        /// Given the master's own number, the message states it instead of an adjective.
        /// </summary>
        [Fact]
        public void AKnownWaitIsRenderedAsTheNumber()
        {
            Assert.Contains(
                "47 seconds",
                MasterErrorText.DescribeFailure((int)ErrorCode.RateLimited, 47));

            Assert.Contains(
                "15 minutes",
                MasterErrorText.DescribeFailure((int)ErrorCode.AccountLocked, 15 * 60));

            // Zero is a master too old to send the field, not an invitation to retry now.
            Assert.Equal(
                MasterErrorText.Describe(ErrorCode.RateLimited),
                MasterErrorText.DescribeFailure((int)ErrorCode.RateLimited, 0));
        }

        /// <summary>
        /// An over-long chat line must not be described as sending too often.
        /// </summary>
        /// <remarks>
        /// Both arrived as <c>RateLimited</c>, so the advice was to wait — and the same text was
        /// still too long afterwards. The message carries the limit, because "shorter" is not an
        /// instruction a player can follow without a target.
        /// </remarks>
        [Fact]
        public void AnOverLongChatLineSaysHowLongIsAllowed()
        {
            string text = MasterErrorText.Describe(ErrorCode.ChatMessageTooLong);

            Assert.Contains(MspChatLimits.MaxTextCharacters.ToString(), text);
            Assert.DoesNotContain("too quickly", text);
        }

        [Fact]
        public void TheUsernameRuleInTheMessageMatchesTheOneTheServerEnforces()
        {
            // protocol-spec.md § 13: length 3-16, only a-z0-9_. A message that describes a
            // different rule sends the player round in circles.
            string text = MasterErrorText.Describe(ErrorCode.InvalidUsername);

            Assert.Contains("3-16", text);
            Assert.Contains("a-z", text);
            Assert.Contains("underscore", text);
        }
    }

    /// <summary>phase-03 trap 2: the password must be hashed before it leaves the machine.</summary>
    public sealed class PasswordHasherTests
    {
        [Fact]
        public void TheHashIsSixtyFourLowercaseHexCharacters()
        {
            // AuthService.IsValidSha256 rejects anything else outright, before the password is
            // even compared — so the wrong shape reads as a wrong password.
            string hash = PasswordHasher.Hash("hunter2", "tester");

            Assert.Equal(64, hash.Length);
            foreach (char c in hash)
                Assert.True((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'), $"'{c}' is not lowercase hex");
        }

        [Fact]
        public void TheUsernameSaltsIt()
        {
            Assert.NotEqual(
                PasswordHasher.Hash("hunter2", "alice"),
                PasswordHasher.Hash("hunter2", "bob"));
        }

        [Fact]
        public void TheUsernameCaseDoesNotChangeTheHash()
        {
            // Usernames are a-z on the server. A player who capitalises theirs on the login
            // screen would otherwise get a wrong-password error with the correct password.
            Assert.Equal(
                PasswordHasher.Hash("hunter2", "tester"),
                PasswordHasher.Hash("hunter2", "TeStEr"));
        }

        [Fact]
        public void ThePasswordCaseDoesChangeIt()
        {
            Assert.NotEqual(
                PasswordHasher.Hash("hunter2", "tester"),
                PasswordHasher.Hash("HUNTER2", "tester"));
        }

        [Fact]
        public void ARoomPasswordIsUnsaltedSoBothSidesComputeTheSameThing()
        {
            // The creator hashes it before the room has an id; the joiner hashes it after.
            Assert.Equal(
                PasswordHasher.HashRoomPassword("letmein"),
                PasswordHasher.HashRoomPassword("letmein"));

            Assert.NotEqual(
                PasswordHasher.HashRoomPassword("letmein"),
                PasswordHasher.Hash("letmein", "tester"));
        }

        [Fact]
        public void ARoomPasswordHashIsTheRightShapeToo()
        {
            string hash = PasswordHasher.HashRoomPassword("letmein");

            Assert.Equal(64, hash.Length);
        }

        [Fact]
        public void NullsAreRejectedRatherThanHashed()
        {
            Assert.Throws<ArgumentNullException>(() => PasswordHasher.Hash(null!, "tester"));
            Assert.Throws<ArgumentNullException>(() => PasswordHasher.Hash("hunter2", null!));
            Assert.Throws<ArgumentNullException>(() => PasswordHasher.HashRoomPassword(null!));
        }

        [Fact]
        public void ItMatchesAKnownSha256Vector()
        {
            // "abc" -> the canonical SHA-256 test vector. Proves the hex encoding, not just
            // that two calls agree with each other.
            Assert.Equal(
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                PasswordHasher.HashRoomPassword("abc"));
        }
    }
}
