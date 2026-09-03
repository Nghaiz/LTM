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

	/// <summary>Container children already reported for their ground-snap outcome, so each warns once.</summary>
	private static readonly HashSet<string> _warnedContainerChildren = new HashSet<string>();

	/// <summary>
	/// How far a container-authored child may sit from its ground-snapped position before the
	/// correction is worth a warning. Small mesh irregularities correct by centimetres; this
	/// keeps those silent while still catching an authored point that is metres off the ground.
	/// </summary>
	private const float ContainerSnapWarnDistanceMetres = 1f;

	public Vector3 RandomSpawnPointPosition()
	{
		int childCount = spawnpointContainer.childCount;
		if (childCount == 0)
		{
			return RandomPosition();
		}
		return SnappedContainerChildPosition(spawnpointContainer.GetChild(Random.Range(0, childCount)));
	}

	/// <summary>
	/// Ground-snaps an authored container child. Ledger X-81 closed only the non-container
	/// branch of <see cref="GetSpawnPosition"/> -- this method used to return
	/// <c>spawnpointContainer.GetChild(...).position</c> verbatim, with no snap and no warning,
	/// which is how an authored capture-point child at y=103.5 placed players 42.6 m in the air
	/// on Dustbowl. Reuses <see cref="Ironfront.Net.Unity.GroundSnap"/> rather than
	/// re-implementing the raycast rule <see cref="RandomPosition"/> already applies to the
	/// jittered path.
	/// </summary>
	private Vector3 SnappedContainerChildPosition(Transform child)
	{
		Vector3 authored = child.position;

		if (!Ironfront.Net.Unity.GroundSnap.TrySnap(authored, out Vector3 grounded))
		{
			// Same fallback RandomPosition() uses on a miss: hand back the authored point,
			// loudly, rather than a silently unsnapped placement. Keyed per-child (not per
			// SpawnPoint name) because one container holds several independently authored
			// children, and warned once each for the same "sixty times a second is the same
			// as never" reason RandomPosition() documents.
			if (_warnedContainerChildren.Add(name + "/" + child.name + "#miss"))
			{
				Debug.LogWarning(
					$"[spawn] '{name}' child '{child.name}' at {authored} found no ground "
					+ $"within {Ironfront.Net.Unity.GroundSnap.MaxDistanceMetres} m below its "
					+ "ray start, so bodies are being placed at the authored point un-snapped "
					+ "and may be standing in mid-air. Either the point is too far above its "
					+ "terrain, or the ground under it is on a layer the spawn mask excludes. "
					+ "See ledger X-81.");
			}
			return authored;
		}

		float correction = Vector3.Distance(authored, grounded);
		if (correction > ContainerSnapWarnDistanceMetres
			&& _warnedContainerChildren.Add(name + "/" + child.name + "#corrected"))
		{
			Debug.LogWarning(
				$"[spawn] '{name}' child '{child.name}' authored at {authored} was "
				+ $"{correction:F1} m off the ground and has been snapped to {grounded}. That "
				+ "authored position is a scene defect the level author should fix. See ledger "
				+ "X-81.");
		}

		return grounded;
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
