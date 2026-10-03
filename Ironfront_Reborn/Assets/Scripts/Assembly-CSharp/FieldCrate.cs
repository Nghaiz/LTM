using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// An ammunition or medical crate left in the field at random (phase P32): what the radar draws
/// and what a squad short of rounds or health walks to. The resupply itself is the deployable's own
/// (an <see cref="Ammobox"/> or <see cref="Medipack"/> on the same object), so a crate serves both
/// sides, like a dropped bag.
/// </summary>
/// <remarks>
/// <para>
/// <b>On every role.</b> The crate prefab carries this component, so the server's copy (placed by
/// <see cref="FieldSupplyDirector"/>), an offline copy and a client's copy (drawn by the projectile
/// presenter from <c>S_PROJECTILE_SPAWN</c>) all list themselves; the client's radar and the
/// server's bots read the same list.
/// </para>
/// </remarks>
public sealed class FieldCrate : MonoBehaviour
{
	/// <summary>Metres a soldier must stand within: a bag's range (<c>ServerDeployableAuthority.ResupplyRange</c>).</summary>
	public const float Range = 6f;

	public SupplyKind kind;

	private static readonly List<FieldCrate> all = new List<FieldCrate>();

	/// <summary>Every crate in the field right now.</summary>
	public static IReadOnlyList<FieldCrate> All => all;

	/// <summary>The nearest crate of a kind the caller needs, within <paramref name="maxDistance"/>; null when none.</summary>
	public static FieldCrate Nearest(Vector3 from, bool ammo, bool medical, float maxDistance)
	{
		FieldCrate best = null;
		float bestSquared = maxDistance * maxDistance;
		for (int i = 0; i < all.Count; i++)
		{
			FieldCrate crate = all[i];
			if (crate == null || !(crate.kind == SupplyKind.Ammo ? ammo : medical))
			{
				continue;
			}
			float squared = (crate.transform.position - from).sqrMagnitude;
			if (squared < bestSquared)
			{
				bestSquared = squared;
				best = crate;
			}
		}
		return best;
	}

	private void OnEnable()
	{
		all.Add(this);
	}

	private void OnDisable()
	{
		all.Remove(this);
	}
}
