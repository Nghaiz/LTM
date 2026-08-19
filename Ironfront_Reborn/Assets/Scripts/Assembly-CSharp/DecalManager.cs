using System;
using System.Collections.Generic;
using Ironfront.Net.Unity.Client;
using UnityEngine;

public class DecalManager : MonoBehaviour
{
	public enum DecalType
	{
		Impact = 0,
		BloodBlue = 1,
		BloodRed = 2,
		/// <summary>
		/// An explosion's burn mark. debt-closure phase 2 task 2d, ledger C-7.
		/// </summary>
		/// <remarks>
		/// Explosions drew <see cref="Impact"/> before this — a bullet chip where a blast had
		/// been — because the enum had no scorch member, which
		/// <c>NetClientExplosionPresenter</c> recorded in a comment rather than as a gap.
		/// <b>Appended, never inserted:</b> the value is the index into
		/// <see cref="decalDrawers"/> and into the two mesh dictionaries, so renumbering the
		/// existing three would silently repaint every blood decal in the game as an impact.
		/// </remarks>
		Scorch = 3
	}

	private class MeshData
	{
		public Mesh mesh;

		public Vector3[] verts;

		public Vector3[] normals;

		public Vector2[] uvs;

		public bool meshNeedsUpdating;

		public void UpdateMesh()
		{
			if (meshNeedsUpdating)
			{
				mesh.vertices = verts;
				mesh.normals = normals;
				mesh.uv = uvs;
				meshNeedsUpdating = false;
			}
		}
	}

	private const int SCENERY_MASK = 1;

	private const int MAX_DECALS = 16383;

	private const int MAX_VERTS_PER_MESH = 65532;

	private const float MESH_BOUNDS = 9999f;

	private const float NORMAL_OFFSET = 0.03f;

	private const float MAX_SCENERY_DISTANCE = 0.2f;

	private const float BLOOD_LAUNCH_SPEED = 2f;

	public static DecalManager instance;

	public GameObject impactPrefab;

	public GameObject bloodPrefab;

	public GameObject bloodDropPrefab;

	public GameObject[] decalDrawers;

	private Dictionary<DecalType, int> vertexIndex;

	private Dictionary<DecalType, MeshData> decalMeshData;

	private ParticleSystem splatParticleSystem;

	private int maxVerts;

	private void Awake()
	{
		switch (QualitySettings.GetQualityLevel())
		{
		case 0:
			maxVerts = 800;
			break;
		case 1:
			maxVerts = 8000;
			break;
		default:
			maxVerts = 65532;
			break;
		}
		instance = this;
		splatParticleSystem = GetComponent<ParticleSystem>();
	}

	public void StartGame()
	{
		InitMeshes();
	}

	public void InitMeshes()
	{
		decalMeshData = new Dictionary<DecalType, MeshData>();
		vertexIndex = new Dictionary<DecalType, int>();
		Vector3[] array = new Vector3[maxVerts];
		Vector2[] array2 = new Vector2[maxVerts];
		Vector3[] array3 = new Vector3[maxVerts];
		int[] array4 = new int[3 * (maxVerts / 2)];
		for (int i = 0; i < maxVerts; i++)
		{
			array[i] = new Vector3(i / 2 % 2, (i + 1) / 2 % 2, 0f) * 0.0001f;
			array2[i] = new Vector2(i / 2 % 2, (i + 1) / 2 % 2);
			array3[i] = Vector3.up;
		}
		for (int j = 0; j < array4.Length / 6; j++)
		{
			array4[j * 6] = j * 4;
			array4[j * 6 + 1] = j * 4 + 1;
			array4[j * 6 + 2] = j * 4 + 2;
			array4[j * 6 + 3] = j * 4;
			array4[j * 6 + 4] = j * 4 + 2;
			array4[j * 6 + 5] = j * 4 + 3;
		}
		foreach (int value in Enum.GetValues(typeof(DecalType)))
		{
			Mesh mesh = new Mesh();
			MeshData meshData = new MeshData();
			mesh.name = string.Concat((DecalType)value, " mesh");
			meshData.mesh = mesh;
			meshData.verts = (Vector3[])array.Clone();
			meshData.uvs = (Vector2[])array2.Clone();
			meshData.normals = (Vector3[])array3.Clone();
			meshData.meshNeedsUpdating = true;
			meshData.UpdateMesh();
			mesh.triangles = array4;
			mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 9999f);
			mesh.MarkDynamic();
			// Bounds-checked rather than indexed blind. decalDrawers is an authored array and
			// phase 2 writes no prefabs or scenes (they are Phase 1's), so a build that predates
			// the authoring of Scorch's drawer has three entries for four enum members. Indexing
			// it would throw IndexOutOfRange out of StartGame and take the whole decal system
			// down; announcing the gap once and drawing that type through its fallback does not.
			if (decalDrawers == null || value >= decalDrawers.Length || decalDrawers[value] == null)
			{
				NetClientPresenterGuard.WarnOnce(
					"decal-drawer-missing:" + value,
					"[decals] DecalType." + (DecalType)value + " has no drawer in "
					+ "DecalManager.decalDrawers. Decals of that type fall back to Impact. "
					+ "Assign one on the DecalManager object.");
				continue;
			}
			GameObject gameObject = decalDrawers[value];
			MeshFilter component = gameObject.GetComponent<MeshFilter>();
			component.mesh = mesh;
			decalMeshData.Add((DecalType)value, meshData);
			vertexIndex.Add((DecalType)value, 0);
		}
	}

	public static void AddDecal(Vector3 point, Vector3 normal, float size, DecalType type)
	{
		// Every bullet and grenade impact reaches here, so it runs constantly on a headless
		// server. Returning first also skips the four raycasts in CanSpawnDecal, which are
		// pure cost when nothing will be drawn.
		if (instance == null)
		{
			return;
		}
		// decalMeshData and vertexIndex are built in StartGame() -> InitMeshes(), not Awake(),
		// so instance can be non-null with both still unset (V10 Task 9 defect 4, same shape
		// as MinimapUi defect 2).
		if (instance.decalMeshData == null || instance.vertexIndex == null)
		{
			NetClientPresenterGuard.WarnOnce(
				"decal-manager-not-ready",
				"[net] DecalManager.AddDecal ran before StartGame() built the decal mesh data. "
				+ "Skipping this decal.");
			return;
		}
		// A type with no authored drawer draws as Impact rather than not at all: a blast that
		// leaves no mark reads as the decal system being broken, where a bullet-chip-shaped one
		// reads as the cosmetic gap it is. Impact itself is index 0 and has always been authored.
		if (!instance.decalMeshData.ContainsKey(type))
		{
			type = DecalType.Impact;
		}
		Vector3 vector = Vector3.Cross(normal, UnityEngine.Random.insideUnitSphere).normalized * (size / 2f);
		Vector3 vector2 = Vector3.Cross(normal, vector);
		point += normal * 0.03f;
		bool flag = CanSpawnDecal(point, vector, vector2, normal);
		if (!flag)
		{
			vector *= 0.3f;
			vector2 *= 0.3f;
		}
		if (flag || CanSpawnDecal(point, vector, vector2, normal))
		{
			instance.PushQuad(point - vector - vector2, point + vector - vector2, point + vector + vector2, point - vector + vector2, normal, type);
		}
	}

	private static bool CanSpawnDecal(Vector3 point, Vector3 up, Vector3 right, Vector3 normal)
	{
		return Physics.Raycast(point + (-up - right) * 0.3f, -normal, 0.23f, 1) && Physics.Raycast(point + (up - right) * 0.3f, -normal, 0.23f, 1) && Physics.Raycast(point + (up + right) * 0.3f, -normal, 0.23f, 1) && Physics.Raycast(point + (-up + right) * 0.3f, -normal, 0.23f, 1);
	}

	private void Update()
	{
		if (!GameManager.instance.ingame)
		{
			return;
		}
		foreach (int value in Enum.GetValues(typeof(DecalType)))
		{
			// A type whose drawer was never authored has no mesh data. See InitMeshes.
			MeshData pending;
			if (decalMeshData.TryGetValue((DecalType)value, out pending))
			{
				pending.UpdateMesh();
			}
		}
	}

	private void PushQuad(Vector3 c1, Vector3 c2, Vector3 c3, Vector3 c4, Vector3 normal, DecalType type)
	{
		MeshData meshData = decalMeshData[type];
		int num = vertexIndex[type];
		meshData.verts[num] = c1;
		meshData.verts[num + 1] = c2;
		meshData.verts[num + 2] = c3;
		meshData.verts[num + 3] = c4;
		meshData.normals[num] = normal;
		meshData.normals[num + 1] = normal;
		meshData.normals[num + 2] = normal;
		meshData.normals[num + 3] = normal;
		meshData.meshNeedsUpdating = true;
		vertexIndex[type] = (num + 4) % maxVerts;
	}

	public static void CreateBloodDrop(Vector3 point, Vector3 baseVelocity, int team)
	{
		// Actor.Damage calls this ceil(damage / 10) times per hit, so on a server it is the
		// highest-frequency UI touch in the game.
		if (instance == null)
		{
			return;
		}
		Color color = ColorScheme.TeamColor(team);
		BloodParticle component = ((GameObject)UnityEngine.Object.Instantiate(instance.bloodDropPrefab, point, Quaternion.identity)).GetComponent<BloodParticle>();
		component.transform.localScale.Scale(Vector3.one * UnityEngine.Random.Range(2f, 3f));
		component.velocity = baseVelocity + (UnityEngine.Random.insideUnitSphere + Vector3.up) * 2f;
		component.team = team;
		component.GetComponent<Renderer>().material.color = color;
		instance.splatParticleSystem.Emit(point - baseVelocity.normalized * 0.4f, -baseVelocity * 0.3f + Vector3.up + UnityEngine.Random.insideUnitSphere, UnityEngine.Random.Range(0.7f, 3f), 0.25f, color);
	}
}
