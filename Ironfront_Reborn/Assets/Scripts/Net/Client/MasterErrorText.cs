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

                // ----- the master itself (9000-9001)
                case ErrorCode.InternalServerError:
                    return "The master server hit an internal error. Try again.";
                case ErrorCode.RateLimited:
                    return "Too many attempts. Wait a few seconds and try again.";

                default:
                    // A code from a newer master than this build knows. Showing the number
                    // keeps it reportable rather than reducing it to "something went wrong".
                    return $"The master server refused the request (code {(int)code}).";
            }
        }
    }
}
