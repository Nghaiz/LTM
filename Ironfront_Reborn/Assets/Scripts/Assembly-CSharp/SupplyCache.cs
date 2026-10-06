using System.Collections.Generic;
using Ironfront.Net.Unity;
using UnityEngine;

/// <summary>What a <see cref="SupplyCache"/> hands out.</summary>
public enum SupplyKind
{
	Ammo,
	Medical,
}

/// <summary>
/// A fixed ammo dump or medical station at a flag or HQ: every few seconds it refills the spare
/// ammunition, or heals, every living soldier of the side holding that flag who stands next to it,
/// player or bot.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists (owner request 2026-10-03).</b> Holding a flag gave a spawn point and a score
/// multiplier and nothing else, so there was little reason to fight over one. A cache that serves
/// only the holder makes every flag a resupply point worth taking and denying.
/// </para>
/// <para>
/// <b>Authoritative where health and ammunition are.</b> It pulses offline and on a server, never
/// on a networked client: the client sees the result through replicated health and spare rounds.
/// A networked player's spare rounds live in the server's pool, reached through
/// <see cref="NetResupply"/>; a bot's live on its <see cref="Actor"/>, as offline.
/// </para>
/// <para>
/// <b>Listed for everyone, pulsing only where it may.</b> <see cref="All"/> is kept from Awake to
/// OnDestroy, so a client's radar can draw the caches and a server's squads can walk to them, while
/// the pulse itself is switched off on a client.
/// </para>
/// </remarks>
public class SupplyCache : MonoBehaviour
{
	public SupplyKind kind;

	/// <summary>The flag whose holder this serves. A neutral flag serves no one.</summary>
	public SpawnPoint point;

	/// <summary>
	/// Metres from the cache's centre a soldier must stand within, measured across the ground (see
	/// <see cref="Reaches"/>): the one range every ammunition and health source shares.
	/// </summary>
	/// <remarks>
	/// A cache is a module of crates up to 2.9 m from its centre (measured on Forest Lake's 24,
	/// 2026-10-06), so 5 m still covers a soldier standing against the far crate of a stack. It
	/// was 8 m after the v4.3.0 playtest (#553); the owner ruled on 2026-10-06 that a soldier must
	/// stand close to refill, 5 m and no more, and the scenes' authored 6 m no longer counts.
	/// </remarks>
	public const float Reach = Ironfront.Net.Replication.Projectiles.ServerDeployableAuthority.ResupplyRange;

	/// <summary>Metres above or below a cache a soldier may stand and still be served: a floor, not a storey.</summary>
	public const float VerticalReach = 3f;

	/// <summary>Seconds between pulses: an ammo bag's rate.</summary>
	public float interval = 3f;

	private static readonly List<SupplyCache> all = new List<SupplyCache>();

	private static readonly List<Actor> nearby = new List<Actor>();

	/// <summary>Seconds between the one-line summary of what every cache handed out.</summary>
	private const float ReportSeconds = 60f;

	private static int playerRefills;

	private static int botRefills;

	private static int heals;

	private static float nextReport;

	private float nextPulse;

	/// <summary>Every cache in the loaded level.</summary>
	public static IReadOnlyList<SupplyCache> All => all;

	/// <summary>The team this cache serves now, or -1 while its flag is neutral.</summary>
	public int ServedTeam => point != null ? point.owner : -1;

	/// <summary>
	/// The nearest cache serving <paramref name="team"/> that hands out what is asked for, within
	/// <paramref name="maxDistance"/> metres of <paramref name="from"/>; null when there is none.
	/// </summary>
	public static SupplyCache Nearest(Vector3 from, int team, bool ammo, bool medical, float maxDistance)
	{
		SupplyCache best = null;
		float bestSquared = maxDistance * maxDistance;
		for (int i = 0; i < all.Count; i++)
		{
			SupplyCache cache = all[i];
			if (cache == null || cache.ServedTeam != team || team < 0)
			{
				continue;
			}
			if (!(cache.kind == SupplyKind.Ammo ? ammo : medical))
			{
				continue;
			}
			float squared = (cache.transform.position - from).sqrMagnitude;
			if (squared < bestSquared)
			{
				bestSquared = squared;
				best = cache;
			}
		}
		return best;
	}

	private void Awake()
	{
		all.Add(this);
	}

	private void OnDestroy()
	{
		all.Remove(this);
	}

	private void Start()
	{
		// A networked client only draws the crates; the pulse is the server's.
		if (NetContext.IsClient)
		{
			enabled = false;
			return;
		}
		nextPulse = Time.time + interval;
	}

	private void Update()
	{
		Report();
		if (Time.time < nextPulse)
		{
			return;
		}
		nextPulse = Time.time + interval;
		int holder = ServedTeam;
		if (holder < 0)
		{
			return;
		}
		nearby.Clear();
		foreach (Actor candidate in ActorManager.instance.actors)
		{
			if (candidate != null && !candidate.dead && Reaches(candidate.Position()))
			{
				nearby.Add(candidate);
			}
		}
		for (int i = 0; i < nearby.Count; i++)
		{
			Actor actor = nearby[i];
			if (actor == null || actor.dead || actor.team != holder)
			{
				continue;
			}
			if (kind == SupplyKind.Medical)
			{
				if (actor.ResupplyHealth())
				{
					heals++;
				}
			}
			else if (NetResupply.TryGiveAmmo(actor.gameObject, out int rounds))
			{
				if (rounds > 0)
				{
					playerRefills++;
				}
			}
			else if (RefillOnActor(actor))
			{
				botRefills++;
			}
		}
		nearby.Clear();
	}

	/// <summary>
	/// Whether a soldier standing at <paramref name="position"/> is served: within
	/// <see cref="Reach"/> across the ground, on roughly the cache's own floor.
	/// </summary>
	public bool Reaches(Vector3 position)
	{
		Vector3 offset = position - base.transform.position;
		return Mathf.Abs(offset.y) <= VerticalReach && offset.x * offset.x + offset.z * offset.z <= Reach * Reach;
	}

	/// <summary>The cache whose reach covers <paramref name="position"/>, nearest first; null when none does.</summary>
	public static SupplyCache Reaching(Vector3 position)
	{
		SupplyCache best = null;
		float bestSquared = float.MaxValue;
		for (int i = 0; i < all.Count; i++)
		{
			SupplyCache cache = all[i];
			if (cache == null || !cache.Reaches(position))
			{
				continue;
			}
			float squared = (cache.transform.position - position).sqrMagnitude;
			if (squared < bestSquared)
			{
				bestSquared = squared;
				best = cache;
			}
		}
		return best;
	}

	/// <summary>The flag's name as the HUD writes it: "QUARRY" for "Quarry Capture Point".</summary>
	public string FlagName => point != null ? point.name.Replace(" Capture Point", string.Empty).ToUpperInvariant() : string.Empty;

	/// <summary>Refills a body whose rounds live on its <see cref="Actor"/>; true when any slot rose.</summary>
	private static bool RefillOnActor(Actor actor)
	{
		int before = 0;
		for (int i = 0; i < actor.spareAmmo.Length; i++)
		{
			before += actor.spareAmmo[i];
		}
		actor.ResupplyAmmo();
		int after = 0;
		for (int i = 0; i < actor.spareAmmo.Length; i++)
		{
			after += actor.spareAmmo[i];
		}
		return after > before;
	}

	/// <summary>
	/// Once a minute, where caches pulse: how many player and bot ammo refills and heals they gave.
	/// The server log is the only place a cache's work shows (a client sees replicated numbers
	/// only), and nothing is logged for a minute in which no cache gave anything.
	/// </summary>
	private static void Report()
	{
		if (Time.time < nextReport)
		{
			return;
		}
		if (nextReport > 0f && playerRefills + botRefills + heals > 0)
		{
			Debug.Log($"[supply] last {ReportSeconds:0} s: {playerRefills} player and {botRefills} bot ammo refill(s), {heals} heal(s).");
		}
		playerRefills = 0;
		botRefills = 0;
		heals = 0;
		nextReport = Time.time + ReportSeconds;
	}

	private void OnDrawGizmosSelected()
	{
		Gizmos.color = kind == SupplyKind.Medical ? Color.red : Color.green;
		Gizmos.DrawWireSphere(base.transform.position, Reach);
	}
}
