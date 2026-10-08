using Ironfront.Net.Protocol;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// What a practice match is played by: the room settings a multiplayer host chooses (mode,
    /// victory rule, points, night-vision battery, bots), and the three things only an offline
    /// match can choose: the player's side, whether vehicles spawn, and the respawn time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner, 2026-10-08:</b> "bring the features multiplayer already has to practice; the
    /// settings a multiplayer room is created with are not in practice, everything there says IN
    /// DEVELOPMENT". So the room half is <see cref="RoomSettings"/> itself, read by the same rules
    /// the create-room form uses, rather than a practice-only copy that could drift from it.
    /// </para>
    /// <para>
    /// <b>Bots are the match's total, as a room's are</b> (protocol 13), split evenly between the
    /// sides the way the server splits a room's ("N per team").
    /// </para>
    /// <para>
    /// Engine-free so <c>Ironfront.Client.Flow.Tests</c> can compile it.
    /// </para>
    /// </remarks>
    public readonly struct PracticeSettings
    {
        /// <summary>The bots a fresh practice screen offers: a room's default.</summary>
        public const int DefaultBots = ProtocolConstants.DEFAULT_ROOM_BOTS;

        /// <summary>The respawn time the original practice menu shipped with, in seconds.</summary>
        public const int DefaultRespawnSeconds = 5;

        /// <summary>Shortest respawn time a practice match may set, in seconds.</summary>
        public const int MinRespawnSeconds = 1;

        /// <summary>Longest respawn time a practice match may set, in seconds.</summary>
        public const int MaxRespawnSeconds = 60;

        public PracticeSettings(RoomSettings rules, int bots, int playerTeam, bool vehicles, int respawnSeconds)
        {
            Rules = rules;
            Bots = bots;
            PlayerTeam = playerTeam;
            Vehicles = vehicles;
            RespawnSeconds = respawnSeconds;
        }

        /// <summary>Mode, victory rule, points and night-vision battery: a room's own settings.</summary>
        public RoomSettings Rules { get; }

        /// <summary>The match's bots, both sides together.</summary>
        public int Bots { get; }

        /// <summary>The side the player fights on: 0 is blue, 1 is red.</summary>
        public int PlayerTeam { get; }

        /// <summary>Whether the map's vehicles spawn at all.</summary>
        public bool Vehicles { get; }

        /// <summary>Seconds between respawn waves, for bots and the player alike.</summary>
        public int RespawnSeconds { get; }

        /// <summary>Blue's bots: half the total, rounded down.</summary>
        public int Team0Bots => Bots / 2;

        /// <summary>Red's bots: the rest.</summary>
        public int Team1Bots => Bots - Team0Bots;

        /// <summary>What the practice screen opens with: today's room defaults, blue, vehicles on.</summary>
        public static PracticeSettings Default
            => new PracticeSettings(RoomSettings.Default, DefaultBots, 0, vehicles: true, DefaultRespawnSeconds);
    }
}
