using Ironfront.Net.Unity.Server;
using UnityEngine;

namespace Ironfront.Net.Unity.Bindings
{
    /// <summary>
    /// Installs a <see cref="NetInputSource"/> on one driver's <c>FpsActorController</c> and
    /// feeds it the axes the server accepted. The <c>Assembly-CSharp</c> half of
    /// <see cref="IDriverInputSink"/>. V5 task 5.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the wiring nothing did.</b> Before V5,
    /// <c>grep -rn "SetInputSource\|NetInputSource"</c> across the repository returned the
    /// definition, one comment and the test project — no production call site anywhere. It went
    /// unnoticed because server movement bypasses the controller entirely (<c>ServerPlayer</c>
    /// drives <c>NetMovementAgent</c>), but all four vehicles PULL through
    /// <c>Driver().controller.CarInput()</c> / <c>HelicopterInput()</c>. The moment a networked
    /// player drove, the vehicle read whatever source the controller happened to hold — on a
    /// headless build, a keyboard that is not there.
    /// </para>
    /// <para>
    /// <b>No new <c>ActorController</c> subclass (V5-D7).</b> One would trip
    /// <c>Actor.aiControlled</c>, frozen in <c>Awake</c> from
    /// <c>controller.GetType() == typeof(AiActorController)</c> and then read by UI, LOD and
    /// weapon culling. Extending the existing <c>IInputSource</c> seam does not go near it.
    /// </para>
    /// <para>
    /// <b>The previous source is remembered rather than assumed.</b> On a listen server or in
    /// the Editor the driver may be a local player whose <c>LocalInputSource</c> is the thing
    /// they walk with; replacing it with the null object on seat exit would leave them unable to
    /// move.
    /// </para>
    /// </remarks>
    internal sealed class NetDriverInputSink : IDriverInputSink
    {
        private readonly FpsActorController _controller;
        private readonly NetInputSource _source;
        private readonly IInputSource _previous;

        private NetDriverInputSink(FpsActorController controller)
        {
            _controller = controller;
            _source = new NetInputSource();
            _previous = controller.InputSource;

            controller.SetInputSource(_source);
        }

        /// <summary>
        /// Installs a sink on this GameObject's controller, or returns null when it has none.
        /// </summary>
        /// <remarks>
        /// Null is a real answer, not a failure: a bot has an <c>AiActorController</c> and drives
        /// itself, and the server counts a null as an unreachable controller only because a
        /// networked PLAYER reaching a driver seat without one means that vehicle will not
        /// respond to them at all.
        /// </remarks>
        internal static IDriverInputSink Attach(GameObject gameObject)
        {
            if (gameObject == null) return null;

            FpsActorController controller = gameObject.GetComponent<FpsActorController>();
            return controller != null ? new NetDriverInputSink(controller) : null;
        }

        /// <inheritdoc />
        public bool Exists => _controller != null;

        /// <inheritdoc />
        public void SetAxes(
            float steer, float throttle,
            float heliYaw, float heliCollective, float heliRoll, float heliPitch)
        {
            _source.SetVehicleAxes(
                steer, throttle,
                new HelicopterAxes(heliYaw, heliCollective, heliRoll, heliPitch));
        }

        /// <inheritdoc />
        public void Centre() => _source.ClearVehicleAxes();

        /// <inheritdoc />
        public void Detach()
        {
            if (_controller == null) return;

            _source.ClearVehicleAxes();
            _controller.SetInputSource(_previous ?? NullInputSource.Instance);
        }
    }
}
