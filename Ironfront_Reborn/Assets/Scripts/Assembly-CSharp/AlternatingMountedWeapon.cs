using System;
using Ironfront.Net.Replication.Vehicles;
using UnityEngine;

public class AlternatingMountedWeapon : MountedWeapon
{
	public Transform[] muzzles;

	/// <summary>
	/// Which muzzle the NEXT shot leaves from. Replicated in the vehicle entry's subtype tail.
	/// V6 task 4.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Not cosmetic.</b> It selects the transform <see cref="MuzzlePosition"/> returns, which
	/// AI aiming reads and which V7's projectile origin will read — so an unreplicated value is an
	/// AIM divergence, not a wrong-looking flash.
	/// </para>
	/// <para>
	/// Public so the encoder can read it and the decoder can write it, and
	/// <c>[NonSerialized]</c> because it is runtime state: a prefab carrying a stale muzzle index
	/// would open every match mid-sequence.
	/// </para>
	/// </remarks>
	[NonSerialized]
	public byte currentMuzzle;

	/// <summary>
	/// Advances to the next muzzle. The ONE mutation site.
	/// </summary>
	/// <remarks>
	/// Extracted so there is exactly one place the index moves — the shipped code advanced it
	/// inline inside <see cref="SpawnProjectile"/>, which is fine until a second caller appears
	/// and the two disagree about whether the advance already happened.
	/// </remarks>
	public void AdvanceMuzzle()
	{
		if (muzzles == null || muzzles.Length == 0)
		{
			return;
		}
		currentMuzzle = (byte)((currentMuzzle + 1) % muzzles.Length);
	}

	/// <summary>
	/// Writes a replicated muzzle index, folded into range.
	/// </summary>
	/// <remarks>
	/// <b>The modulo is the whole point.</b> <c>muzzles.Length</c> is a per-prefab authored value,
	/// so a client whose prefab revision has fewer muzzles than the server's would index out of
	/// range and throw inside the render path. Folding costs nothing and turns a crash into a
	/// wrong-but-harmless muzzle choice.
	/// </remarks>
	public void ApplyReplicatedMuzzle(byte index)
	{
		currentMuzzle = VehicleSubtypeTail.FoldMuzzleIndex(
			index, muzzles != null ? muzzles.Length : 0);
	}

	/// <summary>
	/// Spawns from the CURRENT muzzle, then advances.
	/// </summary>
	/// <remarks>
	/// <b>The order is load-bearing and looks like a bug.</b> The shell leaves the old muzzle
	/// while <see cref="MuzzlePosition"/> immediately afterwards returns the NEXT one — and that
	/// asymmetry is what the AI has aimed with since before the netcode existed. Preserved
	/// exactly; a test pins the sequence, because "tidying" it into advance-then-spawn would move
	/// every AI's aim point by one barrel with nothing failing anywhere.
	/// </remarks>
	protected override Projectile SpawnProjectile(Vector3 direction)
	{
		Transform transform = muzzles[currentMuzzle];
		AdvanceMuzzle();
		Quaternion rotation = Quaternion.LookRotation(direction + UnityEngine.Random.insideUnitSphere * configuration.spread);
		Projectile component = ((GameObject)UnityEngine.Object.Instantiate(configuration.projectilePrefab, transform.position, rotation)).GetComponent<Projectile>();
		component.source = user;
		return component;
	}

	public override Vector3 MuzzlePosition()
	{
		return muzzles[currentMuzzle].position;
	}
}
