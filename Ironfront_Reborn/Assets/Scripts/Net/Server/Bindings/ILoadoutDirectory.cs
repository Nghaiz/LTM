namespace Ironfront.Net.Unity.Server
{
    /// <summary>Which slot of a body's loadout a name is being asked for.</summary>
    public enum LoadoutSlot
    {
        Primary,
        Secondary,
        Gear1,
    }

    /// <summary>
    /// Lets a caller force a named weapon into a slot instead of the random draw, without the
    /// drawing code naming who is forcing it. Ledger <b>X-27</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> `AiActorController.GetLoadout` picks each slot with
    /// `UnityEngine.Random.Range` over a private static name array, and a networked player's
    /// server-side body goes through it. Measured across three runs of the SAME lane-B
    /// programme with the spawn pinned: weapon 1 twice and weapon 15 once, 30 shots against
    /// 14. A flake rate over runs that differ in weapon is a rate over three different
    /// experiments.
    /// </para>
    /// <para>
    /// <b>Why a seam and not a seed.</b> Exactly the argument
    /// <see cref="PinnedSpawnPointDirectory"/> makes one file over: a seed pins the draw
    /// SEQUENCE, and three clients joining over a real socket reach the draw at a different
    /// offset in that sequence every run. Only narrowing the ANSWER is deterministic.
    /// </para>
    /// <para>
    /// <b>Names, not entries.</b> `WeaponManager.LoadoutSet` and `WeaponEntry` compile into
    /// `Assembly-CSharp`, which no asmdef can reference — the same inversion
    /// <see cref="ISpawnPointDirectory"/> and <see cref="IGameplayActorSource"/> are built
    /// around. A name is what the drawing code already holds, so nothing is lost by passing
    /// one.
    /// </para>
    /// <para>
    /// <b>Null means "keep the draw".</b> A directory that answers for one slot and not the
    /// others is the ordinary case: lane B pins a primary because the checks fire it, and has
    /// no opinion about a gear slot.
    /// </para>
    /// </remarks>
    public interface ILoadoutDirectory
    {
        /// <summary>
        /// The weapon name to force into <paramref name="slot"/>, or <c>null</c> to keep
        /// whatever was drawn.
        /// </summary>
        string OverrideFor(LoadoutSlot slot);
    }
}
