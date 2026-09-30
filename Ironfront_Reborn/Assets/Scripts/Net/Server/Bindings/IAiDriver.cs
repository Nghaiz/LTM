namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// The bot brain steering one replicated body, and the call that parks it for good when the
    /// body becomes a player slot. Phase-3A.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A seam rather than a direct call, for the same reason every other one here is one.</b>
    /// <c>AiActorController</c> lives in <c>Assembly-CSharp</c>, which no <c>.asmdef</c> can
    /// reference — so this assembly declares what it needs and <c>IronfrontNetBindings</c>,
    /// which sits outside every asmdef, supplies it.
    /// </para>
    /// <para>
    /// <b>Suspend rather than destroy.</b> <c>Actor.aiControlled</c> is frozen in <c>Awake</c>
    /// from <c>controller.GetType() == typeof(AiActorController)</c> and then read by UI, LOD,
    /// weapon culling and <c>ActorManager.Register</c>. Removing the controller would flip that
    /// flag's meaning out from under every one of those readers, and a body whose
    /// <c>controller</c> is null dereferences it in <c>Awake</c> before anything else runs.
    /// Disabling the component leaves the type in place and only stops it driving.
    /// </para>
    /// <para>
    /// <b>Why it must be suspended at all.</b> Server movement for a claimed body is driven by
    /// <c>ServerPlayer</c> through <c>NetMovementAgent</c>. An AI still steering the same
    /// <c>CharacterController</c> is a second writer to one position, and the client is
    /// predicting against only one of them. <c>NetVerificationHarness.OpenSecondSlot</c> found
    /// this the hard way and disabled the controller by reflecting on its type NAME; this
    /// interface is that fix, typed.
    /// </para>
    /// <para>
    /// <b>No resume.</b> A resume on release made a leaver's body play on as a bot, and an
    /// unclaimed slot is announced to no client (X-18), so it played on unseen: the 2026-09-30
    /// live test. A released slot now leaves the match (<c>NetServerActor.ReturnToPool</c>).
    /// </para>
    /// </remarks>
    public interface IAiDriver
    {
        /// <summary>False once the controller or its GameObject has been destroyed.</summary>
        bool Exists { get; }

        /// <summary>
        /// Stops the bot brain driving. Called when the pool builds the body, and again (as a
        /// no-op) when a connection claims it.
        /// </summary>
        void Suspend();
    }
}
