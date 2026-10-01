namespace Ironfront.Net.Replication.Movement
{
    /// <summary>
    /// The loaded map's bounded water -- lakes and rivers, as against its sea -- as a height field.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A sea is one height; a lake is a height somewhere.</b> <see cref="MovementCore.WaterHeight"/>
    /// describes water that covers everything below it, which is all Dustbowl and Island have. A
    /// lake above lower ground (Forest Lake) cannot be described that way: read as a sea level it
    /// would flood every valley beneath it, and left out it would leave the lake a floor players
    /// walk along with no breath to lose while the bots swim it. So a map registers its bounded
    /// bodies here, and <see cref="MovementCore.SurfaceAt"/> takes the higher of the two.
    /// </para>
    /// <para>
    /// <b>Both sides must answer the same.</b> The client's prediction and the server's replay swim
    /// against this surface, so it has to come from the map itself (the game rasterises each body's
    /// mesh once at load), never from anything either side works out on its own.
    /// </para>
    /// </remarks>
    public interface IBoundedWater
    {
        /// <summary>
        /// The highest bounded surface over (<paramref name="x"/>, <paramref name="z"/>), or
        /// negative infinity where no bounded body covers that spot.
        /// </summary>
        float SurfaceAt(float x, float z);
    }
}
