using Ironfront.Net.Unity;

/// <summary>
/// How long a detonated projectile's GameObject sticks around. Phase-V7 task 8.
/// </summary>
/// <remarks>
/// <para>
/// <b>The problem this exists to fix.</b> <c>ExplodingProjectile.Explode</c> instantiates
/// nothing -- the effects are pre-attached child <c>ParticleSystem</c>s disabled in place -- and
/// the GameObject is then kept alive by two chained timers: <c>StopSmoke</c> at
/// <c>smokeTime</c> (8 s), which chains to <c>Cleanup</c> at 10 s. Eighteen seconds after
/// impact, purely so particles can finish. On a headless server nobody is watching them, and
/// nothing bounds how many accumulate: sixteen players and thirty-two bots trading rockets hold
/// a growing pile of dead GameObjects, each still carrying a <c>ParticleSystem</c>, an
/// <c>AudioSource</c> and, for a <c>Rocket</c>, a <c>Light</c>.
/// </para>
/// <para>
/// <b>One branch selecting a delay, not a branch around the effect code.</b> V7 section 5 scores
/// "the server-side VFX guard is mis-scoped and strips client visuals too" at 8, and keeping the
/// role check down to a returned number is the mitigation: there is no path by which this file
/// can disable something a client renders, because it does not know what a client renders.
/// </para>
/// <para>
/// <b>Why the callers still use <c>Invoke</c>.</b> Both detonation paths set
/// <c>enabled = false</c> as part of going inert, which stops <c>Update</c> and would stop a
/// coroutine -- <c>Invoke</c> is the one timer that survives it without adding a second
/// component to every projectile prefab. What V7 task 8 actually objected to was the
/// <b>string</b> form, which no <c>grep</c> finds; the callers now pass <c>nameof</c>.
/// </para>
/// </remarks>
public static class ProjectileCleanupPolicy
{
	/// <summary>
	/// Seconds to hold a detonated projectile before destroying it.
	/// </summary>
	/// <param name="authoredSeconds">What the prefab asks for. Honoured everywhere but a server.</param>
	/// <returns>
	/// Zero at <see cref="NetRole.Server"/> -- destroyed on the next frame after the blast has
	/// resolved and <c>S_EXPLOSION</c> has been framed, because there is nothing to look at.
	/// The authored value at <see cref="NetRole.Client"/> and <see cref="NetRole.Offline"/>,
	/// exactly as before.
	/// </returns>
	public static float HoldSeconds(float authoredSeconds)
		=> NetContext.IsServer ? 0f : authoredSeconds;

	/// <summary>Whether the cosmetic smoke phase runs at all.</summary>
	/// <remarks>
	/// False on a server, where the chain it heads exists only to delay a destroy.
	/// </remarks>
	public static bool PlaysCosmetics => !NetContext.IsServer;
}
