using UnityEngine;
using UnityStandardAssets.Characters.FirstPerson;

namespace Ironfront.Net.Unity.Bindings
{
    /// <summary>
    /// The predefined-assembly half of <see cref="ILegacyMovementProbe"/>. Phase C4d.
    /// </summary>
    /// <remarks>
    /// This file is the only thing in the tree that names
    /// <c>UnityStandardAssets.Characters.FirstPerson.FirstPersonController</c> outside
    /// <c>Assembly-CSharp-firstpass</c> itself. See <see cref="ILegacyMovementProbe"/> for why a
    /// second predefined assembly needed a seam of its own, and for the scan blind spot that hid
    /// it until the compiler found it.
    /// </remarks>
    internal sealed class LegacyMovementProbeBinding : ILegacyMovementProbe
    {
        private readonly FirstPersonController _controller;

        internal LegacyMovementProbeBinding(FirstPersonController controller)
            => _controller = controller;

        /// <inheritdoc/>
        public bool IsDriving
            => _controller != null && _controller.enabled && _controller.inputEnabled;

        /// <inheritdoc/>
        public bool IsSprinting => _controller != null && _controller.sprinting;
    }
}
