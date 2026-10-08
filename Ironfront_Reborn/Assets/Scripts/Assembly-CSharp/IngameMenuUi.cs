using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.SceneManagement;
using UnityStandardAssets.Characters.FirstPerson;

public class IngameMenuUi : MonoBehaviour
{
	public static IngameMenuUi instance;

	public AudioMixer mixer;

	private Canvas canvas;

	public static void Show()
	{
		if (instance == null)
		{
			return;
		}
		instance.canvas.enabled = true;
		MouseLook.paused = true;
		Cursor.lockState = CursorLockMode.None;
		Cursor.visible = true;
		// Pausing never wrote fixedDeltaTime; Unity issues no fixed step at timeScale 0, and
		// a zero step would be handed to every `rate * Time.fixedDeltaTime` in the project.
		// PhysicsRate preserves that asymmetry rather than tidying it away.
		//
		// Offline only. A networked match does not stop because one player opened a menu: the
		// server and every other player carry on, and a time scale of zero here froze this
		// client's physics, animation and effects under them while the snapshots kept arriving.
		if (Ironfront.Net.Unity.NetContext.IsOffline)
		{
			PhysicsRate.SetTimeScale(0f);
		}
		instance.mixer.SetFloat("pitch", Time.timeScale);
	}

	public static void Hide()
	{
		if (instance == null)
		{
			return;
		}
		instance.canvas.enabled = false;
		MouseLook.paused = false;
		// PhysicsRate, not a second `Time.timeScale / 60f`. That literal made this UI script
		// an authority on the project's physics rate, and a peer that never constructed it --
		// a dedicated server build -- kept a different one. Issue #123.
		PhysicsRate.SetTimeScale(1f);
		instance.mixer.SetFloat("pitch", Time.timeScale);
		Cursor.lockState = CursorLockMode.Locked;
		Cursor.visible = false;
	}

	/// <summary>
	/// Whether the pause menu is showing. False where there is no menu — a server is never
	/// paused, and <c>Actor.UpdateMovement</c> asks this for every actor on every frame.
	/// </summary>
	public static bool IsOpen()
	{
		return instance != null && instance.canvas.enabled;
	}

	private void Awake()
	{
		instance = this;
		canvas = GetComponent<Canvas>();
		canvas.enabled = false;
		Hide();
	}

	public void Resume()
	{
		Hide();
	}

	/// <summary>
	/// The SETTINGS row: the same settings screen as the main menu's, over the match
	/// (<see cref="Ironfront.Net.Unity.GameOverlays"/>). The original options panel only where no
	/// overlay is installed (a headless or test process).
	/// </summary>
	public void Options()
	{
		if (Ironfront.Net.Unity.GameOverlays.IsAvailable(Ironfront.Net.Unity.OverlayPage.Settings))
		{
			Ironfront.Net.Unity.GameOverlays.Open(Ironfront.Net.Unity.OverlayPage.Settings);
			return;
		}
		OptionsUi.Show();
	}

	public void Menu()
	{
		MouseLook.paused = false;
		// A matchmade match is left through the flow, which drops the game-server link and
		// returns to the lobby still signed in. Loading the scene underneath it, all this did
		// until 2026-09-29, left the flow in a match with the link up, and MULTIPLAYER on the
		// menu it landed on did nothing until the game was restarted.
		if (Ironfront.Net.Unity.NetClientBindings.TryLeaveMatch())
		{
			return;
		}
		SceneManager.LoadScene(1);
	}

	public void Quit()
	{
		AppQuit.Quit();
	}

	private void Update()
	{
		// Not the Esc that closes the chat box. The chat box reads it first, before any gameplay script,
		// and OwnsKeyboard still answers true for the rest of that frame.
		// Nor the Esc an overlay (settings, the guide) is closing on, this frame or while it is up.
		if (!Input.GetKeyDown(KeyCode.Escape) || Ironfront.Net.Unity.LocalTextEntry.OwnsKeyboard
			|| Ironfront.Net.Unity.GameOverlays.OwnsEscape)
		{
			return;
		}
		if (canvas.enabled)
		{
			Hide();
			if (OptionsUi.IsOpen())
			{
				OptionsUi.SaveAndClose();
			}
		}
		else
		{
			Show();
		}
	}
}
