namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// The bot brain steering one replicated body, and the two calls that hand the body to a
    /// connection and take it back. Phase-3A.
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
    /// </remarks>
    public interface IAiDriver
    {
        /// <summary>False once the controller or its GameObject has been destroyed.</summary>
        bool Exists { get; }

        /// <summary>Stops the bot brain driving. Called when a connection claims the body.</summary>
        void Suspend();

        /// <summary>
        /// Hands the body back to the bot brain. Called when the claim is released.
        /// </summary>
        /// <remarks>
        /// A slot is reused across a match: without this, every disconnect would leave one more
        /// inert mannequin standing in the map for the rest of the round, and a server that had
        /// seen <c>MaxConnections</c> joins and departures would be a map full of them.
        /// </remarks>
        void Resume();
    }
}
