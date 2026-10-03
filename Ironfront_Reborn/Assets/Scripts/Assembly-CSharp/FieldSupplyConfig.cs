using System;
using UnityEngine;

/// <summary>
/// What a map scatters each match (phase P32): a vehicle at every flag that is not an HQ, a few
/// vehicles out in the field, and ammunition and medical crates; how far out, how far apart, and
/// how often each kind comes up. Read by <see cref="FieldSupplyDirector"/> from
/// <c>Resources/FieldSupply/&lt;scene name&gt;</c>; a map with no such asset scatters nothing.
/// </summary>
/// <remarks>
/// Data rather than code so a map opts in, and is tuned, without a code change: Dustbowl and Island
/// have no asset and play exactly as before.
/// </remarks>
[CreateAssetMenu(menuName = "Ironfront/Field supply config")]
public sealed class FieldSupplyConfig : ScriptableObject
{
	/// <summary>One kind of vehicle and how often it is chosen against the others in its list.</summary>
	[Serializable]
	public struct VehicleChoice
	{
		public GameObject prefab;

		public float weight;
	}

	/// <summary>
	/// The pad each scattered vehicle stands on: a prefab saved inactive whose root carries a
	/// <see cref="VehicleSpawner"/>, filled in and woken by <see cref="FieldSupplyDirector"/>.
	/// </summary>
	public GameObject padPrefab;

	[Header("A vehicle at every flag that is not an HQ")]
	public VehicleChoice[] flagVehicles = new VehicleChoice[0];

	/// <summary>Metres from the flag the parking place may be: outside a firebase's walls, inside its reach.</summary>
	public float flagRingInner = 34f;

	public float flagRingOuter = 62f;

	/// <summary>
	/// A flag with no dry place in reach -- Forest Lake's Island -- gets one of these on the water
	/// beside it instead: bots never board a boat, but a player can.
	/// </summary>
	public VehicleChoice[] waterVehicles = new VehicleChoice[0];

	/// <summary>Seconds before a flag's vehicle is replaced once wrecked.</summary>
	public float flagRespawnSeconds = 25f;

	[Header("Vehicles out in the field")]
	public VehicleChoice[] fieldVehicles = new VehicleChoice[0];

	public int fieldVehicleCount = 2;

	/// <summary>Metres a field vehicle keeps from every flag, so it is a find and not a flag's second vehicle.</summary>
	public float fieldMinFromFlag = 110f;

	/// <summary>Metres between any two things this config places.</summary>
	public float spacing = 120f;

	/// <summary>Seconds after a field vehicle is wrecked before another appears somewhere else.</summary>
	public float fieldRespawnSeconds = 45f;

	/// <summary>
	/// The ammunition crate: a deployable (an <see cref="Ammobox"/> with a <see cref="FieldCrate"/>),
	/// so the server replicates it as it does a dropped bag and it resupplies both sides.
	/// </summary>
	[Header("Supply crates in the field")]
	public GameObject ammoCratePrefab;

	/// <summary>The medical crate: a <see cref="Medipack"/> with a <see cref="FieldCrate"/>.</summary>
	public GameObject medicalCratePrefab;

	/// <summary>Crates out at once; each lasts its prefab's lifetime and is then replaced somewhere else.</summary>
	public int crateCount = 10;

	/// <summary>The share of crates that are medical rather than ammunition.</summary>
	public float medicalShare = 0.4f;

	/// <summary>Metres a crate keeps from every flag: the flags have caches of their own.</summary>
	public float crateMinFromFlag = 50f;

	/// <summary>Metres between two crates.</summary>
	public float crateSpacing = 90f;

	/// <summary>Seconds after a crate runs out before its replacement turns up.</summary>
	public float crateRespawnSeconds = 20f;

	/// <summary>Steepest ground a vehicle is parked on, in degrees.</summary>
	[Header("Parking")]
	public float maxSlopeDegrees = 11f;

	/// <summary>Largest height difference under the footprint's corners, in metres.</summary>
	public float maxStep = 0.7f;

	/// <summary>Metres a parking place keeps from the nearest terrain tree.</summary>
	public float treeClearance = 3.5f;
}
