using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Whether this client's player is typing into a text field right now, and so whether the
    /// keyboard belongs to the game or to the text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a static here rather than a property on the chat component.</b>
    /// <c>ClientChatSender</c> already exposes <c>IsComposing</c>, and it had zero consumers
    /// across the whole repository — because it structurally could not have one. It lives in
    /// <c>Ironfront.Net.Unity.Client</c>, whose asmdef sets <c>autoReferenced: false</c>, so
    /// <c>Assembly-CSharp</c> cannot name it; and <see cref="MovementSimulation"/> and
    /// <c>LocalInputSource</c> are in assemblies that do not reference the client one either.
    /// The three readers that must suppress input span all three of those assemblies, and this
    /// one — <c>Ironfront.Net.Unity.Shared</c>, which references nothing and is
    /// <c>autoReferenced</c> — is the only place every one of them can see. A property nobody
    /// can reach is not a seam, and that is exactly how Enter came to mean two things at once.
    /// </para>
    /// <para>
    /// <b>One writer, several readers.</b> <c>ClientChatSender</c> writes it; the input sources
    /// read it. It is deliberately not a counter or a stack: a second text field would be a
    /// second thing to keep balanced, and the one that leaked would eat the player's movement
    /// keys with nothing on screen to explain why.
    /// </para>
    /// <para>
    /// <b>False on a server.</b> A dedicated server has no chat box, so nothing ever sets it
    /// and every read is false — which is what the suppression terms want, and why they need no
    /// role branch.
    /// </para>
    /// </remarks>
    public static class LocalTextEntry
    {
        /// <summary>
        /// True while a text field owns the keyboard. Gameplay input is suppressed for as long
        /// as it is set.
        /// </summary>
        public static bool Composing { get; set; }

        /// <summary>
        /// Clears the flag at subsystem registration, for <see cref="NetContext.ResetOnLoad"/>'s
        /// reason: with domain reload disabled a static survives from one Play session into the
        /// next, and a flag left true would start the next session with the player unable to
        /// move and no chat box on screen to close.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            Composing = false;
        }
    }
}
