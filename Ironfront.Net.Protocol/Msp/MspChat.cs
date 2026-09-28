namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// What the <c>channel</c> byte on <c>MSP_CHAT_SEND</c> / <c>MSP_CHAT_PUSH</c> means.
    /// protocol-spec.md section 11.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>These values existed on the wire before anything said what they were, and the gap cost
    /// the room its privacy.</b> The master had two branches keyed on the byte —
    /// <c>MspMessageDispatcher.SendChat</c> broadcast channel 0 to every connection it held and
    /// routed anything else to the sender's room — while the Unity lobby sent a
    /// <c>ChatChannel = 0</c> whose own remark read "zero, which is what the master echoes back
    /// untouched". Both halves were self-consistent and they disagreed, so every line typed into
    /// a room-lobby chat box went to every player logged into the master. The spec named the
    /// field and never named its values, so neither side was reading it wrong.
    /// </para>
    /// <para>
    /// <b>Shared, not duplicated.</b> Both ends already compile against this assembly, so a
    /// channel renumbered here breaks the client and the master in the same build rather than
    /// letting them drift apart again.
    /// </para>
    /// </remarks>
    public static class MspChatChannel
    {
        /// <summary>Every player logged into the master, whatever room they are in.</summary>
        public const byte Global = 0;

        /// <summary>
        /// The members of the sender's room, and nobody else. A sender who is in no room is
        /// refused with <see cref="ErrorCode.NotInARoom"/> rather than silently dropped.
        /// </summary>
        public const byte Room = 1;

        /// <summary>True when <paramref name="channel"/> is one this protocol defines.</summary>
        public static bool IsDefined(byte channel) => channel == Global || channel == Room;
    }

    /// <summary>
    /// Limits on an <c>MSP_CHAT_SEND</c> body, shared by the sender and the master that judges it.
    /// </summary>
    /// <remarks>
    /// <b>The number was only ever on the server, and that is what made an over-long line
    /// unfixable by the player.</b> <c>ChatService.TryCreate</c> refused anything past 200
    /// characters and reported the refusal with the same code it used for flooding, so the client
    /// said "you are sending too often" — and the player waited, sent the identical text, and got
    /// the identical sentence. Nothing in the UI capped the field, because nothing in the UI knew
    /// the cap. It lives here so the input field can be built from it and the master can enforce
    /// the same number.
    /// </remarks>
    public static class MspChatLimits
    {
        /// <summary>
        /// Longest chat line the master accepts, in characters, measured before sanitizing.
        /// </summary>
        /// <remarks>
        /// <b>Before sanitizing, deliberately.</b> Stripping control characters can only shorten
        /// a string, so judging the raw length refuses a 5,000-character paste without first
        /// allocating a sanitized copy of it.
        /// </remarks>
        public const int MaxTextCharacters = 200;
    }
}
