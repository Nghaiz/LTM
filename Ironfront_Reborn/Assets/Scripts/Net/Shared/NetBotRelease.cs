using Ironfront.Net.Replication.Match;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// The one place the game asks "may a bot exist yet?", and the one place it reports that a
    /// player has entered the world.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a static seam rather than a component.</b> The two callers sit on opposite sides
    /// of the assembly boundary and neither can hold a reference to the other:
    /// <c>ActorManager</c> asks the question and compiles into <c>Assembly-CSharp</c>, which no
    /// assembly definition may reference; <c>ServerCombatBridge</c> answers it from
    /// <c>Ironfront.Net.Unity.Server</c>. The same constraint that produced
    /// <see cref="NetSceneBindings"/>, and the same shape.
    /// </para>
    /// <para>
    /// <b>The policy is engine-free; only the clock is not.</b>
    /// <see cref="BotReleaseGate"/> holds the rule and is covered by <c>dotnet test</c>;
    /// this type supplies <c>Time.time</c> and nothing else. Keeping the arithmetic out of a
    /// <c>MonoBehaviour</c> is what makes "no bot before a human, and not for 30s after" a
    /// thing a test can assert without a scene.
    /// </para>
    /// <para>
    /// <b>Offline is included, deliberately.</b> This departs from V8 D2's "offline is
    /// unchanged" standing rule, on the owner's instruction (2026-09-20): the single-player
    /// match has the same complaint — the map is carved up before the player is in it — and
    /// gating one mode only would leave the two spawning on different rules.
    /// </para>
    /// </remarks>
    public static class NetBotRelease
    {
        private static readonly BotReleaseGate Gate = new BotReleaseGate();

        /// <summary>Seconds after the first player spawn before bots may exist.</summary>
        public static float DelaySeconds => Gate.DelaySeconds;

        /// <summary>Whether a player body has entered the world this round.</summary>
        public static bool HasPlayerSpawned => Gate.HasAnchor;

        /// <summary>
        /// Whether bots may be created or respawned right now.
        /// </summary>
        /// <remarks>
        /// Read by <c>ActorManager.SpawnWave</c>, which the shipped <c>spawnTime: 0.1</c> runs
        /// ten times a second — so this is a field read and a subtraction, and allocates
        /// nothing.
        /// </remarks>
        public static bool IsReleased => Gate.IsReleasedAt(Time.time);

        /// <summary>Seconds until bots may exist; infinity while no player has spawned.</summary>
        public static float SecondsUntilRelease => Gate.SecondsUntilRelease(Time.time);

        /// <summary>
        /// Reports that a player body has entered the world. Only the first call per round
        /// anchors the clock.
        /// </summary>
        /// <remarks>
        /// Called from <c>ServerCombatBridge.PlaceAtSpawn</c> on the networked path and from
        /// <c>FpsActorController</c>'s first deploy offline. BOTH teams' bots release from this
        /// one anchor whichever team the player was on: anchoring per team would hand the map
        /// to whichever side happened to be occupied first.
        /// </remarks>
        public static void NotifyPlayerSpawned()
        {
            if (Gate.HasAnchor) return;

            Gate.NotifyPlayerSpawned(Time.time);
            Debug.Log(
                $"[net] first player body is in the world; bots release in {Gate.DelaySeconds:F0}s.");
        }

        /// <summary>
        /// Re-arms the gate for a new round, so the next one opens as empty as the first did.
        /// </summary>
        /// <remarks>
        /// Paired with despawning the previous round's bots. <c>PerformReset</c> restores every
        /// capture point's opening owner but does nothing to the bodies, so re-arming without
        /// clearing them would leave the gate shut over a map that is already occupied — the
        /// worst of both.
        /// </remarks>
        public static void ResetForNewRound()
        {
            if (!Gate.HasAnchor) return;

            Gate.Reset();
            Debug.Log("[net] bot release gate re-armed for the next round.");
        }
    }
}
