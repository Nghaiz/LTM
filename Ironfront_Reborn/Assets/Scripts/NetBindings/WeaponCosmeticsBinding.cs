/// <summary>
/// The <c>Assembly-CSharp</c> half of <see cref="Ironfront.Net.Unity.IGameplayWeapon"/>.
/// Phase C4a.
/// </summary>
/// <remarks>
/// One member wide, because <c>PlayFireCosmetics()</c> is already a public method with the right
/// signature and satisfies the interface unwritten. Only the liveness flag needs declaring — see
/// <c>IGameplayWeapon.Exists</c> for why an interface reference cannot answer it for itself.
/// </remarks>
public partial class Weapon
{
    /// <inheritdoc/>
    public bool Exists => this != null;
}
