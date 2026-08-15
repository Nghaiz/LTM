#nullable enable

using System;
using System.Security.Cryptography;
using System.Text;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Hashes a password before it leaves the machine. phase-03 trap 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OWNER: Dev A. Written by the lead's assist track
    /// (plans/assist-dev-a/step-06-master-connection.md).
    /// </para>
    /// <para>
    /// <b>This is not password storage, and it must not be mistaken for it.</b> What the master
    /// receives is treated as the secret and re-hashed with bcrypt before it reaches the
    /// database — <c>BCrypt.Net-Next</c> is referenced by <c>Ironfront.MasterServer</c> for
    /// exactly that. A single unsalted SHA-256 would be a poor choice at rest; here the job is
    /// only to keep the plaintext off the wire, so that a capture of the TCP stream, or a
    /// master-server log line that records more than it should, does not hand over a password
    /// the player has almost certainly reused somewhere else.
    /// </para>
    /// <para>
    /// <b>The username is the salt, per phase-03 trap 2's <c>SHA256(password + username)</c>.</b>
    /// It is a weak salt — it is public and guessable — but it is enough to stop one rainbow
    /// table covering every account at once, which is the whole ambition at this layer. It also
    /// means two players who pick the same password do not send the same bytes.
    /// </para>
    /// </remarks>
    public static class PasswordHasher
    {
        /// <summary>
        /// Returns <c>SHA256(password + username)</c> as lowercase hex.
        /// </summary>
        /// <remarks>
        /// The username is lowercased first. protocol-spec.md § 13 defines a username as
        /// 3–16 characters of <c>a-z0-9_</c>, so a client that upper-cased one on the login
        /// screen would otherwise compute a hash the account cannot match — a wrong-password
        /// error with a correct password, which is close to undebuggable from the player's
        /// side.
        /// </remarks>
        public static string Hash(string password, string username)
        {
            if (password == null) throw new ArgumentNullException(nameof(password));
            if (username == null) throw new ArgumentNullException(nameof(username));

            byte[] input = Encoding.UTF8.GetBytes(password + username.ToLowerInvariant());

            using (var sha = SHA256.Create())
            {
                return ToHex(sha.ComputeHash(input));
            }
        }

        /// <summary>
        /// Returns <c>SHA256(password)</c> as lowercase hex, for a room password.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Unsalted, and it has to be.</b> The master bcrypt-verifies a room password against
        /// what was stored when the room was created (<c>LobbyService.CanJoinRoom</c>), so the
        /// hash a joiner sends must equal the hash the creator sent. There is no value both
        /// sides hold at both moments to salt with: the room id does not exist when the room is
        /// created, and the name is neither unique nor fixed. Salting with either produces a
        /// wrong-room-password error for the correct password, which is the failure this whole
        /// file exists to avoid.
        /// </para>
        /// <para>
        /// That is a weaker hash than <see cref="Hash"/>, and it is aimed at a weaker secret: a
        /// room password is shared aloud among the people joining, is worth nothing outside that
        /// room, and is bcrypted by the master before it is stored. The job here is the same
        /// narrow one — keep the plaintext off the wire.
        /// </para>
        /// </remarks>
        public static string HashRoomPassword(string password)
        {
            if (password == null) throw new ArgumentNullException(nameof(password));

            using (var sha = SHA256.Create())
            {
                return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(password)));
            }
        }

        /// <summary>Lowercase hex, allocated once at the right size.</summary>
        private static string ToHex(byte[] bytes)
        {
            const string Digits = "0123456789abcdef";

            var chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                chars[i * 2] = Digits[bytes[i] >> 4];
                chars[i * 2 + 1] = Digits[bytes[i] & 0x0F];
            }

            return new string(chars);
        }
    }
}
