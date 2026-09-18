#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Ironfront.Net.Unity
{
    public readonly struct DisplayResolutionOption
    {
        public DisplayResolutionOption(int width, int height)
        {
            Width = width;
            Height = height;
        }

        public int Width { get; }
        public int Height { get; }
        public string Label => $"{Width} × {Height}";
    }

    public static class DisplayResolutionCatalog
    {
        public static DisplayResolutionOption[] Build(IEnumerable<DisplayResolutionOption> source)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            HashSet<(int Width, int Height)> seen = new HashSet<(int Width, int Height)>();
            List<DisplayResolutionOption> unique = new List<DisplayResolutionOption>();

            foreach (DisplayResolutionOption option in source)
            {
                if (option.Width <= 0 || option.Height <= 0)
                    continue;

                if (seen.Add((option.Width, option.Height)))
                    unique.Add(option);
            }

            return unique
                .OrderBy(option => (long)option.Width * option.Height)
                .ToArray();
        }

        public static int FindBestIndex(
            IReadOnlyList<DisplayResolutionOption> options,
            int width,
            int height)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (options.Count == 0)
                return -1;

            int bestIndex = 0;
            long bestDistance = DistanceSquared(options[0], width, height);

            for (int index = 0; index < options.Count; index++)
            {
                DisplayResolutionOption option = options[index];
                if (option.Width == width && option.Height == height)
                    return index;

                long distance = DistanceSquared(option, width, height);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = index;
                }
            }

            return bestIndex;
        }

        private static long DistanceSquared(DisplayResolutionOption option, int width, int height)
        {
            long widthDelta = (long)option.Width - width;
            long heightDelta = (long)option.Height - height;
            return widthDelta * widthDelta + heightDelta * heightDelta;
        }
    }
}
