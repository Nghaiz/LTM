#nullable enable

using System;
using System.Collections.Generic;

namespace Ironfront.Net.Unity.Client.Menu
{
    public static class MenuNavigationCycle
    {
        public static int NextIndex(int currentIndex, bool backwards, IReadOnlyList<bool> canSelect)
        {
            if (canSelect == null) throw new ArgumentNullException(nameof(canSelect));
            int count = canSelect.Count;
            if (count == 0) return -1;

            bool hasCurrent = currentIndex >= 0 && currentIndex < count;
            for (int step = 0; step < count; step++)
            {
                int candidate = hasCurrent
                    ? (currentIndex + (backwards ? -(step + 1) : step + 1) + count) % count
                    : backwards ? count - 1 - step : step;
                if (canSelect[candidate]) return candidate;
            }

            return -1;
        }
    }
}
