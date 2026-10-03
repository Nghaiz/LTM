using System;
using Ironfront.Net.Protocol;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// The game-mode settings of the room this game server hosts: game mode, victory rule, points
    /// and night-vision battery, pushed by the master with the room (GS_ROOM_ASSIGNED, protocol 14,
    /// phase P32). <c>MatchController</c> plays by them.
    /// </summary>
    /// <remarks>
    /// The same shape as <see cref="NetBotRelease"/>'s room bot count, and fed from the same
    /// assignment: <c>MasterLinkBootstrap</c> wires <c>GameServerMatchReporter.RoomSettingsAssigned</c>
    /// here. Offline and before any assignment it holds <see cref="RoomSettings.Default"/>, the
    /// match the game has always played.
    /// </remarks>
    public static class NetRoomRules
    {
        /// <summary>The room the settings below belong to, or 0 before any assignment.</summary>
        public static ushort RoomId { get; private set; }

        /// <summary>The hosted room's settings.</summary>
        public static RoomSettings Current { get; private set; } = RoomSettings.Default;

        /// <summary>Raised when a room's settings differ from the ones held, on the main thread.</summary>
        public static event Action<RoomSettings> Changed;

        /// <summary>
        /// Records the settings of the room the master allocated this server to. Called for every
        /// ticket the master issues, so it logs and raises only a change.
        /// </summary>
        public static void Assign(ushort roomId, RoomSettings settings)
        {
            bool changed = roomId != RoomId || !SameAs(settings, Current);
            RoomId = roomId;
            Current = settings;
            if (!changed) return;

            Debug.Log($"[net] room {roomId} plays {settings}.");
            Changed?.Invoke(settings);
        }

        private static bool SameAs(in RoomSettings a, in RoomSettings b)
            => a.Mode == b.Mode && a.Rule == b.Rule && a.VictoryPoints == b.VictoryPoints
               && a.NightVisionSeconds == b.NightVisionSeconds;

        // Domain reload is off in the Editor: a room from one play session must not carry into the next.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForPlaySession()
        {
            RoomId = 0;
            Current = RoomSettings.Default;
            Changed = null;
        }
    }
}
