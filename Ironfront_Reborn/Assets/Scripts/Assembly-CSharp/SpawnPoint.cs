using System.Collections.Generic;
using UnityEngine;

public class SpawnPoint : MonoBehaviour
{
	public int owner = -1;

	public int maxSquadSize = 5;

	public List<SpawnPoint> adjacentSpawnPoints;

	public Transform spawnpointContainer;

	protected virtual void Awake()
	{
		if (spawnpointContainer != null)
		{
			Renderer[] componentsInChildren = spawnpointContainer.GetComponentsInChildren<Renderer>();
			Renderer[] array = componentsInChildren;
			foreach (Renderer renderer in array)
			{
				renderer.enabled = false;
			}
		}
	}

	public virtual Vector3 GetSpawnPosition()
	{
		if (spawnpointContainer == null)
		{
			return RandomPosition();
		}
		return RandomSpawnPointPosition();
	}

	/// <summary>Spawn points already reported as unsnappable, so the warning is once each.</summary>
	private static readonly HashSet<string> _warnedUnsnappable = new HashSet<string>();

	/// <summary>
	/// A point on the ground within a few metres of this spawn point.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Ledger X-81.</b> This snapped with an unmasked, unbounded downward ray and returned
	/// the un-snapped point in silence when it missed -- three faults, all invisible from the
	/// outside because every outcome is a plausible-looking <c>Vector3</c>. The rule itself now
	/// lives in <see cref="Ironfront.Net.Unity.GroundSnap"/>, which a test can reach; this type
	/// cannot be referenced by any test assembly, because it compiles into
	/// <c>Assembly-CSharp</c>.
	/// </para>
	/// <para>
	/// <b>What this does NOT claim.</b> It is not the cause of bodies falling out of the world
	/// (X-75/X-82) and is deliberately not filed as one: a faller in <c>p4-pointblank-01</c> was
	/// placed at the exact modal height and fell anyway, and <c>p4-grenade-01</c>'s driver stood
	/// at y = 9.078 for five seconds before it began descending. Two defects at one address, and
	/// conflating them once already cost an investigation.
	/// </para>
	/// </remarks>
	public Vector3 RandomPosition()
	{
		Vector3 authored = base.transform.position;
		Vector3 jittered = authored + Vector3.Scale(Random.insideUnitSphere, new Vector3(3f, 0f, 3f));

		if (Ironfront.Net.Unity.GroundSnap.TrySnap(jittered, out Vector3 grounded))
		{
			return grounded;
		}

		// LOUD, and once per spawn point rather than per placement: this runs on every respawn,
		// and a warning that repeats sixty times a second is filtered out as noise, which is the
		// same as not warning at all.
		//
		// The AUTHORED point is returned rather than the jittered one. Both are guesses, but the
		// authored point is where a level designer deliberately put a spawn, while the jittered
		// one is that guess plus up to three metres of randomness in a direction nothing checked.
		if (_warnedUnsnappable.Add(name))
		{
			Debug.LogWarning(
				$"[spawn] '{name}' at {authored} found no ground within "
				+ $"{Ironfront.Net.Unity.GroundSnap.MaxDistanceMetres} m below its ray start, so "
				+ "bodies are being placed at the authored point un-snapped and may be standing "
				+ "in mid-air. Either the point is too far above its terrain, or the ground under "
				+ "it is on a layer the spawn mask excludes. See ledger X-81.");
		}

		return authored;
	}

	public Vector3 RandomSpawnPointPosition()
	{
		int childCount = spawnpointContainer.childCount;
		if (childCount == 0)
		{
			return RandomPosition();
		}
		return spawnpointContainer.GetChild(Random.Range(0, childCount)).position;
	}

	public virtual bool IsSafe()
	{
		return false;
	}

	public virtual float GotoRadius()
	{
		return 5f;
	}

	public virtual bool IsFrontLine()
	{
		foreach (SpawnPoint adjacentSpawnPoint in adjacentSpawnPoints)
		{
			if (adjacentSpawnPoint.owner != owner)
			{
				return true;
			}
		}
		return false;
	}
}
