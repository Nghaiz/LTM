using System;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Evaluates the input expressions <c>FpsActorController</c> used before phase-00 task 3
    /// beside the values <see cref="LocalInputSource"/> now returns, and names the first site
    /// where the two disagree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Temporary.</b> Delete this file, and the two lines in
    /// <c>FpsActorController.Awake</c> that install it, once the client track has played a session and the
    /// Console stayed quiet. It is written to be deleted — nothing depends on it.
    /// </para>
    /// <para>
    /// <b>Why it exists.</b> The task substituted a dozen input expressions without ever running
    /// the game, against phase-00 criterion 5: <i>"single-player still plays exactly as before
    /// the refactor"</i>. No gate in this repository can check that — CI does not compile Unity
    /// code, let alone play it. A wrong axis sign or an inverted <c>&amp;&amp;</c> yields a game
    /// that runs and feels subtly wrong, which is the worst failure mode available. This turns
    /// "eyeball a dozen diffs" into "play for five minutes; the Console is silent or it names
    /// the site".
    /// </para>
    /// <para>
    /// The shape is borrowed from <c>MovementShadowCompare</c>, which solved the same problem
    /// for the movement port, down to the summary-on-exit and the "I am here and ticking" line —
    /// silence from a harness that was never installed is indistinguishable from silence from a
    /// harness that found nothing, and that ambiguity has already cost this project a playtest.
    /// </para>
    /// <para>
    /// <b>Read-only by construction.</b> There is no code path here that writes to the actor,
    /// the controller, or the input source. It reads and it logs.
    /// </para>
    /// </remarks>
    // Late, so that anything the actor controller's Update does to the loadout screen this frame
    // has already happened and both sides of the comparison see the same UI state.
    [DefaultExecutionOrder(1000)]
    [DisallowMultipleComponent]
    public sealed class InputShadowCompare : MonoBehaviour
    {
        /// <summary>Command-line switch that suppresses installation.</summary>
        public const string DisableArgument = "-ironfront-no-input-shadow";

        /// <summary>
        /// Whether <see cref="Install"/> does anything. Default on: a harness nobody remembers
        /// to enable is a harness that reports nothing on the one run that mattered.
        /// </summary>
        public static bool Enabled = true;

        [Tooltip("Disagreement between two axis values before it is reported. Unity's smoothed " +
                 "axes are read twice in the same frame here, so a genuine match is exact; " +
                 "this is float slack, not tolerance.")]
        public float AxisEpsilon = 1e-4f;

        private IInputSource _source;

        // One latch per site: a mismatch that fires every frame would bury the Console and
        // teach the reader to ignore it. The first occurrence is the whole message.
        private readonly bool[] _reported = new bool[SiteCount];
        private int _framesScored;
        private int _sitesDiverged;
        private bool _summarised;

        private const int SiteCount = 10;

        private static readonly string[] SiteNames =
        {
            "Fire (was line 130)",
            "Aiming (was line 139)",
            "Reload (was line 144)",
            "Crouch (was line 675)",
            "Sprint (was line 715)",
            "MoveX / Horizontal (was lines 164, 188, 213, 215)",
            "MoveZ / Vertical (was lines 164, 188, 213, 215)",
            "Lean (was line 378)",
            "LookDeltaX / Mouse X (was line 202)",
            "LookDeltaY / Mouse Y (was line 202)",
        };

        /// <summary>
        /// Attaches the harness to <paramref name="host"/> unless it is switched off. Safe to
        /// call more than once.
        /// </summary>
        public static void Install(GameObject host, IInputSource source)
        {
            if (!Enabled || host == null || source == null) return;
            if (host.GetComponent<InputShadowCompare>() != null) return;

            host.AddComponent<InputShadowCompare>()._source = source;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ReadCommandLine()
        {
            // Reset explicitly: with domain reload disabled, a static set false in one Play
            // session would silently stay false in the next, and the harness would report
            // nothing for reasons nobody could see.
            Enabled = true;

            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], DisableArgument, StringComparison.OrdinalIgnoreCase))
                    continue;

                Enabled = false;
                return;
            }
        }

        private void OnEnable()
        {
            _summarised = false;
            Debug.Log($"[InputShadowCompare] attached to '{name}' and ticking. Play for a few " +
                      "minutes — run, shoot, aim, reload, crouch, lean, sprint, swim, drive — " +
                      "then stop Play. Silence until the summary means the substitution set is " +
                      $"right. Pass {DisableArgument} to switch this off.");
        }

        private void Update()
        {
            if (_source == null) return;

            _framesScored++;

            // The right-hand side of each pair is the expression that stood in
            // FpsActorController before the refactor, transcribed verbatim. Where it stops being
            // verbatim, this harness stops being evidence.
            // Through the binding, as LocalInputSource reads it. Calling the legacy UI directly
            // would make this harness compare the seam against itself minus the seam, and the
            // one thing it must not do is disagree with the production path for a reason that
            // is the harness's own.
            bool loadoutOpen = NetInputBindings.Environment.LoadoutScreenOpen;

            // On both sides, for the same reason loadoutOpen is on both sides: it is a
            // suppression term shared by the production path, not part of the transcription
            // being checked. Present here only so the harness keeps comparing the seam against
            // the legacy expression rather than against the suppression.
            bool typing = LocalTextEntry.Composing;

            CompareBool(0, _source.Fire(),
                (Input.GetButton("Fire1") || Input.GetMouseButton(0)) && !loadoutOpen && !typing);
            CompareBool(1, _source.Aim(),
                (Input.GetButton("Fire2") || Input.GetMouseButton(1)) && !loadoutOpen && !typing);
            CompareBool(2, _source.Reload(),
                Input.GetButton("Reload") && !loadoutOpen && !typing);
            CompareBool(3, _source.Crouch(), Input.GetButton("Crouch") && !typing);
            CompareBool(4, _source.Sprint(), Input.GetButton("Sprint") && !typing);

            CompareAxis(5, _source.MoveX, typing ? 0f : Input.GetAxis("Horizontal"));
            CompareAxis(6, _source.MoveZ, typing ? 0f : Input.GetAxis("Vertical"));
            CompareAxis(7, _source.Lean, typing ? 0f : Input.GetAxis("Lean"));
            CompareAxis(8, _source.LookDeltaX, Input.GetAxis("Mouse X"));
            CompareAxis(9, _source.LookDeltaY, Input.GetAxis("Mouse Y"));
        }

        private void CompareBool(int site, bool viaInterface, bool legacy)
        {
            if (viaInterface == legacy) return;

            Report(site, viaInterface ? "true" : "false", legacy ? "true" : "false");
        }

        private void CompareAxis(int site, float viaInterface, float legacy)
        {
            if (Mathf.Abs(viaInterface - legacy) <= AxisEpsilon) return;

            Report(site, viaInterface.ToString("F4"), legacy.ToString("F4"));
        }

        private void Report(int site, string viaInterface, string legacy)
        {
            if (_reported[site]) return;

            _reported[site] = true;
            _sitesDiverged++;

            Debug.LogWarning(
                $"INPUT DIVERGED site=\"{SiteNames[site]}\" frame={_framesScored} " +
                $"IInputSource={viaInterface} original={legacy}. " +
                "The refactor changed this input's meaning. Reported once per site per session; " +
                "fix LocalInputSource, not the call site.");
        }

        private void OnDisable() => Summarise();

        // OnDisable is not guaranteed on a built player's exit; OnApplicationQuit is. Both fire
        // in the Editor, hence the latch.
        private void OnApplicationQuit() => Summarise();

        private void Summarise()
        {
            if (_summarised) return;
            _summarised = true;

            if (_framesScored == 0)
            {
                Debug.LogWarning(
                    $"[InputShadowCompare] on '{name}' scored zero frames, so it proves nothing. " +
                    "Update never ran: the component's GameObject was never active, or it was " +
                    "installed without an input source.");
                return;
            }

            if (_sitesDiverged == 0)
            {
                Debug.Log(
                    $"[InputShadowCompare] CLEAN over {_framesScored} frames — every input site " +
                    "agreed with the expression it replaced on every frame observed. Note what " +
                    "this does and does not say: a site is only checked on frames where it was " +
                    "exercised, so this is evidence in proportion to what was actually played. " +
                    "Once you are satisfied, delete InputShadowCompare.cs and the Install call " +
                    "in FpsActorController.Awake.");
                return;
            }

            Debug.LogWarning(
                $"[InputShadowCompare] {_sitesDiverged} of {SiteCount} sites diverged over " +
                $"{_framesScored} frames. Search the Console for \"INPUT DIVERGED\" — each names " +
                "the site and the line it came from. Do not delete this harness yet.");
        }
    }
}
