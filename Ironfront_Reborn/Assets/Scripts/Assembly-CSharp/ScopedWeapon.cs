using UnityEngine;

public class ScopedWeapon : Weapon
{
	public GameObject scope;

	private Action blackoutAction = new Action(0.3f);

	private Texture2D blackoutTexture;

	private bool showingScope;

	protected override void Awake()
	{
		base.Awake();
		blackoutTexture = new Texture2D(8, 8);
	}

	public override void FindRenderers(bool thirdPerson)
	{
		base.FindRenderers(thirdPerson);
		Renderer[] componentsInChildren = scope.GetComponentsInChildren<Renderer>();
		foreach (Renderer item in componentsInChildren)
		{
			renderers.Remove(item);
		}
		SetAiming(false);
	}

	public override void Unholster()
	{
		base.Unholster();
		SetAiming(false);
	}

	public override void SetAiming(bool aiming)
	{
		base.SetAiming(aiming);
		if (!HasActiveAnimator())
		{
			return;
		}
		if (aiming)
		{
			EnsureBlackout();
			showingScope = false;
			blackoutAction.Start();
			return;
		}
		showingScope = false;
		blackoutAction.Stop();
		scope.SetActive(false);
		foreach (Renderer renderer in renderers)
		{
			renderer.enabled = true;
		}
	}

	protected override void Update()
	{
		base.Update();
		if (blackoutAction.TrueDone() || !(blackoutAction.Ratio() > 0.5f) || showingScope)
		{
			return;
		}
		showingScope = true;
		scope.SetActive(true);
		foreach (Renderer renderer in renderers)
		{
			renderer.enabled = false;
		}
	}

	/// <summary>How opaque the blackout is now: zero unless the scope is coming up in first person.</summary>
	internal float BlackoutAlpha()
	{
		if (HasActiveAnimator() && !blackoutAction.TrueDone() && showingScope)
		{
			return Mathf.Clamp01(4f - 4f * blackoutAction.Ratio());
		}
		return 0f;
	}

	internal Texture2D BlackoutTexture => blackoutTexture;

	// The blackout's IMGUI draw lives on a component of its own, added here, on the first-person
	// weapon, the first time it aims: an OnGUI on this class ran on every bot's rifle (ScopeBlackout).
	// Not compiled into the dedicated server: IMGUI is stripped there, and Unity logs
	// 'OnGUI function detected ... not called' for every instance -- once per bot, 402 lines in
	// one 100-bot match (B4, 2026-09-30).
	private void EnsureBlackout()
	{
#if !UNITY_SERVER
		if (blackout == null)
		{
			blackout = base.gameObject.AddComponent<ScopeBlackout>();
			blackout.weapon = this;
		}
#endif
	}

#if !UNITY_SERVER
	private ScopeBlackout blackout;
#endif
}
