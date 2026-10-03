using Ironfront.Net.Protocol;
using Ironfront.Net.Unity;
using UnityEngine;

/// <summary>
/// How the bots fight at night (phase P32 Night Mode): what the dark changes about seeing, and
/// the goggles they carry.
/// </summary>
/// <remarks>
/// <para>
/// <b>Seeing.</b> The original's bots already see by the fog (<c>AiActorController.CanSeeActor</c>:
/// a sighting scaled by exp(-(r*fog)^2)), so the night's fog alone halves how far they spot a man.
/// The dark changes two things on top. A muzzle flash is a light in it: a target that has fired in
/// the last few seconds is seen as through <see cref="MuzzleFlashFogFactor"/> of the fog. And
/// gunfire is heard and its flash seen from further off, so a bot turns toward shooting within
/// <see cref="GunfireAttentionMetres"/>, not the day's 30 m.
/// </para>
/// <para>
/// <b>Goggles.</b> Every bot carries the players' goggles on the same battery (the room's
/// <c>nightVisionSeconds</c>): on when it is in a fight or closing on its objective, off when it
/// has been quiet a while, and with the night's fog thinned to the players' night-vision share while
/// on (<see cref="BotNightVision"/>).
/// </para>
/// <para>
/// <b>Moving.</b> A squad keeps closer together in the dark (<see cref="SquadSpreadMetres"/>), and
/// the commander plans with <c>TacticsProfile.Night</c>: it gathers short of a defended flag before
/// going in, and draws in bots from further off to join a squad.
/// </para>
/// </remarks>
public static class NightTactics
{
	/// <summary>The share of the fog a target that has just fired is seen through.</summary>
	public const float MuzzleFlashFogFactor = 0.5f;

	/// <summary>How far off a bot turns toward gunfire at night; 30 m by day.</summary>
	public const float GunfireAttentionMetres = 90f;

	/// <summary>The day's gunfire attention range, the original's.</summary>
	public const float DayGunfireAttentionMetres = 30f;

	/// <summary>How far a squad member's move order scatters from the point at night; 3 m by day.</summary>
	public const float SquadSpreadMetres = 1.5f;

	/// <summary>The day's scatter, the original's.</summary>
	public const float DaySquadSpreadMetres = 3f;

	/// <summary>The players' night-vision fog share, used when the map has no TimeOfDay.</summary>
	public const float FallbackNightVisionFogFactor = 0.35f;

	/// <summary>Whether the map is in the dark: Night Mode, or the original's offline night.</summary>
	public static bool IsNight => GameManager.instance != null && GameManager.instance.nightMode;

	/// <summary>The fog share a bot's goggles leave: the same as the players'.</summary>
	public static float NightVisionFogFactor
		=> TimeOfDay.instance != null ? TimeOfDay.instance.nightVisionFogFactor : FallbackNightVisionFogFactor;

	/// <summary>Seconds of night vision in a full battery: the room's, or the default offline.</summary>
	public static float BatterySeconds
		=> NetRoomRules.Current.NightVisionSeconds > 0 ? NetRoomRules.Current.NightVisionSeconds : RoomRules.DefaultNightVisionSeconds;
}
