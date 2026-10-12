using UnityEngine;

public class Wrench : MeleeWeapon
{
	protected const int VEHICLE_HIT_MASK = 4096;

	public float repairHealth = 50f;

	public AudioClip repairSound;

	protected override void ShootMeleeRay(Ray ray)
	{
		RaycastHit hitInfo;
		if (FirstHitInReach(ray, 4096, out hitInfo))
		{
			Vehicle componentInParent = hitInfo.collider.GetComponentInParent<Vehicle>();
			// A collider on the vehicle layer with no Vehicle above it is scenery: nothing to repair.
			if (componentInParent != null)
			{
				bool flag = componentInParent.Repair(repairHealth);
				HitSomething(hitInfo.collider);
				if (flag)
				{
					audio.PlayOneShot(repairSound);
				}
				// The local player's bar only: a server swing has no HUD (IngameUi is absent there).
				if (user != null && !user.aiControlled && IngameUi.instance != null)
				{
					IngameUi.instance.FlashVehicleBar(componentInParent.GetHealthRatio());
				}
			}
		}
		base.ShootMeleeRay(ray);
	}
}
