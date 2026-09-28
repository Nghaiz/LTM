using UnityEngine;

/// <summary>
/// Supplies the manager singletons when a map scene is entered WITHOUT passing through the
/// menu — which is every networked run.
/// </summary>
/// <remarks>
/// <para>
/// <b>On this path the prefab's serialized values ARE the match settings.</b>
/// <c>MainMenu.SaveGameSettings</c> is the only writer of <c>GameManager.assaultMode</c>,
/// <c>reverseMode</c>, <c>nightMode</c> and <c>noVehicles</c>, and it is reached from
/// <c>MainMenu.StartLevel</c> alone. <c>ClientFlowBootstrap</c>,
/// <c>DedicatedServerSceneBootstrap</c> and <c>LaneBHarness</c> each call
/// <c>SceneManager.LoadScene</c> directly, so the menu never runs and
/// <c>Assets/Resources/_Managers.prefab</c> decides instead.
/// </para>
/// <para>
/// <b>X-80.</b> That prefab shipped with <c>assaultMode: 1</c> while the menu's own toggle is
/// authored <c>m_IsOn: 0</c> (<c>Menu.unity:138865</c>), so every recorded server run opened with
/// each neutral point handed to team 1 — <c>6 of 6</c> on Dustbowl and <c>5 of 5</c> on Island,
/// across 33 runs — against two maps that author exactly one base per side and leave the rest
/// neutral. Assault mode is an optional Ravenfield match modifier the player ticks, not the
/// default opening; the prefab now reads <c>assaultMode: 0</c> and the server opens
/// <c>2 of 6</c> / <c>2 of 5</c>, which is what the map authors wrote.
/// </para>
/// <para>
/// So a default changed in that prefab changes what every networked match plays. It is a
/// gameplay decision, not a serialization detail.
/// </para>
/// </remarks>
public class LevelTester : MonoBehaviour
{
	private void Awake()
	{
		if (GameManager.instance == null)
		{
			InstantiateManagers();
		}
	}

	private void InstantiateManagers()
	{
		GameObject original = Resources.Load("_Managers") as GameObject;
		Object.Instantiate(original);
	}
}
