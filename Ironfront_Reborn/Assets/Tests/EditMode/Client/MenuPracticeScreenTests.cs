#nullable enable

using System.Linq;
using Ironfront.Net.Configuration;
using Ironfront.Net.Unity.Client.Menu;
using NUnit.Framework;

namespace Ironfront.Net.Unity.Client.Tests
{
    public sealed class MenuPracticeScreenTests
    {
        [Test]
        public void MapLabelsComeOnlyFromTheCurrentMapCatalog()
        {
            string[] expected = MapCatalog.All.Select(entry => entry.DisplayName).ToArray();

            string[] actual = MenuPracticeScreen.CurrentMapLabels();

            CollectionAssert.AreEqual(expected, actual);
            CollectionAssert.DoesNotContain(actual, "ARCHIPELAGO");
            CollectionAssert.DoesNotContain(actual, "COASTLINE");
            CollectionAssert.AreEqual(
                MapCatalog.All.Select(entry => entry.SceneName).ToArray(),
                MenuPracticeScreen.CurrentMapScenes());
        }
    }
}
