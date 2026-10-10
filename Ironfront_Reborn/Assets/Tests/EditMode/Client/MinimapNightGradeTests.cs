using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// One moonlight for every map's night picture (owner, 2026-10-11): Island and Dustbowl grade the
    /// original game's live shot at run time with the same <c>MinimapNightGrade</c> the baker gave
    /// Forest Lake's night, so the three maps look alike after dark.
    /// </summary>
    /// <remarks>
    /// Reached by name: this assembly cannot reference Assembly-CSharp, where the grade lives beside
    /// <c>MinimapCamera</c>.
    /// </remarks>
    public sealed class MinimapNightGradeTests
    {
        private const string Folder = "Assets/Textures/Minimap/";

        [Test]
        public void ForestLakesBakedNightIsTheSharedGradeOfItsBakedDay()
        {
            Texture2D day = Decode(Folder + "ForestLake_Minimap.png");
            Texture2D night = Decode(Folder + "ForestLake_Minimap_Night.png");
            Assert.AreEqual(day.width, night.width);

            Color32[] dayPixels = day.GetPixels32();
            Color32[] nightPixels = night.GetPixels32();
            var graded = (Color32[])dayPixels.Clone();
            Grade(graded);

            int worst = 0, samples = 0;
            long total = 0;
            for (int i = 0; i < graded.Length; i += 37)
            {
                int error = Math.Max(Math.Abs(graded[i].r - nightPixels[i].r),
                    Math.Max(Math.Abs(graded[i].g - nightPixels[i].g), Math.Abs(graded[i].b - nightPixels[i].b)));
                worst = Math.Max(worst, error);
                total += error;
                samples++;
            }
            // The baker grades its unrounded day; this grades the day as saved, so a pixel may round
            // the other way (measured: worst 1, mean 0.16). The night is dark, so a changed grade moves
            // a channel by only a level or two: saturation 0.45 for 0.35 measured worst 2, mean 1.47.
            Assert.LessOrEqual(worst, 1, "the night picture was not made with MinimapNightGrade");
            Assert.Less(total / (double)samples, 0.5, "the night picture was not made with MinimapNightGrade");
        }

        [Test]
        public void TheGradeIsDarkAndBlueAndKeepsBrightGroundBrighter()
        {
            var pixels = new[] { new Color32(200, 180, 120, 255), new Color32(60, 90, 40, 255) };
            Grade(pixels);

            Assert.Less(pixels[0].r, 60);
            Assert.Greater(pixels[0].b, pixels[0].r, "the moon is blue");
            Assert.Greater(pixels[0].g + pixels[0].b, pixels[1].g + pixels[1].b, "a road catches more moon than a field");
        }

        private static void Grade(Color32[] pixels)
        {
            Type grade = Type.GetType("MinimapNightGrade, Assembly-CSharp", throwOnError: true);
            MethodInfo apply = grade.GetMethod("Apply", new[] { typeof(Color32[]) });
            Assert.IsNotNull(apply, "MinimapNightGrade.Apply(Color32[]) is gone");
            apply.Invoke(null, new object[] { pixels });
        }

        private static Texture2D Decode(string assetPath)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            Assert.IsTrue(texture.LoadImage(File.ReadAllBytes(assetPath)), assetPath);
            return texture;
        }
    }
}
