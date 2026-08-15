using UnityEngine;

public class FollowMainCamera : MonoBehaviour
{
	private void LateUpdate()
	{
		// There is no main camera on a headless server, and this ran every frame with no test.
		Camera camera = Camera.main;
		if (camera == null)
		{
			return;
		}
		base.transform.position = camera.transform.position;
	}
}
