using System.Collections.Generic;
using Ironfront.Net.Unity;
using UnityEngine;

public class Medipack : Projectile
{
	public new const string name = "MEDIPACK";

	private const float RESUPPLY_RATE = 3f;

	private const float RESUPPLY_RANGE = 6f;

	public float reducedLifetimePerResupply = 5f;

	// See Ammobox for why this buffer is shared and static. V7 task 7.
	private static readonly List<Actor> _nearby = new List<Actor>();

	private void Awake()
	{
		// Not incoming fire. See Ammobox.Awake. V7 task 3.
		warnsEnemyAi = false;

		Rigidbody component = GetComponent<Rigidbody>();
		component.linearVelocity = base.transform.forward * configuration.speed;

		// Actor.ResupplyHealth writes health directly, which is the single most
		// clearly-authoritative number in the game. On a network the server owns the pulse and
		// routes the heal through IActorDamageSink.ApplyHeal, so that health still has exactly
		// one writer (phase-05 D9). Offline is unchanged (V7-D11).
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
			if (_nearby[i].ResupplyHealth())
			{
				// The one lifetime in the game no client can predict, and therefore the reason
				// S_PROJECTILE_SPAWN carries a remaining-lifetime byte at all (V7-D8).
				expireTime -= reducedLifetimePerResupply;
			}
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
