using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The lamps of one flag or HQ in Night Mode (phase P32), laid out by the base's own plan rather
/// than at random: team-coloured lamps on the four diagonals round the flag, a lamp inside every
/// gate post, one by every ammo cache and medical station, a lantern on every watchtower, the
/// floodlight props switched on, and at an HQ a ring of lamps round its compound.
/// </summary>
/// <remarks>
/// <para>
/// <b>Owner request 2026-10-04:</b> more lamps at the flags and HQs, placed with care. Forest
/// Lake's firebases come from one blueprint (<c>ForestLakeOutposts</c>, editor), whose pieces
/// keep their names in the scene ("Outpost Dressing/Outpost - &lt;flag&gt;/Gate Post", ...); the
/// lamps are hung on those, so a base rebuilt by the blueprint is lit the same way.
/// </para>
/// <para>
/// Client only and without colliders, like the rest of the night's dressing. Every light is
/// handed back to <see cref="NightModeDirector"/>, which keeps only the nearest few lit. The
/// lamps, lanterns and beams are prefabs on the Night Mode config; the base's pieces are found by
/// the names the blueprint gives them, which is the generated bases' contract, not a hand-wired
/// reference.
/// </para>
/// </remarks>
public static class NightBaseLighting
{
	public const string OutpostRoot = "Outpost Dressing";

	private const string OutpostPrefix = "Outpost - ";

	private const float LampProbeAbove = 4f;

	private const float LampProbeDepth = 9f;

	private const float LampClearance = 0.35f;

	private const float LampClearanceFrom = 0.5f;

	private const float LampClearanceTo = 2.4f;

	private static readonly float[] FlagLampStretch = { 1f, 0.8f, 1.25f, 1.5f };

	/// <summary>Lights the base round <paramref name="point"/>; returns how many lamps and lights it set out.</summary>
	public static int Dress(NightModeConfig config, SpawnPoint point, Action<GameObject> addPiece, Action<Light, SpawnPoint> addLight)
	{
		if (config.flagLightPrefab == null || point == null)
		{
			return 0;
		}
		Vector3 flag = point.transform.position;
		Transform outpost = FindOutpost(point);
		Transform frontGate = outpost != null ? outpost.Find("Front Gate") : null;
		float frontYaw = frontGate != null ? Yaw(frontGate.position - flag) : 0f;
		int placed = 0;

		// The flag: four lamps on the diagonals, square to the base, in the colour of its holder.
		for (int i = 0; i < 4; i++)
		{
			float yaw = frontYaw + 45f + (i * 90f);
			foreach (float stretch in FlagLampStretch)
			{
				Vector3 around = flag + (Quaternion.Euler(0f, yaw, 0f) * Vector3.forward * (config.flagLightDistance * stretch));
				if (TryPlaceLamp(config, around, flag, point, addPiece, addLight))
				{
					placed++;
					break;
				}
			}
		}
		if (outpost == null)
		{
			return placed;
		}

		var gatePosts = new List<Transform>();
		foreach (Transform piece in outpost)
		{
			switch (piece.name)
			{
				case "Gate Post":
					gatePosts.Add(piece);
					break;
				case "Ammo Cache":
				case "Medical Station":
					placed += LampBeside(config, piece.position, flag, config.supplyLampOffset, addPiece, addLight) ? 1 : 0;
					break;
				case "Watchtower":
					placed += Lantern(config, piece, addLight) ? 1 : 0;
					break;
				case "Floodlight":
					placed += SwitchOn(config, piece, addLight) ? 1 : 0;
					break;
			}
		}
		// Inside every gate post, leaning over the gateway.
		foreach (Transform post in gatePosts)
		{
			Vector3 inward = flag - post.position;
			inward.y = 0f;
			Vector3 around = post.position + (inward.normalized * config.gateLampInset);
			placed += TryPlaceLamp(config, around, post.position, null, addPiece, addLight) ? 1 : 0;
		}
		// An HQ keeps its authored compound and has no gates of the blueprint's: ring it with lamps.
		if (frontGate == null && point is CapturePoint headquarters)
		{
			float radius = headquarters.captureRange + config.headquartersLampMargin;
			for (int i = 0; i < config.headquartersLamps; i++)
			{
				float yaw = frontYaw + (i * 360f / config.headquartersLamps);
				Vector3 around = flag + (Quaternion.Euler(0f, yaw, 0f) * Vector3.forward * radius);
				placed += TryPlaceLamp(config, around, flag, null, addPiece, addLight) ? 1 : 0;
			}
		}
		return placed;
	}

	/// <summary>The blueprint's group for a flag, or null on a map built by hand.</summary>
	private static Transform FindOutpost(SpawnPoint point)
	{
		GameObject root = GameObject.Find(OutpostRoot);
		if (root == null)
		{
			return null;
		}
		string name = OutpostPrefix + point.name.Replace(" Capture Point", string.Empty);
		return root.transform.Find(name);
	}

	private static float Yaw(Vector3 direction) => Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;

	private static bool LampBeside(NightModeConfig config, Vector3 subject, Vector3 flag, float offset, Action<GameObject> addPiece, Action<Light, SpawnPoint> addLight)
	{
		Vector3 toFlag = flag - subject;
		toFlag.y = 0f;
		Vector3 side = new Vector3(toFlag.z, 0f, -toFlag.x).normalized;
		// Toward the flag first, where the players stand; then to either side of it.
		foreach (Vector3 step in new[] { toFlag.normalized, (toFlag.normalized + side).normalized, (toFlag.normalized - side).normalized })
		{
			if (TryPlaceLamp(config, subject + (step * offset), subject, null, addPiece, addLight))
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>A lamp on the ground at <paramref name="around"/>, its head leaning toward <paramref name="lookAt"/>.</summary>
	private static bool TryPlaceLamp(NightModeConfig config, Vector3 around, Vector3 lookAt, SpawnPoint colouredBy, Action<GameObject> addPiece, Action<Light, SpawnPoint> addLight)
	{
		if (!TryLampGround(around, out Vector3 at))
		{
			return false;
		}
		Vector3 facing = lookAt - at;
		facing.y = 0f;
		GameObject lamp = UnityEngine.Object.Instantiate(config.flagLightPrefab, at,
			facing.sqrMagnitude > 0.01f ? Quaternion.LookRotation(facing) : Quaternion.identity);
		addPiece(lamp);
		foreach (Light light in lamp.GetComponentsInChildren<Light>(true))
		{
			if (colouredBy == null)
			{
				light.color = config.lampLight;
			}
			addLight(light, colouredBy);
		}
		return true;
	}

	/// <summary>
	/// Ground a lamp's stand can take: any level surface just above or below the terrain (a
	/// concrete pad as well as grass), dry, with nothing in the pole's way.
	/// </summary>
	private static bool TryLampGround(Vector3 around, out Vector3 at)
	{
		at = around;
		Terrain terrain = Terrain.activeTerrain;
		float top = terrain != null ? terrain.SampleHeight(around) + terrain.GetPosition().y : around.y;
		Vector3 origin = new Vector3(around.x, top + LampProbeAbove, around.z);
		if (!Physics.Raycast(origin, Vector3.down, out RaycastHit hit, LampProbeDepth, ~0, QueryTriggerInteraction.Ignore)
			|| hit.normal.y < 0.85f || WaterLevel.Depth(hit.point) > -0.3f)
		{
			return false;
		}
		Vector3 bottom = hit.point + (Vector3.up * LampClearanceFrom);
		Vector3 upper = hit.point + (Vector3.up * LampClearanceTo);
		if (Physics.CheckCapsule(bottom, upper, LampClearance, ~0, QueryTriggerInteraction.Ignore))
		{
			return false;
		}
		at = hit.point;
		return true;
	}

	/// <summary>A lantern hung under a watchtower's roof, over its platform.</summary>
	private static bool Lantern(NightModeConfig config, Transform tower, Action<Light, SpawnPoint> addLight)
	{
		if (!TryBounds(tower, out Bounds bounds))
		{
			return false;
		}
		Vector3 at = new Vector3(bounds.center.x, bounds.min.y + (bounds.size.y * config.towerLanternHeight), bounds.center.z);
		return Hang(config.towerLanternPrefab, tower, at, Quaternion.identity, addLight);
	}

	/// <summary>A floodlight prop's lamp, lit: a wide cone down from the top of the prop.</summary>
	private static bool SwitchOn(NightModeConfig config, Transform floodlight, Action<Light, SpawnPoint> addLight)
	{
		if (!TryBounds(floodlight, out Bounds bounds))
		{
			return false;
		}
		Vector3 at = new Vector3(bounds.center.x, bounds.max.y - 0.3f, bounds.center.z);
		return Hang(config.floodlightBeamPrefab, floodlight, at, Quaternion.LookRotation(Vector3.down, floodlight.forward), addLight);
	}

	/// <summary>A light prefab hung on a base's piece; it goes with the piece.</summary>
	private static bool Hang(GameObject prefab, Transform piece, Vector3 at, Quaternion rotation, Action<Light, SpawnPoint> addLight)
	{
		if (prefab == null)
		{
			return false;
		}
		GameObject hung = UnityEngine.Object.Instantiate(prefab, at, rotation, piece);
		Light[] lights = hung.GetComponentsInChildren<Light>(true);
		foreach (Light light in lights)
		{
			addLight(light, null);
		}
		return lights.Length > 0;
	}

	private static bool TryBounds(Transform piece, out Bounds bounds)
	{
		bounds = default;
		bool any = false;
		foreach (Renderer renderer in piece.GetComponentsInChildren<Renderer>(true))
		{
			if (!any)
			{
				bounds = renderer.bounds;
				any = true;
			}
			else
			{
				bounds.Encapsulate(renderer.bounds);
			}
		}
		return any;
	}
}
