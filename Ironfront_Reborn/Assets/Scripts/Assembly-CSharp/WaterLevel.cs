using UnityEngine;

public class WaterLevel : MonoBehaviour
{
	public static float height;

	// The instance whose surface MovementCore swims against, so a map torn down after the next one
	// has loaded cannot take the new map's water away with it.
	private static WaterLevel published;

	public static bool InWater(Vector3 position)
	{
		return position.y <= height;
	}

	public static float Depth(Vector3 position)
	{
		return height - position.y;
	}

	private void Awake()
	{
		height = base.transform.position.y;
		// The movement a player's client predicts and its server replays swims against this same
		// surface (2026-09-29): a water line the two disagreed about would mispredict every tick.
		published = this;
		Ironfront.Net.Replication.Movement.MovementCore.WaterHeight = height;
	}

	private void OnDestroy()
	{
		if (published != this)
		{
			return;
		}
		published = null;
		Ironfront.Net.Replication.Movement.MovementCore.WaterHeight = float.NegativeInfinity;
	}
}
