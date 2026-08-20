using UnityEngine;

/// <summary>
/// The single exit point for the application.
/// </summary>
/// <remarks>
/// <para>
/// <c>UnityEngine.Application.Quit()</c> is a documented no-op inside the Editor: it returns
/// immediately and Play mode keeps running. Every Quit button in this project called it
/// directly, so in the Editor — which is where the game is actually played during development
/// — the button did nothing and read as broken.
/// </para>
/// <para>
/// The <c>Debug.Log</c> below is deliberate and is the diagnosis, not noise. If pressing Quit
/// prints nothing, the button is not wired to <see cref="Quit"/> in the scene at all, which is
/// a Prefab/Canvas fix in the Editor rather than a code fix.
/// </para>
/// </remarks>
public static class AppQuit
{
	public static void Quit()
	{
		Debug.Log("[AppQuit] quit requested");

		// A quit issued from a paused menu leaves timeScale at 0. Harmless in a player, but in
		// the Editor the next Play session inherits whatever the domain was left holding when
		// domain reloading is disabled, and a frozen game is a worse bug than the one fixed here.
		//
		// Through PhysicsRate rather than a bare assignment, because THE SAME INHERITANCE applies
		// to Time.fixedDeltaTime. Restoring timeScale to 1 while leaving the step at 0.2/60 hands
		// the next session a slow-motion step at normal speed, and PhysicsRate would then recover
		// exactly that as the project's base rate and keep it. Issue #123.
		PhysicsRate.SetTimeScale(1f);

#if UNITY_EDITOR
		UnityEditor.EditorApplication.isPlaying = false;
#else
		Application.Quit();
#endif
	}
}
