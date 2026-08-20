using UnityEngine;

/// <summary>
/// The one place <c>Time.timeScale</c> and <c>Time.fixedDeltaTime</c> are written together.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists — issue #123.</b> <c>ProjectSettings/TimeManager.asset</c> declared a
/// fixed timestep, and two unrelated components overwrote it at runtime with
/// <c>Time.timeScale / 60f</c>: <c>FpsActorController</c> (slow-motion toggle) and
/// <c>IngameMenuUi</c> (pause/resume). A peer that constructed neither — <b>a dedicated server
/// build is exactly such a peer</b> — kept the project setting, so the server stepped physics at
/// 50 Hz while every rendered client stepped at 60. Measured live in Dustbowl by both the
/// Profiler and the behavioural harness: <c>FixedDeltaTimeMs = 16.66667</c> on the client.
/// </para>
/// <para>
/// Rigidbody integration is not step-independent, so the same inputs produced different vehicle
/// and helicopter motion on the two sides. That is the class of divergence V0 exists to remove
/// and the one V5's prediction blend assumes is gone — and it lands squarely on
/// <c>phase-3-harness.md</c> § 2 checks 7 (vehicle parity at 100 ms / 5 % loss) and 12 (turret
/// parity), where it would have presented as a replication defect.
/// </para>
/// <para>
/// <b>What is NOT affected, so nobody widens this.</b> <c>MovementCore</c> does not read
/// <c>Time.fixedDeltaTime</c> at all — its own remark pins the step to
/// <c>1/ProtocolConstants.SIM_TICK_RATE</c>, 30 Hz, <i>"never a variable"</i>. Player movement
/// prediction was never exposed to this, and the netcode's accumulator still owns its own rate.
/// Decision A5 stands: the netcode does not take the physics rate, and the physics rate does not
/// take the netcode's.
/// </para>
/// <para>
/// <b>The base step is read, never declared.</b> A <c>const 1f/60f</c> here would be a second
/// source of truth beside <c>TimeManager.asset</c>, free to disagree with it — which is the
/// shape of the bug this class removes, one layer up. Instead the base is recovered from the
/// live values on first use, and <see cref="SetTimeScale"/> is the only writer afterwards.
/// </para>
/// </remarks>
public static class PhysicsRate
{
    private static float _baseFixedDeltaTime;

    /// <summary>
    /// The unscaled fixed timestep — the project setting, in seconds.
    /// </summary>
    /// <remarks>
    /// Recovered rather than assumed, and self-correcting: at normal speed the live
    /// <c>fixedDeltaTime</c> IS the base, and while something is already scaled the base is
    /// <c>fixedDeltaTime / timeScale</c>. That second branch matters in the Editor, where
    /// leaving play mode does not reliably restore a scaled <c>fixedDeltaTime</c>, so a naive
    /// capture-on-first-load would bake a slow-motion step in as the project setting and every
    /// later resume would restore the wrong number.
    /// </remarks>
    public static float BaseFixedDeltaTime
    {
        get
        {
            if (_baseFixedDeltaTime > 0f) return _baseFixedDeltaTime;

            float scale = Time.timeScale;
            _baseFixedDeltaTime = scale > 0f
                ? Time.fixedDeltaTime / scale
                : Time.fixedDeltaTime;

            return _baseFixedDeltaTime;
        }
    }

    /// <summary>
    /// Sets the time scale and the matching physics step together.
    /// </summary>
    /// <param name="scale">
    /// 1 for normal speed, a fraction for slow motion, 0 to pause.
    /// </param>
    /// <remarks>
    /// <b>A paused scale leaves the step alone.</b> Unity stops issuing fixed steps at
    /// <c>timeScale == 0</c>, so scaling the step to 0 buys nothing and hands a zero timestep to
    /// every <c>rate * Time.fixedDeltaTime</c> in the project. The pause path never wrote the
    /// step before this change either — only the resume path did — and that asymmetry is
    /// preserved deliberately rather than tidied away.
    /// </remarks>
    public static void SetTimeScale(float scale)
    {
        float baseStep = BaseFixedDeltaTime;   // read BEFORE the scale moves

        Time.timeScale = scale;

        if (scale > 0f) Time.fixedDeltaTime = baseStep * scale;
    }
}
