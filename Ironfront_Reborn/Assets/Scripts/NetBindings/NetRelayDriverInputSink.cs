using Ironfront.Net.Unity.Server;
using UnityEngine;

namespace Ironfront.Net.Unity.Bindings
{
    /// <summary>
    /// The <see cref="IDriverInputSink"/> half of <see cref="NetVehicleAxisRelay"/>: what a
    /// networked driver gets when their body has no <c>FpsActorController</c> to install a
    /// <c>NetInputSource</c> onto. Ledger <b>X-46</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A second sink rather than a second branch inside the first.</b>
    /// <see cref="NetDriverInputSink"/> is about remembering and restoring an
    /// <c>IInputSource</c>; this one is about publishing two vectors. Folded together they would
    /// share a constructor that half-initialises whichever half it is not, and the null checks
    /// that follow would be indistinguishable from a bug.
    /// </para>
    /// <para>
    /// <b><see cref="Detach"/> centres rather than destroys.</b> A body enters and leaves seats
    /// all match; removing the component per exit would allocate one per entry on a path
    /// <c>ServerVehicleInputBridge</c> already keeps a dictionary specifically to avoid churning.
    /// Centring also clears <see cref="NetVehicleAxisRelay.Driving"/>, so a relay left on a body
    /// that has got out reads as "nobody is driving this" rather than as a held stick.
    /// </para>
    /// </remarks>
    internal sealed class NetRelayDriverInputSink : IDriverInputSink
    {
        private readonly NetVehicleAxisRelay _relay;

        internal NetRelayDriverInputSink(NetVehicleAxisRelay relay) => _relay = relay;

        /// <inheritdoc />
        public bool Exists => _relay != null;

        /// <inheritdoc />
        public void SetAxes(
            float steer, float throttle,
            float heliYaw, float heliCollective, float heliRoll, float heliPitch)
        {
            if (_relay == null) return;

            _relay.SetAxes(
                new Vector2(steer, throttle),
                new Vector4(heliYaw, heliCollective, heliRoll, heliPitch));
        }

        /// <inheritdoc />
        public void Centre()
        {
            if (_relay != null) _relay.Centre();
        }

        /// <inheritdoc />
        public void Detach()
        {
            if (_relay != null) _relay.Centre();
        }
    }
}
