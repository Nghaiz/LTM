using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// The original-game presentation attached to a replicated actor proxy. Implemented in
    /// Assembly-CSharp so the net client can show authored weapons and team materials without
    /// referencing the original game's Actor/Weapon types.
    /// </summary>
    public interface IRemoteActorPresentation
    {
        bool Exists { get; }
        void ApplyTeam(byte team);
        void SetVisible(bool visible);
        IGameplayWeapon EquipWeapon(byte networkId, Transform weaponParent);

        /// <summary>
        /// Shows or hides the equipped weapon alone, leaving the body as it is: a swimmer's gun
        /// is put away, as the original puts it away in water.
        /// </summary>
        void SetWeaponShown(bool shown);
    }
}
