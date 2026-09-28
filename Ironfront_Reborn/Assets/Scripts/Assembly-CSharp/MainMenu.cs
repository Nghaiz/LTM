using UnityEngine;
using UnityEngine.UI;

public class MainMenu : MonoBehaviour
{
	public GameObject menuContent;

	public Toggle assaultModeToggle;

	public Toggle reverseModeToggle;

	public Toggle nightModeToggle;

	public Toggle noVehiclesToggle;

	public InputField victoryScoreInput;

	public InputField numberActorsInput;

	public InputField respawnTimeInput;

	public Slider botBalanceSlider;

	private void Start()
	{
		ShowMenu();
	}

	public void StartLevel(string levelName)
	{
		SaveGameSettings();
		Application.LoadLevel(levelName);
	}

	private void SaveGameSettings()
	{
		GameManager.instance.assaultMode = assaultModeToggle.isOn;
		GameManager.instance.reverseMode = reverseModeToggle.isOn;
		GameManager.instance.nightMode = nightModeToggle.isOn;
		GameManager.instance.noVehicles = noVehiclesToggle.isOn;
		int result;
		if (int.TryParse(victoryScoreInput.text, out result))
		{
			GameManager.instance.victoryPoints = result;
		}
		int result2;
		if (int.TryParse(numberActorsInput.text, out result2))
		{
			int num = Mathf.RoundToInt(botBalanceSlider.value * (float)result2);
			ActorManager.instance.team0Bots = result2 - num;
			ActorManager.instance.team1Bots = num;
		}
		int result3;
		if (int.TryParse(respawnTimeInput.text, out result3))
		{
			ActorManager.instance.spawnTime = result3;
		}
	}

	private void Update()
	{
		menuContent.SetActive(!OptionsUi.IsOpen());
		float value = botBalanceSlider.value;
		Color color = ((!(value > 0.5f)) ? Color.Lerp(Color.blue, Color.white, value * 2f) : Color.Lerp(Color.white, Color.red, (value - 0.5f) * 2f));
		ColorBlock colors = botBalanceSlider.colors;
		colors.normalColor = color;
		colors.highlightedColor = color;
		colors.pressedColor = color;
		botBalanceSlider.colors = colors;
	}

	public void OpenOptions()
	{
		OptionsUi.Show();
	}

	public void ShowMenu()
	{
		menuContent.SetActive(true);
	}

	public void Quit()
	{
		AppQuit.Quit();
	}
}
