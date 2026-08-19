using System.Collections.Generic;
using Ironfront.Net.Unity;
using UnityEngine;

public class Ammobox : Projectile
{
	public new const string name = "AMMO BAG";

	private const float RESUPPLY_RATE = 3f;

	private const float RESUPPLY_RANGE = 6f;

	// Reused across every pulse of every bag, for the reason ActorManager.ActorsInRange's
	// buffer overload exists: a fresh List per three-second pulse per deployable is a steady
	// GC drip for as long as the bag is on the ground. V7 task 7.
	private static readonly List<Actor> _nearby = new List<Actor>();

	private void Awake()
	{
		// A thrown bag is not incoming fire, and the base Start used to tell the enemy team it
		// was: ActorManager.RegisterProjectile raycasts 9999 m and walks every alive enemy to
		// warn their AI. V7 task 3.
		warnsEnemyAi = false;

		Rigidbody component = GetComponent<Rigidbody>();
		component.linearVelocity = base.transform.forward * configuration.speed;

		// The pulse is authoritative state -- Actor.ResupplyAmmo fills spareAmmo[5]. On a
		// network the server owns it and drives it through ServerDeployableAuthority on a
		// tick-counted timer; a client running this would move a number phase-05 D5 and D9 put
		// on the server. Offline keeps the wall-clock repeat exactly as it was (V7-D11).
		if (NetContext.IsOffline)
		{
			InvokeRepeating(nameof(Resupply), 3f, 3f);
		}
	}

	private void Resupply()
	{
		ActorManager.AliveActorsInRange(base.transform.position, 6f, _nearby);
		for (int i = 0; i < _nearby.Count; i++)
		{
			_nearby[i].ResupplyAmmo();
		}
	}

	protected override void Update()
	{
		if (Time.time > expireTime)
		{
			Object.Destroy(base.gameObject);
		}
	}
}
