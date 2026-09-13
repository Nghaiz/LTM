using UnityEngine;
using UnityEngine.SceneManagement;

public class GotoMenu : MonoBehaviour
{
	public float duration = 10f;

	private Action nextSceneAction = new Action(1f);

	private bool loadingNextScene;

	private void Start()
	{
		nextSceneAction.StartLifetime(duration);
	}

	private void Update()
	{
		if ((Input.anyKeyDown && CanSkip()) || nextSceneAction.TrueDone())
		{
			GotoNextScene();
		}
	}

	private void GotoNextScene()
	{
		if (loadingNextScene)
		{
			return;
		}

		loadingNextScene = true;
		try
		{
			PlayerPrefs.SetInt("SeenIntro", 1);
			PlayerPrefs.Save();
		}
		catch (PlayerPrefsException exception)
		{
			// A read-only/locked registry must not trap a Windows client on the intro scene.
			// Remembering the skip is optional; entering the menu is not.
			Debug.LogWarning("[startup] could not save SeenIntro; continuing to the menu. "
				+ exception.Message);
		}

		SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex + 1);
	}

	private bool CanSkip()
	{
		return PlayerPrefs.HasKey("SeenIntro");
	}
}
