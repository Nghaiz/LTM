using UnityEngine;

public class SpectatorCamera : MonoBehaviour
{
	private Vector3 velocity = Vector3.zero;

	private MeanFilterVector3 smoothVelocity = new MeanFilterVector3(60);

	private Vector3 angularVelocity;

	private MeanFilterVector3 smoothAngularVelocity = new MeanFilterVector3(60);

	private bool smooth;

	public float speedMultiplier = 1f;

	private void Update()
	{
		float y = Input.mouseScrollDelta.y;
		if (y < 0f)
		{
			speedMultiplier /= 1.3f;
		}
		else if (y > 0f)
		{
			speedMultiplier *= 1.3f;
		}
		float num = 20f;
		if (Input.GetKey(KeyCode.LeftShift))
		{
			num = 80f;
		}
		if (Input.GetKey(KeyCode.LeftControl))
		{
			num = 5f;
		}
		num *= speedMultiplier;
		velocity = (base.transform.forward * Input.GetAxis("Vertical") + base.transform.right * Input.GetAxis("Horizontal") + base.transform.up * Input.GetAxis("Lean")) * num;
		if (!smooth)
		{
			base.transform.position += velocity * Time.deltaTime;
		}
		else
		{
			base.transform.position += smoothVelocity.Value() * Time.deltaTime;
		}
		Vector3 eulerAngles = base.transform.eulerAngles;
		angularVelocity = new Vector3(Input.GetAxis("Mouse Y") * ((!OptionsUi.GetOptions().mouseInvert) ? (-1f) : 1f), Input.GetAxis("Mouse X"), 0f) * OptionsUi.GetOptions().mouseSensitivity * 3f;
		if (!smooth)
		{
			eulerAngles += angularVelocity;
		}
		else
		{
			eulerAngles += smoothAngularVelocity.Value();
		}
		base.transform.eulerAngles = eulerAngles;

		if (Input.GetKeyDown(KeyCode.L))
		{
			string screenshotName = "screenshot.png";

			// P27. The `3` is the original's supersize factor, restored. Unity renamed
			// Application.CaptureScreenshot to ScreenCapture.CaptureScreenshot and the rewrite
			// dropped the second argument, which the rename never required -- the (string, int)
			// overload still exists. Nothing in the tree documented the drop, and the whole point
			// of a spectator-camera screenshot is a capture worth keeping, so this had been
			// silently writing at 1x since the Unity 6 migration.
			ScreenCapture.CaptureScreenshot(screenshotName, 3);
		}
		RaycastHit hitInfo;
		if (Input.GetMouseButtonDown(0) && Physics.Raycast(base.transform.position, base.transform.forward, out hitInfo) && !hitInfo.collider.gameObject.isStatic)
		{
			base.transform.parent = hitInfo.collider.transform;
		}
		if (Input.GetMouseButtonDown(1))
		{
			base.transform.parent = null;
			eulerAngles = base.transform.eulerAngles;
			eulerAngles.z = 0f;
			base.transform.eulerAngles = eulerAngles;
		}
		if (Input.GetMouseButtonDown(2))
		{
			smooth = !smooth;
		}
	}

	private void FixedUpdate()
	{
		smoothVelocity.Tick(velocity);
		smoothAngularVelocity.Tick(angularVelocity);
	}
}
