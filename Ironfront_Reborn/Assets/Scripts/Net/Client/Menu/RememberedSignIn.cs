#nullable enable

using UnityEngine;

namespace Ironfront.Net.Unity.Client.Menu
{
    /// <summary>
    /// What "Remember me" keeps on this machine: the username, and the master's token that signs
    /// it in without the password (owner's list of 2026-10-09, item 1; protocol 14.0.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Never the password.</b> The token expires after thirty days, is replaced every time it is
    /// used, and the master keeps only its hash; a password or the hash the client sends in its
    /// place would be a permanent key in a settings file.
    /// </para>
    /// <para>
    /// The username key is the one the sign-in screen has always used, so a player who ticked the
    /// box before this change keeps their name in the field.
    /// </para>
    /// </remarks>
    public static class RememberedSignIn
    {
        internal const string UsernameKey = "ironfront.menu.remembered-username";
        internal const string TokenKey = "ironfront.menu.remembered-token";

        /// <summary>The remembered username, or empty.</summary>
        public static string Username => PlayerPrefs.GetString(UsernameKey, string.Empty);

        /// <summary>The token to sign in with, or empty when this machine has none.</summary>
        public static string Token => PlayerPrefs.GetString(TokenKey, string.Empty);

        /// <summary>Whether opening Multiplayer should sign in by itself.</summary>
        public static bool CanSignInAutomatically => Token.Length > 0;

        /// <summary>Keeps <paramref name="username"/> and, when there is one, the master's <paramref name="token"/>.</summary>
        public static void Remember(string username, string token)
        {
            if (!string.IsNullOrWhiteSpace(username)) PlayerPrefs.SetString(UsernameKey, username.Trim());
            if (!string.IsNullOrEmpty(token)) PlayerPrefs.SetString(TokenKey, token);
            else PlayerPrefs.DeleteKey(TokenKey);
            PlayerPrefs.Save();
        }

        /// <summary>Replaces a spent token with the one its sign-in returned.</summary>
        public static void ReplaceToken(string token)
        {
            if (string.IsNullOrEmpty(token)) PlayerPrefs.DeleteKey(TokenKey);
            else PlayerPrefs.SetString(TokenKey, token);
            PlayerPrefs.Save();
        }

        /// <summary>Stops signing in automatically; the username stays in the field.</summary>
        public static void ForgetToken()
        {
            PlayerPrefs.DeleteKey(TokenKey);
            PlayerPrefs.Save();
        }

        /// <summary>Forgets everything: "Remember me" was unticked.</summary>
        public static void Forget()
        {
            PlayerPrefs.DeleteKey(UsernameKey);
            PlayerPrefs.DeleteKey(TokenKey);
            PlayerPrefs.Save();
        }
    }
}
