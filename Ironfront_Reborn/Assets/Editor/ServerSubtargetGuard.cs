using UnityEditor;
using UnityEngine;

namespace Ironfront
{
	/// <summary>
	/// Puts an interactive Editor that opens on the Linux dedicated-server subtarget back on the
	/// Windows player, once per session.
	/// </summary>
	/// <remarks>
	/// <c>tools/build-server.ps1</c> builds in batch mode, and <see cref="EditorBuild"/> leaves the
	/// target on Linux/Server there to save a reimport. The target lives in <c>Library</c>, so the
	/// next interactive Editor opens on it, compiles the game with <c>UNITY_SERVER</c>, and Play
	/// silently has no player and no HUD (<c>LocalClient.Exists</c> is false). Seen 2026-10-03:
	/// Forest Lake in Play with a scenery camera and nothing else, after a server build.
	/// </remarks>
	[InitializeOnLoad]
	internal static class ServerSubtargetGuard
	{
		private const string CheckedKey = "Ironfront.ServerSubtargetGuard.Checked";

		static ServerSubtargetGuard()
		{
			if (Application.isBatchMode || SessionState.GetBool(CheckedKey, false))
			{
				return;
			}
			SessionState.SetBool(CheckedKey, true);
			if (EditorUserBuildSettings.standaloneBuildSubtarget != StandaloneBuildSubtarget.Server)
			{
				return;
			}
			EditorApplication.delayCall += BackToWindowsPlayer;
		}

		private static void BackToWindowsPlayer()
		{
			BuildTarget was = EditorUserBuildSettings.activeBuildTarget;
			EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Player;
			bool switched = EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64);
			Debug.LogWarning($"[build] this Editor opened on {was}/Server, left by a batch server build; "
				+ (switched ? "switched back to Windows/Player so Play has a player and a HUD." : "could not switch back to Windows/Player: do it in Build Settings before pressing Play."));
		}
	}
}
