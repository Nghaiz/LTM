using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A scene object that detonates when shot: a fuel drum, a gas cylinder, an ammo crate.
/// debt-closure phase 2 task 2f, ledger C-11.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this fills.</b> <c>ExplosionKind.Environment</c> has been on the wire since V1 and
/// had zero production producers: the only three construction sites are Rocket, Grenade and
/// <c>ServerProjectileBridge.ExplosionKindFor</c>, whose mapper is <c>Grenade : Rocket</c>. The
/// member existed, the client's effect table indexed it, and nothing could ever emit it. V1
/// handed it forward to V7 and V7 added no environment path. This is a small feature rather than
/// a debt repayment and is sized as one.
/// </para>
/// <para>
/// <b>Server-authoritative through the same path as every other blast.</b> It calls
/// <c>ActorManager.Explode</c>, which owns the three-way role split at its own choke point — so
/// a client applies no health damage and keeps only the corpse ragdoll impulse (AD-4), and the
/// server announces one <c>S_EXPLOSION</c> carrying <c>ExplosionKind.Environment</c>. Nothing
/// about the role rules is re-implemented here.
/// </para>
/// <para>
/// <b>No attacker.</b> <c>ActorManager.Explode</c> takes a null <c>source</c> and
/// <c>ResolveAttackerId</c> turns that into its no-attacker sentinel rather than into actor 0,
/// which is a real id. A drum that kills someone is not credited to whoever shot it: attributing
/// a chain of three drums to the player who started it is a scoring decision nobody has taken,
/// and inventing one here would be worse than leaving the kill unattributed.
/// </para>
/// <para>
/// <b>Chain detonation is deferred, never recursive.</b> A drum's blast damages a neighbouring
/// drum, whose own detonation runs from <c>Invoke</c> on a later frame — the same discipline
/// <c>Vehicle.Die</c> uses, and what keeps <c>ActorManager.Explode</c>'s shared victim buffer
/// from being re-entered mid-loop.
/// </para>
/// </remarks>
[DisallowMultipleComponent]
public class ExplosiveProp : MonoBehaviour
{
	[Tooltip("Damage this prop absorbs before it goes off.")]
	public float health = 40f;

	[Tooltip("Seconds between taking lethal damage and detonating. Also breaks a chain's recursion.")]
	public float fuseSeconds = 0.3f;

	[Tooltip("The blast. Unassigned falls back to code defaults; see WreckExplosion on Vehicle.")]
	public ExplodingProjectile.ExplosionConfiguration explosionConfiguration;

	[Tooltip("Optional. Played on detonation; absent on a stripped headless build.")]
	public ParticleSystem detonationParticles;

	[Tooltip("Optional. Played on detonation.")]
	public AudioSource detonationSound;

	[Tooltip("Optional. Hidden on detonation, so the drum stops being a drum.")]
	public Renderer[] renderers;

	private bool detonated;

	private bool fuseLit;

	/// <summary>
	/// Every live prop, so a blast can reach them without a scene-wide search.
	/// </summary>
	/// <remarks>
	/// The same shape as <c>ActorManager.vehicles</c> and for the same reason: an explosion
	/// happens often enough that a <c>FindObjectsOfType</c> per blast is not an option, and props
	/// are placed once per map rather than spawned per second.
	/// </remarks>
	private static readonly List<ExplosiveProp> live = new List<ExplosiveProp>();

	/// <summary>Live props. Read by <c>ActorManager.Explode</c>; never mutated by the caller.</summary>
	public static IReadOnlyList<ExplosiveProp> Live => live;

	private void OnEnable()
	{
		if (!live.Contains(this))
		{
			live.Add(this);
		}
	}

	private void OnDisable()
	{
		live.Remove(this);
	}

	/// <summary>
	/// Takes damage, and lights the fuse when it runs out.
	/// </summary>
	/// <remarks>
	/// <b>The health ladder runs at every role and that is correct here</b>, unlike an actor's:
	/// a prop's health is not replicated, so there is no authoritative value for a client to
	/// disagree with. What a client must not do is apply the BLAST's damage, and it does not —
	/// <c>ActorManager.Explode</c> refuses that itself.
	/// </remarks>
	public void Damage(float amount)
	{
		if (detonated || fuseLit)
		{
			return;
		}
		health -= amount;
		if (health > 0f)
		{
			return;
		}
		fuseLit = true;
		Invoke("Detonate", fuseSeconds);
	}

	/// <summary>Sets it off now, whatever its health. For a scripted event or a trigger.</summary>
	public void Detonate()
	{
		if (detonated)
		{
			return;
		}
		detonated = true;

		ActorManager.Explode(
			base.transform.position, Blast(), null,
			Ironfront.Net.Protocol.ExplosionKind.Environment);

		if (detonationParticles != null)
		{
			detonationParticles.Play();
		}
		if (detonationSound != null)
		{
			detonationSound.Play();
		}
		if (renderers != null)
		{
			for (int i = 0; i < renderers.Length; i++)
			{
				if (renderers[i] != null)
				{
					renderers[i].enabled = false;
				}
			}
		}

		// Colliders stay, deliberately: a drum that vanishes on detonation would drop anything
		// standing on it through the world. The prop is inert, not absent.
		base.enabled = false;
	}

	/// <summary>
	/// The blast, with code defaults when nothing was authored.
	/// </summary>
	/// <remarks>
	/// Same reasoning as <c>Vehicle.WreckExplosion</c>: an unauthored
	/// <c>ExplosionConfiguration</c> has null <c>AnimationCurve</c>s and reading it straight
	/// would throw inside the detonation. The curves run 1 at the centre to 0 at the edge,
	/// because <c>ExplosionRanges</c> hands out <c>t = distance / range</c>.
	/// </remarks>
	private ExplodingProjectile.ExplosionConfiguration Blast()
	{
		if (explosionConfiguration == null)
		{
			explosionConfiguration = new ExplodingProjectile.ExplosionConfiguration();
		}
		if (explosionConfiguration.damageFalloff == null || explosionConfiguration.damageFalloff.length == 0)
		{
			explosionConfiguration.damageFalloff = AnimationCurve.Linear(0f, 1f, 1f, 0f);
		}
		if (explosionConfiguration.balanceFalloff == null || explosionConfiguration.balanceFalloff.length == 0)
		{
			explosionConfiguration.balanceFalloff = AnimationCurve.Linear(0f, 1f, 1f, 0f);
		}
		return explosionConfiguration;
	}
}
