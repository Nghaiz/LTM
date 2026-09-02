#nullable enable

using Ironfront.Net.Protocol;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Turns a master-server error code into a line the player can act on.
    /// protocol-spec.md § 13.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written by the lead's assist track
    /// (plans/unity-client/study/step-06-master-connection.md). phase-03 handoff item 2 — "send D the
    /// list of error codes the client needs to display, cross-checked against the table in
    /// protocol-spec.md § 13" — is this file, and every code in that table is covered below.
    /// </para>
    /// <para>
    /// <b>The codes are read through <see cref="ErrorCode"/> rather than as bare integers.</b>
    /// That enum is in the shared protocol library, which both ends already compile against, so
    /// a code renumbered in the spec breaks this switch at compile time instead of silently
    /// describing the wrong failure to a player.
    /// </para>
    /// <para>
    /// <b>Every message names what to do next.</b> phase-03 criterion 7 is "wrong password → a
    /// clear error message", and a client that renders "error 1000" satisfies nothing. An
    /// unrecognised code still shows its number, because a number a player can read out is far
    /// better than "something went wrong" when the alternative is a support conversation.
    /// </para>
    /// </remarks>
    public static class MasterErrorText
    {
        /// <summary>What to show when a request failed but carried no code.</summary>
        public const string Unknown = "The master server refused the request.";

        /// <summary>A player-facing sentence for one error code.</summary>
        public static string Describe(int errorCode) => Describe((ErrorCode)errorCode);

        /// <summary>
        /// A player-facing sentence for a failed request, using the master's own wait when it
        /// sent one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The wait used to be an adjective this file invented.</b> Every
        /// <see cref="ErrorCode.RateLimited"/> rendered as "Wait a few seconds and try again"
        /// against a window of SIXTY, so a player who did as they were told failed again and was
        /// told the same thing -- an instruction that reads as a loop. The master now sends the
        /// number (<c>MSP_LOGIN_RES.retryAfterSec</c>), and a number the player can act on beats
        /// a guess this layer is in no position to make.
        /// </para>
        /// <para>
        /// <b>Zero falls back to the wordless form rather than saying "0 seconds".</b> A master
        /// older than the field sends nothing, and "try again in 0 seconds" would be both wrong
        /// and unfollowable.
        /// </para>
        /// </remarks>
        public static string DescribeFailure(int errorCode, int retryAfterSeconds)
        {
            if (errorCode == (int)ErrorCode.Ok) return Unknown;
            if (retryAfterSeconds <= 0) return Describe((ErrorCode)errorCode);

            string wait = FormatWait(retryAfterSeconds);

            switch ((ErrorCode)errorCode)
            {
                case ErrorCode.RateLimited:
                    return "Too many login attempts from this network. Try again in " + wait + ".";
                case ErrorCode.AccountLocked:
                    return "That password is right, but the account is locked after too many "
                           + "failed attempts. Try again in " + wait + ".";
                default:
                    return Describe((ErrorCode)errorCode);
            }
        }

        /// <summary>Whole seconds as something a player reads without converting it.</summary>
        private static string FormatWait(int seconds)
        {
            if (seconds < 60) return seconds + (seconds == 1 ? " second" : " seconds");

            int minutes = (seconds + 59) / 60;
            return minutes + (minutes == 1 ? " minute" : " minutes");
        }

        /// <summary>
        /// A player-facing sentence for a request that failed, whatever code it carried.
        /// </summary>
        /// <remarks>
        /// The difference from <see cref="Describe"/> is code 0. A master that answers
        /// <c>ok=false</c> without filling in <c>errorCode</c> is not reporting success, but
        /// <see cref="Describe"/> would translate the 0 into "OK." and put that word in red on
        /// the login screen. Every failure path uses this overload for that reason; the plain
        /// one is for when a code is being described on its own terms.
        /// </remarks>
        public static string DescribeFailure(int errorCode)
            => errorCode == (int)ErrorCode.Ok ? Unknown : Describe((ErrorCode)errorCode);

        /// <summary>A player-facing sentence for one error code.</summary>
        public static string Describe(ErrorCode code)
        {
            switch (code)
            {
                case ErrorCode.Ok:
                    return "OK.";

                // ----- accounts (1000-1005)
                case ErrorCode.WrongCredentials:
                    return "Wrong username or password.";
                case ErrorCode.UsernameTaken:
                    return "That username is already taken.";
                case ErrorCode.InvalidUsername:
                    return "Usernames are 3-16 characters, using a-z, 0-9 and underscore.";
                case ErrorCode.SessionExpired:
                    return "Your session expired. Please log in again.";
                case ErrorCode.WrongClientVersion:
                    return "This build is out of date. Update the game and try again.";
                case ErrorCode.InvalidDisplayName:
                    return "Display names are at most 32 characters. Leave it blank to use your username.";
                case ErrorCode.AccountLocked:
                    // Names the state and, crucially, does NOT send the player to change their
                    // password -- which is what "Wrong username or password." did, and which
                    // cannot clear a lock. The wait itself comes from the master; this is the
                    // wording used when it sent no number.
                    return "That password is right, but the account is locked after too many "
                           + "failed attempts. Wait a few minutes and try again.";
                case ErrorCode.AccountBanned:
                    return "This account is banned.";

                // ----- rooms (2000-2004)
                case ErrorCode.RoomNotFound:
                    return "That room no longer exists. Refresh the list.";
                case ErrorCode.RoomFull:
                    return "That room is full.";
                case ErrorCode.WrongRoomPassword:
                    return "Wrong room password.";
                case ErrorCode.MatchAlreadyStarted:
                    return "That match has already started.";
                case ErrorCode.AlreadyInAnotherRoom:
                    return "You are already in another room. Leave it first.";
                case ErrorCode.TeamsWouldUnbalance:
                    // Says what the player can do about it. The side is full because a room
                    // splits its seats in half, and the wait is for somebody to leave that side
                    // -- not for the room to fill, which is the opposite reading.
                    return "That side is full. Wait for a slot on it, or stay where you are.";

                // ----- game servers (3000-3001)
                case ErrorCode.NoGameServerAvailable:
                    return "No game server is free right now. Try again in a moment.";
                case ErrorCode.GameServerNotResponding:
                    return "The game server is not responding. Try another room.";

                // ----- chat (4000-4003)
                case ErrorCode.ChatMessageTooLong:
                    // The number, because the fix is to shorten it and a player cannot shorten
                    // to an unstated target. This refusal used to arrive as RateLimited, so the
                    // advice was to wait -- and the same text failed identically afterwards.
                    return "That message is too long. Chat is at most "
                           + MspChatLimits.MaxTextCharacters + " characters.";
                case ErrorCode.ChatMessageEmpty:
                    return "There was nothing to send.";
                case ErrorCode.ChatChannelInvalid:
                    return "This build asked for a chat channel the server does not have.";
                case ErrorCode.NotInARoom:
                    return "That message was for a room and you are not in one.";
                case ErrorCode.ChatTooFast:
                    return "You are sending messages too quickly. Wait a few seconds.";

                // ----- the master itself (9000-9001)
                case ErrorCode.InternalServerError:
                    return "The master server hit an internal error. Try again.";
                case ErrorCode.RateLimited:
                    // Login only, since chat flooding got its own code -- one code for both
                    // windows (60 s per address, 10 s per player) forces a sentence that is
                    // wrong about one of them.
                    //
                    // "From this network", because the budget is counted per SOURCE ADDRESS:
                    // two people behind one home router share it, and "you" reads to the second
                    // of them as a plain falsehood on their very first attempt. "Up to a minute"
                    // is the real window -- it was "a few seconds", which sent players away to
                    // wait and fail again. Where the master sends the exact remainder,
                    // DescribeFailure(code, retryAfterSeconds) renders that number instead.
                    return "Too many login attempts from this network. Wait up to a minute and try again.";

                default:
                    // A code from a newer master than this build knows. Showing the number
                    // keeps it reportable rather than reducing it to "something went wrong".
                    return $"The master server refused the request (code {(int)code}).";
            }
        }
    }
}
