using System;
using System.Collections.Generic;

namespace Ironfront.Net.Configuration
{
    /// <summary>
    /// The one table that maps a numeric <c>mapId</c> to the Unity scene that hosts it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This table did not exist, and its absence was a manual step in the player's flow.</b>
    /// <c>DedicatedServerSceneBootstrap</c> said so out loud — "there is no mapId-to-scene table
    /// anywhere in the repository" — and chose <c>IRONFRONT_GAMESERVER_SCENE</c> for that reason.
    /// The consequence was on the client: <c>RoomInfo.MapId</c> arrives from the master as a
    /// number, and nothing could turn it into a scene, so no client could load the map the room
    /// it had just joined was being played on. A human picked the map on the server with an
    /// environment variable, and a human picked it again on the client by opening that scene in
    /// the Editor. P8 task 3.2 counts both as manual interventions; this file removes them by
    /// giving the two ends one table instead of two conventions.
    /// </para>
    /// <para>
    /// <b>It lives here rather than in the protocol.</b> The wire carries the id and nothing
    /// else — the id's meaning is deployment content, not framing, and putting it in
    /// <c>Ironfront.Net.Protocol</c> would make a new map a protocol change and a
    /// <c>SpecChecker</c> constant. This assembly is already the single source of truth for the
    /// other thing both ends must agree on and disagree about silently, which is configuration,
    /// and every process in the repository already references it.
    /// </para>
    /// <para>
    /// <b>Ids are assigned here for the first time.</b> Nothing shipped had ever populated
    /// <c>IRONFRONT_GAMESERVER_MAP_IDS</c> — it is empty in <c>.env.example</c>, in
    /// <c>infra/compose/.env.example</c> and in the two compose services — so no deployment can
    /// be broken by choosing them now. They start at 1 because 0 is the value an unset ushort
    /// takes, and a room advertising map 0 must be distinguishable from a room whose map was
    /// never set.
    /// </para>
    /// <para>
    /// <b>Adding a map is two edits, and the second one is not optional.</b> Add a row here, and
    /// add the scene to <c>EditorBuildSettings</c>. A row naming a scene that is not in the
    /// build resolves fine here and then fails at <c>SceneManager.LoadScene</c> — which is why
    /// the client checks <c>Application.CanStreamedLevelBeLoaded</c> before it trusts a name
    /// this table gave it.
    /// </para>
    /// </remarks>
    public static class MapCatalog
    {
        /// <summary>One playable map: its wire id, its scene, and a name for a room list.</summary>
        public readonly struct MapEntry
        {
            public MapEntry(ushort id, string sceneName, string displayName)
            {
                Id = id;
                SceneName = sceneName;
                DisplayName = displayName;
            }

            /// <summary>The id carried on the wire, in <c>RoomInfo.MapId</c>.</summary>
            public ushort Id { get; }

            /// <summary>The scene name <c>SceneManager</c> takes. Case-sensitive.</summary>
            public string SceneName { get; }

            /// <summary>What a player sees in a room list.</summary>
            public string DisplayName { get; }
        }

        /// <summary>
        /// The id used when a room names no map, and the map a standalone server hosts by
        /// default.
        /// </summary>
        /// <remarks>
        /// Dustbowl, matching <c>DedicatedServerSceneBootstrap.DefaultScene</c> and the lane-B
        /// harness's own default. Three defaults that disagree would be three ways for a client
        /// and a server to end up on different maps while both logs look correct.
        /// </remarks>
        public const ushort DefaultMapId = 1;

        /// <summary>Every playable map, in id order.</summary>
        /// <remarks>
        /// <c>Splash</c> and <c>Menu</c> are deliberately absent: they are shell scenes, they
        /// carry no <c>NetServerBootstrap</c>, and a room advertising one of them would name a
        /// map on which no match can be hosted.
        /// </remarks>
        public static readonly IReadOnlyList<MapEntry> All = new[]
        {
            new MapEntry(1, "Dustbowl", "Dustbowl"),
            new MapEntry(2, "Island", "Island"),
        };

        /// <summary>The scene hosting <paramref name="mapId"/>, or false if no row claims it.</summary>
        /// <remarks>
        /// Reports rather than throws, and reports rather than falling back to the default. A
        /// client that silently loaded Dustbowl for an unknown id would put the player in a
        /// different world from the server simulating them, and every symptom of that reads as
        /// a replication fault: bodies at coordinates with no ground under them, a capture point
        /// nobody can reach. An id nobody claims is a configuration error and has to say so.
        /// </remarks>
        public static bool TryGetScene(ushort mapId, out string sceneName)
        {
            for (int i = 0; i < All.Count; i++)
            {
                if (All[i].Id != mapId) continue;
                sceneName = All[i].SceneName;
                return true;
            }

            sceneName = string.Empty;
            return false;
        }

        /// <summary>The id of the map <paramref name="sceneName"/> hosts, or false.</summary>
        /// <remarks>
        /// Ordinal and case-sensitive, because <c>SceneManager</c> is: a lookup that accepted
        /// "dustbowl" here would hand back an id whose scene name then fails to load, and the
        /// error would name the id rather than the casing.
        ///
        /// Null is accepted and answered false rather than thrown at: the callers are an
        /// environment value and an active scene's name, and neither is worth a guard clause at
        /// every call site to say the same thing this method already says.
        /// </remarks>
        public static bool TryGetId(string? sceneName, out ushort mapId)
        {
            if (!string.IsNullOrWhiteSpace(sceneName))
            {
                string trimmed = sceneName.Trim();
                for (int i = 0; i < All.Count; i++)
                {
                    if (!string.Equals(All[i].SceneName, trimmed, StringComparison.Ordinal)) continue;
                    mapId = All[i].Id;
                    return true;
                }
            }

            mapId = 0;
            return false;
        }

        /// <summary>
        /// The scene for <paramref name="mapId"/>, falling back to the default map's scene and
        /// saying which happened.
        /// </summary>
        /// <remarks>
        /// For the one caller that must produce a scene name no matter what — a direct-connect
        /// dial, which never passed through a room and so carries no map id at all.
        /// <paramref name="resolved"/> is what lets that caller log "the room named map 7, which
        /// this build does not know" instead of loading Dustbowl quietly.
        /// </remarks>
        public static string SceneOrDefault(ushort mapId, out bool resolved)
        {
            resolved = TryGetScene(mapId, out string scene);
            if (resolved) return scene;

            TryGetScene(DefaultMapId, out string fallback);
            return fallback;
        }
    }
}
