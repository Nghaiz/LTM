using UnityEngine;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.SceneManagement;

/// <summary>
/// Runs this camera's post-processing only while a loaded map provides a volume for it.
/// </summary>
/// <remarks>
/// The player, spectator and vehicle cameras are shared by every map, but only some maps author
/// post-processing. A <see cref="PostProcessLayer"/> with nothing to blend still renders a
/// full-screen pass and adds dithering noise, so a map without a volume would pay for, and look
/// different from, an effect it never asked for. Volumes only appear or vanish with the scenes
/// that hold them, so the scene events are the whole of the lifecycle to follow. Whether a local
/// volume currently contains the camera is the layer's own per-frame business, not this gate's.
/// </remarks>
[RequireComponent(typeof(PostProcessLayer))]
public sealed class PostProcessingGate : MonoBehaviour
{
	private PostProcessLayer layer;

	private void Awake()
	{
		layer = GetComponent<PostProcessLayer>();
	}

	private void OnEnable()
	{
		SceneManager.sceneLoaded += OnSceneLoaded;
		SceneManager.sceneUnloaded += OnSceneUnloaded;
		Refresh();
	}

	private void OnDisable()
	{
		SceneManager.sceneLoaded -= OnSceneLoaded;
		SceneManager.sceneUnloaded -= OnSceneUnloaded;
	}

	private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
	{
		Refresh();
	}

	private void OnSceneUnloaded(Scene scene)
	{
		Refresh();
	}

	private void Refresh()
	{
		int mask = layer.volumeLayer.value;
		foreach (PostProcessVolume volume in FindObjectsByType<PostProcessVolume>(FindObjectsSortMode.None))
		{
			if (volume.enabled && volume.sharedProfile != null && (mask & (1 << volume.gameObject.layer)) != 0)
			{
				layer.enabled = true;
				return;
			}
		}
		layer.enabled = false;
	}
}
