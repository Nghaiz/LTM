using Ironfront.Net.Replication.Projectiles;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// The face <c>Assembly-CSharp</c>'s projectile damage call sites consult before applying
    /// damage, so the engine and the library stepper can never both do it.
    /// debt-closure phase 2 task 2e, ledger C-1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The delete-path, written and left off.</b>
    /// <c>ServerProjectileBridge.AuthoritativeFlight</c> has shipped default-off since V7 behind
    /// a remark saying that turning it on without first removing the engine-side damage call
    /// would run both simulations and apply damage twice. That remark was the entire safety
    /// mechanism. Phase 2 turns it into three guards and a gate rule, and leaves the flag off
    /// (P-D2) — Phase 5 is the only place it flips, and it arrives with a prepared patch and a
    /// proof obligation rather than a refactor made under measurement pressure.
    /// </para>
    /// <para>
    /// <b>Static, for <see cref="NetVehicleAuthority"/>'s reason.</b> A projectile is an authored
    /// prefab instantiated at the moment of firing; a serialized reference per prefab would be a
    /// manual step that gets forgotten on the one prefab nobody re-opened, and the symptom would
    /// be a projectile whose damage is applied twice with nothing in the log.
    /// </para>
    /// <para>
    /// <b>The decision itself lives in the library</b>, in
    /// <see cref="ProjectileDamageOwnership"/>, so it can be tested without a Unity assembly.
    /// This type is the role adapter: it supplies the two booleans that type takes and nothing
    /// else. Duplicating the rules here would be the classic two-copies bug — the two drift by
    /// one edge case, and the edge case is "offline", where getting it wrong silently stops
    /// single-player doing damage at all.
    /// </para>
    /// </remarks>
    public static class NetProjectileAuthority
    {
        /// <summary>
        /// Mirrors <c>ServerProjectileBridge.AuthoritativeFlight</c>, pushed on assignment.
        /// </summary>
        /// <remarks>
        /// A mirror rather than a reference because <c>Assembly-CSharp</c> must be able to ask
        /// this question from a projectile prefab that has no way to reach the bridge instance,
        /// and because the bridge does not exist at all offline or on a client — where the
        /// answer still has to be correct.
        /// </remarks>
        public static bool AuthoritativeFlight { get; set; }

        /// <summary>
        /// Whether the ENGINE's own hit path should apply this projectile's damage.
        /// </summary>
        /// <remarks>
        /// The full three-way answer, for the sweep in <c>Projectile.Travel</c>: true offline and
        /// on a server with the flag off, false on a client (health arrives in snapshots) and on
        /// a server with the flag on (the stepper owns it). It subsumes the
        /// <c>!NetContext.IsClient</c> check that site already carried.
        /// </remarks>
        public static bool EngineAppliesProjectileDamage =>
            ProjectileDamageOwnership.EngineApplies(
                NetContext.IsClient, NetContext.IsOffline, AuthoritativeFlight);

        /// <summary>
        /// Whether the LIBRARY stepper will apply this projectile's damage, so the engine must
        /// not.
        /// </summary>
        /// <remarks>
        /// <b>Not simply the negation of <see cref="EngineAppliesProjectileDamage"/>, and the
        /// difference is load-bearing.</b> The two <c>ActorManager.Explode</c> call sites do more
        /// than damage: on a client that same call still applies the corpse ragdoll impulse,
        /// which is kept at every role because corpses are never replicated (AD-4), and draws the
        /// local player's own predicted blast (V10 D13). Gating those on "does the engine apply
        /// damage" would switch both off on every client. So they ask the narrower question —
        /// "is somebody else about to do this?" — which is false on a client and offline, leaving
        /// those builds byte-for-byte unchanged.
        /// </remarks>
        public static bool LibraryOwnsProjectileDamage =>
            ProjectileDamageOwnership.LibraryApplies(
                NetContext.IsClient, NetContext.IsOffline, AuthoritativeFlight);

        /// <summary>Restores the default. Role teardown, and the flag's own default is off.</summary>
        public static void Clear() => AuthoritativeFlight = false;

        [UnityEngine.RuntimeInitializeOnLoadMethod(
            UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad() => Clear();
    }
}
