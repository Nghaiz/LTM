using System.IO;
using Ironfront.Rendering.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ironfront.Rendering.Tests
{
    /// <summary>
    /// Every shader a GPU-drawn terrain detail is drawn with has its procedural-instancing copy, in
    /// step with it, compiling, and kept in builds by a material of its own.
    /// </summary>
    public sealed class InstancedDetailShaderTests
    {
        private const string Folder = "Assets/Scripts/Rendering/Resources/InstancedDetails";

        [Test]
        public void TheDetailsOfTheMapsAreDrawnWithShadersThatHaveCopies()
        {
            CollectionAssert.Contains(ShaderNames(), "M_Foliage_Wind", "Setup: Forest Lake's grass shader was not found among the detail shaders");
        }

        [Test]
        public void EveryCopyMatchesItsOriginal()
        {
            foreach (Shader shader in InstancedTreeShaderGenerator.DetailSourceShaders())
            {
                string path = InstancedTreeShaderGenerator.CopyPath(ProceduralShaderCopy.Details, shader);
                Assert.IsTrue(File.Exists(path), $"'{shader.name}' has no instanced copy: run Ironfront > Generate instanced detail shaders");
                Assert.AreEqual(InstancedTreeShaderGenerator.Expected(ProceduralShaderCopy.Details, shader), File.ReadAllText(path).Replace("\r\n", "\n"),
                    $"the instanced copy of '{shader.name}' is out of step with it: run Ironfront > Generate instanced detail shaders");
            }
        }

        [Test]
        public void EveryCopyCompilesAndHasAMaterialThatKeepsItsVariants()
        {
            foreach (Shader shader in InstancedTreeShaderGenerator.DetailSourceShaders())
            {
                var copy = AssetDatabase.LoadAssetAtPath<Shader>(InstancedTreeShaderGenerator.CopyPath(ProceduralShaderCopy.Details, shader));
                Assert.IsNotNull(copy, $"'{shader.name}' has no instanced copy");
                Assert.IsTrue(copy.isSupported, $"the instanced copy of '{shader.name}' is not supported here");
                foreach (ShaderMessage message in ShaderUtil.GetShaderMessages(copy))
                    Assert.AreNotEqual(UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error, message.severity,
                        $"the instanced copy of '{shader.name}' does not compile: {message.message}");

                var keeper = AssetDatabase.LoadAssetAtPath<Material>(InstancedTreeShaderGenerator.MaterialPath(ProceduralShaderCopy.Details, shader));
                Assert.IsNotNull(keeper, $"the instanced copy of '{shader.name}' has no material, so a build strips its instancing variants");
                Assert.AreEqual(copy, keeper.shader);
                Assert.IsTrue(keeper.enableInstancing, $"the material of the instanced copy of '{shader.name}' does not instance");
            }
        }

        [Test]
        public void ABuiltInShaderIsNotGenerated()
        {
            CollectionAssert.DoesNotContain(ShaderNames(), "Standard",
                "the built-in Standard has no source to generate from: its copy is written by hand");
        }

        [Test]
        public void EveryShaderOfTheDetailFolderCompilesAndHasAMaterialThatKeepsItsVariants()
        {
            string[] shaders = Directory.GetFiles(Folder, "*.shader");
            CollectionAssert.Contains(System.Array.ConvertAll(shaders, Path.GetFileName), "Standard.shader",
                "the hand-written copy of Standard is gone: Forest Lake's rocks fall back to the CPU path");
            foreach (string file in shaders)
            {
                string path = file.Replace('\\', '/');
                var copy = AssetDatabase.LoadAssetAtPath<Shader>(path);
                Assert.IsNotNull(copy, path);
                Assert.IsFalse(ShaderUtil.ShaderHasError(copy), $"'{path}' does not compile");
                var keeper = AssetDatabase.LoadAssetAtPath<Material>(Path.ChangeExtension(path, ".mat").Replace('\\', '/'));
                Assert.IsNotNull(keeper, $"'{path}' has no material, so a build strips its instancing variants");
                Assert.AreEqual(copy, keeper.shader);
                Assert.IsTrue(keeper.enableInstancing);
            }
        }

        [Test]
        public void NoDetailCopyHasAnAdditiveLightPass()
        {
            foreach (string file in Directory.GetFiles(Folder, "*.shader"))
            {
                string path = file.Replace('\\', '/');
                var copy = AssetDatabase.LoadAssetAtPath<Shader>(path);
                for (int pass = 0; pass < copy.passCount; pass++)
                    Assert.AreNotEqual("ForwardAdd", copy.FindPassTagValue(pass, new UnityEngine.Rendering.ShaderTagId("LightMode")).name,
                        $"'{path}' lights details with point lights the terrain never lit them with, a pass of every blade per light");
            }
        }

        [Test]
        public void TheCopyIsTheOriginalWithTheDetailSetupAndTheSunAlone()
        {
            const string source = "Shader \"Grass\"\n{\n\tSubShader\n\t{\n\t\tCGPROGRAM\n\t\t#pragma target 3.0\n"
                                  + "\t\t#pragma shader_feature_local _BEND_ON\n\t\t#pragma surface surf Standard addshadow \n\t\tENDCG\n\t}\n}\n";

            string copy = ProceduralShaderCopy.Details.Transform(source, "Grass", "Assets/Grass.shader");

            StringAssert.Contains("Shader \"Hidden/Ironfront/InstancedDetails/Grass\"", copy);
            StringAssert.Contains("\t\t#pragma target 3.0\n\t\t#include \"Assets/Scripts/Rendering/DetailInstancing.cginc\"\n"
                                  + "\t\t#pragma instancing_options procedural:DetailInstancingSetup\n", copy);
            StringAssert.Contains("#pragma multi_compile_local _ _BEND_ON", copy);
            StringAssert.Contains("#pragma surface surf Standard addshadow noforwardadd\n", copy);
            StringAssert.Contains("Ironfront > Generate instanced detail shaders", copy);
        }

        [Test]
        public void AShaderThatIsNotASurfaceShaderGetsNoDetailCopy()
        {
            const string source = "Shader \"Lit\"\n{\n\tSubShader\n\t{\n\t\tPass\n\t\t{\n\t\t\tCGPROGRAM\n\t\t\t#pragma target 3.0\n\t\t\tENDCG\n\t\t}\n\t}\n}\n";
            Assert.Throws<System.ArgumentException>(() => ProceduralShaderCopy.Details.Transform(source, "Lit", "Assets/Lit.shader"),
                "a shader whose additive pass cannot be left out was copied anyway");
        }

        private static System.Collections.Generic.List<string> ShaderNames() =>
            InstancedTreeShaderGenerator.DetailSourceShaders().ConvertAll(shader => shader.name);
    }
}
