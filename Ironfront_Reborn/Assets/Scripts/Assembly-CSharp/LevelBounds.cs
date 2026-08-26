using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.World;
using UnityEngine;

/// <summary>
/// The authored box a match is played inside. Ledger <b>E-6</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This had zero callers, and the consequence was not a wall.</b> A body past the wire's
/// ±2048 m is still simulated by the server at its true position, while <c>Quantize.PackPos</c>
/// clamps every snapshot to the boundary — so the server and all its clients disagree
/// permanently, with no exception and nothing in a log. Dustbowl's two respawning helicopters
/// reach it in well under a minute of level flight, and what a player reports is "the helicopter
/// broke".
/// </para>
/// <para>
/// <b>The decision (E-6, phase 6 task 6.2): clamp, server-side, and count it.</b> The
/// alternatives were considered and rejected. <i>Damage</i> kills a vehicle for crossing a line
/// the player cannot see, and turns a replication problem into a combat outcome. <i>An
/// authoritative teleport</i> is a discontinuity every observer renders as a warp, and it needs
/// its own wire story — a teleport bit, so interpolation does not sweep the body across the map
/// — which is a protocol change for a boundary case. <i>Clamping</i> is the least intervention
/// that makes the wire honest: the server's own position stops diverging from the one every
/// client can be told, and the crossing becomes a counted, logged event instead of a silent
/// permanent rubber-band.
/// </para>
/// <para>
/// <b>Stated plainly, because it IS a gameplay change:</b> Dustbowl's box is 700 m tall centred
/// at y = 207.6, so a helicopter now has a ceiling near y = 557.6 and invisible walls near
/// ±850/±800 m horizontally. That was always true of the wire; the difference is that it now
/// happens where the server can see it, rather than in the encoder where nobody could.
/// </para>
/// <para>
/// <b>The containment rule lives in <see cref="PlayVolume"/>, not here.</b> This class compiles
/// into <c>Assembly-CSharp</c>, which no test assembly can reference (<b>E-11b</b>), so
/// arithmetic written here could only ever be graded by eye. What stays here is the authored
/// box; the rules are in the library, under test.
/// </para>
/// </remarks>
public class LevelBounds : MonoBehaviour
{
	public static LevelBounds instance;

	private PlayVolume volume;

	/// <summary>
	/// How many times a body has been pulled back inside since the scene loaded.
	/// </summary>
	/// <remarks>
	/// The counter is the half of this that makes E-6 closed rather than merely handled. The
	/// quantizer has been clamping all along; what was missing was any record that it happened.
	/// Read it from a diagnostics overlay or a server log line — a non-zero value means bodies
	/// are reaching the edge of the play area, which is a level-design fact worth knowing.
	/// </remarks>
	public static int ClampCount { get; private set; }

	/// <summary>
	/// True when <paramref name="point"/> is inside the authored box.
	/// </summary>
	/// <remarks>
	/// <b>No instance means "inside", and that is a deliberate, documented fallback</b> rather
	/// than a silent one: <c>Menu</c> has no <c>LevelBounds</c> and nothing there moves. A scene
	/// that DOES simulate bodies and forgets the volume gets no protection — which is why the
	/// value of this method is the caller in <c>Vehicle.FixedUpdate</c>, pinned by gate rule
	/// <b>G9</b>, and not the method itself.
	/// </remarks>
	public static bool IsInside(Vector3 point)
	{
		if (instance == null)
		{
			return true;
		}
		return instance.volume.Contains(new Vec3(point.x, point.y, point.z));
	}

	/// <summary>
	/// Pulls <paramref name="point"/> back to the nearest point inside, counting the crossing.
	/// </summary>
	/// <returns>True when the point was outside and <paramref name="clamped"/> differs.</returns>
	public static bool ClampInside(Vector3 point, out Vector3 clamped)
	{
		clamped = point;

		if (instance == null)
		{
			return false;
		}

		if (!instance.volume.TryClamp(new Vec3(point.x, point.y, point.z), out Vec3 inside))
		{
			return false;
		}

		clamped = new Vector3(inside.X, inside.Y, inside.Z);
		ClampCount++;
		return true;
	}

	private void SetupBounds()
	{
		instance = this;
		volume = new PlayVolume(
			new Vec3(base.transform.position.x, base.transform.position.y, base.transform.position.z),
			new Vec3(base.transform.localScale.x, base.transform.localScale.y, base.transform.localScale.z));

		// Clamping to an authored box only keeps bodies encodable if the box is itself inside
		// the wire's range. Today Dustbowl's is, by a wide margin -- but nothing said so, and
		// widening it past 2048 m would reintroduce the exact silent divergence this closes
		// while every other check still passed.
		if (!volume.FitsOnTheWire)
		{
			Debug.LogError(
				$"[bounds] the authored LevelBounds volume ({volume.Min} .. {volume.Max}) reaches "
				+ "past the wire's +/-2048 m position range. Positions outside it are clamped "
				+ "silently by the snapshot encoder, so bodies there desync permanently. Shrink "
				+ "the volume, or widen Quantize.POS_MIN/POS_MAX and bump the protocol.");
		}
	}

	private void Awake()
	{
		SetupBounds();
		ClampCount = 0;
		GetComponent<Renderer>().enabled = false;
	}
}
