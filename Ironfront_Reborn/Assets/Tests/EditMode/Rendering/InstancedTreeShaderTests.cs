using System.IO;
using Ironfront.Rendering.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ironfront.Rendering.Tests
{
    /// <summary>
    /// Every shader a terrain tree is drawn with has its procedural-instancing copy, in step with it,
    /// compiling, and kept in builds by a material of its own.
    /// </summary>
    public sealed class InstancedTreeShaderTests
    {
        [Test]
        public void TheTreesOfTheMapsAreDrawnWithShadersThatHaveCopies()
        {
            Assert.IsNotEmpty(InstancedTreeShaderGenerator.SourceShaders(), "Setup: no terrain tree shader found in the project");
        }

        [Test]
        public void EveryCopyMatchesItsOriginal()
        {
            foreach (Shader shader in InstancedTreeShaderGenerator.SourceShaders())
            {
                string path = InstancedTreeShaderGenerator.CopyPath(shader);
                Assert.IsTrue(File.Exists(path), $"'{shader.name}' has no instanced copy: run Ironfront > Generate instanced tree shaders");
                Assert.AreEqual(InstancedTreeShaderGenerator.Expected(shader), File.ReadAllText(path).Replace("\r\n", "\n"),
                    $"the instanced copy of '{shader.name}' is out of step with it: run Ironfront > Generate instanced tree shaders");
            }
        }

        [Test]
        public void EveryCopyCompilesAndHasAMaterialThatKeepsItsVariants()
        {
            foreach (Shader shader in InstancedTreeShaderGenerator.SourceShaders())
            {
                var copy = AssetDatabase.LoadAssetAtPath<Shader>(InstancedTreeShaderGenerator.CopyPath(shader));
                Assert.IsNotNull(copy, $"'{shader.name}' has no instanced copy");
                Assert.IsTrue(copy.isSupported, $"the instanced copy of '{shader.name}' is not supported here");
                foreach (ShaderMessage message in ShaderUtil.GetShaderMessages(copy))
                    Assert.AreNotEqual(UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error, message.severity,
                        $"the instanced copy of '{shader.name}' does not compile: {message.message}");

                var keeper = AssetDatabase.LoadAssetAtPath<Material>(InstancedTreeShaderGenerator.MaterialPath(shader));
                Assert.IsNotNull(keeper, $"the instanced copy of '{shader.name}' has no material, so a build strips its instancing variants");
                Assert.AreEqual(copy, keeper.shader);
                Assert.IsTrue(keeper.enableInstancing, $"the material of the instanced copy of '{shader.name}' does not instance");
            }
        }

        [Test]
        public void TheCopyIsTheOriginalWithTheProceduralLinesAndEveryKeywordCompiled()
        {
            const string source =
                "// Made with Amplify Shader Editor v1.9.7.1\n// Available at the Unity Asset Store\n"
                + "Shader \"Tree\"\n{\n\tSubShader\n\t{\n\t\tCGPROGRAM\n\t\t#pragma target 3.0\n"
                + "\t\t#pragma shader_feature_local _BEND_ON\n\t\tENDCG\n\t}\n}\n/*ASEBEGIN\ngraph\nASEEND*/\n";

            string copy = TreeShaderVariants.Transform(source, "Tree", "Assets/Tree.shader");

            StringAssert.Contains("Shader \"" + TreeShaderVariants.NamePrefix + "Tree\"", copy);
            StringAssert.Contains("\t\t#pragma target 3.0\n\t\t#include \"Assets/Scripts/Rendering/TreeInstancing.cginc\"\n"
                                  + "\t\t#pragma instancing_options procedural:TreeInstancingSetup\n", copy);
            StringAssert.Contains("#pragma multi_compile_local _ _BEND_ON", copy);
            StringAssert.DoesNotContain("ASEBEGIN", copy);
            StringAssert.DoesNotContain("Made with Amplify", copy);
        }
    }
}
