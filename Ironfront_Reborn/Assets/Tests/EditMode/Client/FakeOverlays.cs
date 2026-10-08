using System;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>Stands in for the overlay host, which installs itself only in play mode.</summary>
    internal sealed class FakeOverlays : IDisposable
    {
        private readonly Action<OverlayPage> _opener = GameOverlays.Opener;
        private readonly Action _closer = GameOverlays.Closer;
        private readonly Func<OverlayPage> _showing = GameOverlays.Showing;
        private readonly Func<OverlayPage, bool> _available = GameOverlays.Available;

        public OverlayPage Current { get; private set; }

        public static FakeOverlays Install()
        {
            var fake = new FakeOverlays();
            GameOverlays.Opener = page => fake.Current = page;
            GameOverlays.Closer = () => fake.Current = OverlayPage.None;
            GameOverlays.Showing = () => fake.Current;
            GameOverlays.Available = _ => true;
            return fake;
        }

        public void Dispose()
        {
            GameOverlays.Opener = _opener;
            GameOverlays.Closer = _closer;
            GameOverlays.Showing = _showing;
            GameOverlays.Available = _available;
        }
    }
}
