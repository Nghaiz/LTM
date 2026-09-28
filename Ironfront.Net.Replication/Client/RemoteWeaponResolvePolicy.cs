namespace Ironfront.Net.Replication.Client
{
    /// <summary>Decides whether a remote actor must (re)resolve its weapon presentation.</summary>
    public static class RemoteWeaponResolvePolicy
    {
        /// <summary>
        /// A matching id is not enough to skip resolution: manager/bootstrap ordering can make
        /// the first lookup fail. Keep retrying until that id has produced a live presentation.
        /// </summary>
        public static bool ShouldResolve(byte requestedId, byte appliedId, bool activeWeaponExists)
            => requestedId != appliedId || !activeWeaponExists;
    }
}
