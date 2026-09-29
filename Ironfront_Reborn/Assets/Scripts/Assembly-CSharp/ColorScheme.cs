using UnityEngine;

public static class ColorScheme
{
	public static Color TeamColor(int team)
	{
		switch (team)
		{
		case 0:
			return Color.blue;
		case 1:
			return Color.red;
		default:
			return Color.Lerp(Color.gray, Color.white, 0.5f);
		}
	}

	/// <summary>
	/// The colour of a soldier's minimap icon: its team's, washed toward white a little for a bot
	/// and a little more for a player, so players stand out without losing their side.
	/// </summary>
	/// <remarks>
	/// The original washes a player 70% toward white (<c>ActorBlip.SetActor</c>), which on this
	/// map read as white, not as a team (owner report 2026-09-29). A bot keeps the original 20%.
	/// One copy, read by <see cref="ActorBlip"/> and by the networked body icons alike.
	/// </remarks>
	public static Color BlipColor(int team, bool isHuman)
	{
		return Color.Lerp(TeamColor(team), Color.white, isHuman ? 0.45f : 0.2f);
	}
}
