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
		PhysicsRate.SetTimeScale(0f);
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

	public void Options()
	{
		OptionsUi.Show();
	}

	public void Menu()
	{
		MouseLook.paused = false;
		SceneManager.LoadScene(1);
	}

	public void Quit()
	{
		AppQuit.Quit();
	}

	private void Update()
	{
		if (!Input.GetKeyDown(KeyCode.Escape))
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
