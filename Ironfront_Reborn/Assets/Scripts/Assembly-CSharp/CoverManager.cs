using System.Collections.Generic;
using UnityEngine;

public class CoverManager : MonoBehaviour
{
	private const float MAX_COVER_DISTANCE = 50f;

	public static CoverManager instance;

	private CoverPoint[] coverPoints;

	public void Awake()
	{
		instance = this;
	}

	public void StartGame()
	{
		coverPoints = Object.FindObjectsOfType<CoverPoint>();
	}

	public CoverPoint ClosestVacant(Vector3 point)
	{
		CoverPoint result = null;
		float num = 50f;
		CoverPoint[] array = coverPoints;
		foreach (CoverPoint coverPoint in array)
		{
			if (!coverPoint.taken)
			{
				float num2 = Vector3.Distance(coverPoint.transform.position, point);
				if (num2 < num)
				{
					num = num2;
					result = coverPoint;
				}
			}
		}
		return result;
	}

	/// <summary>
	/// The vacant points within <paramref name="radius"/> of <paramref name="point"/>, nearest
	/// first, at most <paramref name="max"/>, into <paramref name="result"/> (cleared first).
	/// Phase P28: the candidates <see cref="BotCover"/> judges against the actual enemy.
	/// </summary>
	public void NearestVacant(Vector3 point, float radius, List<CoverPoint> result, int max)
	{
		result.Clear();
		if (coverPoints == null || max <= 0)
		{
			return;
		}
		if (nearestDistances.Length < max)
		{
			nearestDistances = new float[max];
		}
		float limit = radius * radius;
		foreach (CoverPoint coverPoint in coverPoints)
		{
			if (coverPoint == null || coverPoint.taken)
			{
				continue;
			}
			float distance = (coverPoint.transform.position - point).sqrMagnitude;
			if (distance > limit || (result.Count == max && distance >= nearestDistances[max - 1]))
			{
				continue;
			}
			if (result.Count == max)
			{
				result.RemoveAt(max - 1);
			}
			int at = result.Count;
			while (at > 0 && nearestDistances[at - 1] > distance)
			{
				nearestDistances[at] = nearestDistances[at - 1];
				at--;
			}
			nearestDistances[at] = distance;
			result.Insert(at, coverPoint);
		}
	}

	private float[] nearestDistances = new float[0];

	public CoverPoint ClosestVacantCoveringDirection(Vector3 point, Vector3 direction)
	{
		CoverPoint result = null;
		float num = 50f;
		Vector3 normalized = new Vector3(direction.x, 0f, direction.z).normalized;
		CoverPoint[] array = coverPoints;
		foreach (CoverPoint coverPoint in array)
		{
			if (!coverPoint.taken && coverPoint.CoversDirection(normalized))
			{
				float num2 = Vector3.Distance(coverPoint.transform.position, point);
				if (num2 < num)
				{
					num = num2;
					result = coverPoint;
				}
			}
		}
		return result;
	}
}
