#nullable enable

using System;
using System.Collections.Generic;

namespace Ironfront.Net.Unity.Client.Menu
{
    /// <summary>Chooses the next eligible control in a wrapping menu navigation order.</summary>
    public static class MenuNavigationCycle
    {
        public static int NextIndex(
            int currentIndex,
            bool backwards,
            IReadOnlyList<bool> canSelect)
        {
            if (canSelect == null) throw new ArgumentNullException(nameof(canSelect));

            int count = canSelect.Count;
            if (count == 0) return -1;

            bool hasCurrent = currentIndex >= 0 && currentIndex < count;
            for (int step = 0; step < count; step++)
            {
                int candidate;
                if (!hasCurrent)
                {
                    candidate = backwards ? count - 1 - step : step;
                }
                else
                {
                    int offset = backwards ? -(step + 1) : step + 1;
                    candidate = (currentIndex + offset + count) % count;
                }

                if (canSelect[candidate]) return candidate;
            }

            return -1;
        }
    }
}
