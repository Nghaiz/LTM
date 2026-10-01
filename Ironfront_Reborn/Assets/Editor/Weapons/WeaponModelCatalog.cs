using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Ironfront.Tools.Weapons
{
    /// <summary>
    /// The new weapon models under <c>Assets/WeaponModels</c>: how each file imports, and the
    /// materials its surfaces are drawn with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The model files are sources, not runtime assets.</b> Nothing in a scene or a prefab refers
    /// to them; <see cref="ReskinWeapons"/> bakes their triangles into meshes of their own. So they
    /// import readable and with no rig, animation, camera or light, and they stay out of a build.
    /// </para>
    /// <para>
    /// <b>The project renders in Gamma space with the Built-in pipeline.</b> Every material is the
    /// Standard shader (Specular setup for the one model shipped with specular maps). The packs'
    /// metallic and roughness maps were packed into Standard's metallic-in-R, smoothness-in-A layout
    /// when the textures were brought in; materials with no maps take their colours from the
    /// source files, which for the SMAW's OBJ are linear values converted here.
    /// </para>
    /// </remarks>
    public static class WeaponModelCatalog
    {
        public const string Root = "Assets/WeaponModels";

        public sealed class MaterialDef
        {
            public string Name;
            public string[] Sources;
            public Color Color = Color.white;
            public string Albedo, Normal, MetallicGloss, SpecGloss;
            public float Metallic, Smoothness = 0.5f;
            public Color Emission = Color.black;
        }

        public sealed class ModelDef
        {
            public string Slug;
            public string File;
            public bool CalculateNormals;
            public readonly List<MaterialDef> Materials = new List<MaterialDef>();
            public string Path => Root + "/" + Slug + "/" + File;
        }

        private static MaterialDef Mapped(string name, string albedo, string normal, string metallicGloss, params string[] sources)
            => new MaterialDef { Name = name, Sources = sources, Albedo = albedo, Normal = normal, MetallicGloss = metallicGloss, Smoothness = 1f };

        private static MaterialDef Plain(string name, Color color, float metallic, float smoothness, params string[] sources)
            => new MaterialDef { Name = name, Sources = sources, Color = color, Metallic = metallic, Smoothness = smoothness };

        private static Color Rgb(float r, float g, float b) => new Color(r, g, b, 1f);

        /// <summary>A colour Blender wrote into an MTL (linear), as the Gamma-space colour it showed.</summary>
        private static Color FromLinear(float r, float g, float b) => new Color(r, g, b, 1f).gamma;

        public static readonly ModelDef[] Models = BuildModels();

        private static ModelDef[] BuildModels()
        {
            var models = new List<ModelDef>();
            ModelDef M(string slug, string file, bool calculateNormals = false)
            {
                var m = new ModelDef { Slug = slug, File = file, CalculateNormals = calculateNormals };
                models.Add(m);
                return m;
            }

            M("AKM", "AKM.fbx").Materials.Add(Mapped("AKM", "AKM_Albedo", "AKM_Normal", "AKM_MetallicGloss", "AK_mat"));

            ModelDef p226 = M("P226", "P226.fbx");
            p226.Materials.Add(Plain("P226_Steel", Rgb(0.58f, 0.58f, 0.6f), 0.85f, 0.62f, "SIG Sauer P226 X-Five 1 Material 1"));
            p226.Materials.Add(Plain("P226_Black", Rgb(0.07f, 0.07f, 0.075f), 0.6f, 0.45f, "SIG Sauer P226 X-Five 1 Material 2"));
            p226.Materials.Add(Plain("P226_Grip", Rgb(0.05f, 0.05f, 0.05f), 0f, 0.3f, "SIG Sauer P226 X-Five 1 Material 3"));
            p226.Materials.Add(Plain("P226_Parts", Rgb(0.16f, 0.16f, 0.17f), 0.7f, 0.5f, "SIG Sauer P226 X-Five 1 Material 4"));
            p226.Materials.Add(new MaterialDef
            {
                Name = "P226_FiberOptic", Sources = new[] { "SIG Sauer P226 X-Five 1 Material 5" },
                Color = Rgb(0.05f, 0.35f, 0.06f), Smoothness = 0.6f, Emission = Rgb(0.08f, 0.5f, 0.12f),
            });
            p226.Materials.Add(Plain("P226_Brass", Rgb(0.72f, 0.52f, 0.22f), 1f, 0.65f, "9x19mm Round 1 Material 1"));
            p226.Materials.Add(Plain("P226_Copper", Rgb(0.62f, 0.36f, 0.2f), 1f, 0.6f, "9x19mm Round 1 Material 2"));

            M("Obsidian9", "Obsidian9.fbx").Materials.Add(Mapped("Obsidian9", "Obsidian9_Albedo", "Obsidian9_Normal", "Obsidian9_MetallicGloss",
                "Material #26", "Material #29", "Material #30", "Material #31"));

            ModelDef fp6 = M("FabarmFP6", "FabarmFP6.fbx");
            fp6.Materials.Add(Plain("FP6_Steel", Rgb(0.08f, 0.08f, 0.085f), 0.7f, 0.5f, "FABARM FP6 1 Material 1"));
            fp6.Materials.Add(Plain("FP6_Polymer", Rgb(0.075f, 0.075f, 0.075f), 0f, 0.3f, "FABARM FP6 1 Material 2"));
            fp6.Materials.Add(Plain("FP6_Metal", Rgb(0.2f, 0.2f, 0.21f), 0.8f, 0.55f, "FABARM FP6 1 Material 3"));
            fp6.Materials.Add(Plain("FP6_Black", Rgb(0.05f, 0.05f, 0.05f), 0.5f, 0.4f, "FABARM FP6 1 Material 4"));
            fp6.Materials.Add(Plain("FP6_Follower", Rgb(0.5f, 0.05f, 0.02f), 0f, 0.4f, "FABARM FP6 1 Material 5"));
            fp6.Materials.Add(Plain("FP6_ShellHull", Rgb(0.55f, 0.06f, 0.04f), 0f, 0.5f, "12 Gauge Shell 1 Material 1"));
            fp6.Materials.Add(Plain("FP6_ShellBrass", Rgb(0.7f, 0.5f, 0.2f), 1f, 0.65f, "12 Gauge Shell 1 Material 2"));
            fp6.Materials.Add(Plain("FP6_Sights", Rgb(0.06f, 0.06f, 0.06f), 0.6f, 0.45f,
                "LPA Sights SHS02W 1 Material 1", "LPA Sights SHS02W 1 Material 2", "LPA Sights SHS02W 1 Material 3"));
            fp6.Materials.Add(Plain("FP6_SightDot", Rgb(0.95f, 0.95f, 0.95f), 0f, 0.3f, "LPA Sights SHS02W 1 Material 4"));

            // The SMAW's MTL: Blender's base colours, which are linear.
            ModelDef smaw = M("SMAW", "SMAW.obj");
            (string mtl, Color c, float metallic, float smooth)[] smawMaterials =
            {
                ("SMAW_1_Material_1", FromLinear(0.114947f, 0.179049f, 0.057717f), 0f, 0.35f),
                ("SMAW_1_Material_2", Rgb(0.035f, 0.035f, 0.035f), 0.2f, 0.35f),
                ("SMAW_1_Material_3", FromLinear(0.112368f, 0.112368f, 0.112368f), 0.3f, 0.4f),
                ("SMAW_1_Material_4", FromLinear(0.252712f, 0.252712f, 0.252712f), 0.4f, 0.45f),
                ("SMAW_1_Material_5", FromLinear(0.053179f, 0.053179f, 0.053179f), 0.3f, 0.4f),
                ("SMAW_1_Material_6", FromLinear(0.011301f, 0.011301f, 0.011301f), 0.2f, 0.35f),
                ("SMAW_1_Material_7", FromLinear(0.044314f, 0.044314f, 0.044314f), 0.2f, 0.35f),
                ("SMAW_1_Material_8", FromLinear(0.021757f, 0.021757f, 0.021757f), 0.3f, 0.4f),
                ("SMAW_1_Material_9", FromLinear(0.800259f, 0.684613f, 0f), 0f, 0.4f),
                ("SMAW_1_Material_10", FromLinear(0.8f, 0.8f, 0.8f), 0f, 0.3f),
                ("SMAW_1_Material_11", FromLinear(0.800142f, 0.001339f, 0f), 0f, 0.4f),
                ("SMAW_1_Material_12", Rgb(0.01f, 0.01f, 0.01f), 0f, 0.2f),
                ("SMAW_1_Material_13", FromLinear(0.540382f, 0.275206f, 0.031629f), 0.8f, 0.5f),
            };
            foreach (var m in smawMaterials)
                smaw.Materials.Add(Plain("SMAW_" + m.mtl.Substring("SMAW_1_Material_".Length), m.c, m.metallic, m.smooth, m.mtl));

            ModelDef awm = M("AWM", "AWM.fbx");
            awm.Materials.Add(Mapped("AWM", "AWM_Albedo", "AWM_Normal", "AWM_MetallicGloss", "lambert3"));
            awm.Materials.Add(Mapped("AWM_Scope", "AWM_Scope_Albedo", "AWM_Scope_Normal", "AWM_Scope_MetallicGloss", "lambert2"));
            awm.Materials.Add(Plain("AWM_Lens", Rgb(0.02f, 0.03f, 0.05f), 0f, 0.95f, "lambert1"));

            M("M26", "M26.fbx").Materials.Add(Mapped("M26", "M26_Albedo", null, "M26_MetallicGloss", "M61_grenade"));
            M("M84", "M84.fbx").Materials.Add(Mapped("M84", "M84_Albedo", "M84_Normal", "M84_MetallicGloss", "Material.009"));
            M("Binoculars", "Binoculars.fbx").Materials.Add(Mapped("Binoculars", "Binoculars_Albedo", "Binoculars_Normal", "Binoculars_MetallicGloss", "Binoculars"));
            M("AmmoBox556", "AmmoBox556.fbx").Materials.Add(Mapped("AmmoBox556", "AmmoBox556_Albedo", "AmmoBox556_Normal", "AmmoBox556_MetallicGloss", "_Mat_Ammo_Box_556x45mm"));

            ModelDef kit = M("FirstAidKit", "FirstAidKit.fbx");
            kit.Materials.Add(Mapped("FirstAidKit_Bottom", "FirstAidKit_Bottom_Albedo", "FirstAidKit_Bottom_Normal", "FirstAidKit_Bottom_MetallicGloss", "bottom"));
            kit.Materials.Add(Mapped("FirstAidKit_Top", "FirstAidKit_Top_Albedo", "FirstAidKit_Top_Normal", "FirstAidKit_Top_MetallicGloss", "top"));
            kit.Materials.Add(Mapped("FirstAidKit_Button", "FirstAidKit_Button_Albedo", "FirstAidKit_Button_Normal", "FirstAidKit_Button_MetallicGloss", "button"));

            // The pack ships the launcher's texture and the tube's, not the command unit's.
            ModelDef javelin = M("Javelin", "Javelin.obj");
            javelin.Materials.Add(new MaterialDef { Name = "Javelin_A", Sources = new[] { "t_wep_launcher__fgm148_javelin_01_a_cs" }, Albedo = "Javelin_A_Albedo", Smoothness = 0.3f });
            javelin.Materials.Add(Plain("Javelin_B", Rgb(0.3f, 0.31f, 0.25f), 0f, 0.3f, "t_wep_launcher__fgm148_javelin_01_b_cs"));
            javelin.Materials.Add(new MaterialDef { Name = "Javelin_Mat69", Sources = new[] { "mat69" }, Albedo = "Javelin_Mat69_Albedo", Smoothness = 0.3f });

            // lambert12SG ships without a base colour; the maps it has are a dark anodised finish.
            ModelDef sig = M("SIG716", "SIG716.obj");
            sig.Materials.Add(Mapped("SIG716_11", "SIG716_11_Albedo", "SIG716_11_Normal", "SIG716_11_MetallicGloss", "new_716_low_lambert11SG"));
            MaterialDef sig12 = Mapped("SIG716_12", null, "SIG716_12_Normal", "SIG716_12_MetallicGloss", "new_716_low_lambert12SG");
            sig12.Color = Rgb(0.11f, 0.11f, 0.115f);
            sig.Materials.Add(sig12);
            sig.Materials.Add(Mapped("SIG716_13", "SIG716_13_Albedo", "SIG716_13_Normal", "SIG716_13_MetallicGloss", "new_716_low_lambert13SG"));

            ModelDef pn = M("PN5x80", "PN5x80.fbx");
            pn.Materials.Add(new MaterialDef { Name = "PN5x80", Sources = new[] { "Cylinder", "Material" }, Albedo = "PN5x80_Albedo", Normal = "PN5x80_Normal", Metallic = 0.3f, Smoothness = 0.4f });
            pn.Materials.Add(new MaterialDef
            {
                Name = "PN5x80_Lens", Sources = new[] { "LightGreenNightVision" },
                Color = Rgb(0.04f, 0.22f, 0.06f), Smoothness = 0.95f, Emission = Rgb(0.02f, 0.18f, 0.04f),
            });

            ModelDef rfb = M("KelTecRFB", "KelTecRFB.obj");
            for (int i = 0; i < 10; i++)
                rfb.Materials.Add(new MaterialDef { Name = "KelTecRFB_Tex" + i, Sources = new[] { "rfb_mat" + i }, Albedo = "KelTecRFB_Tex" + i, Smoothness = 0.25f });

            M("PipeWrench", "PipeWrench.obj", calculateNormals: true).Materials.Add(new MaterialDef
            {
                Name = "PipeWrench", Sources = new[] { "Assem1_LR01" },
                Albedo = "PipeWrench_Albedo", Normal = "PipeWrench_Normal", SpecGloss = "PipeWrench_SpecGloss", Smoothness = 1f,
            });

            return models.ToArray();
        }

        public static ModelDef Model(string slug)
        {
            foreach (ModelDef m in Models) if (m.Slug == slug) return m;
            throw new System.ArgumentException("No weapon model '" + slug + "' in the catalog.");
        }

        // ------------------------------------------------------------------ import

        /// <summary>Imports every texture and model as the bake needs them, and builds the materials.</summary>
        public static void Configure(StringBuilder log)
        {
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (ModelDef model in Models) ConfigureTextures(model, log);
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            foreach (ModelDef model in Models)
            {
                var materials = new Dictionary<string, Material>();
                foreach (MaterialDef def in model.Materials) materials[def.Name] = BuildMaterial(model, def);
                ConfigureModel(model, materials, log);
            }
        }

        private static void ConfigureTextures(ModelDef model, StringBuilder log)
        {
            string folder = Root + "/" + model.Slug + "/Textures";
            if (!AssetDatabase.IsValidFolder(folder)) return;

            int changed = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                string name = System.IO.Path.GetFileNameWithoutExtension(path);
                bool normal = name.EndsWith("_Normal");
                bool mask = name.EndsWith("_MetallicGloss") || name.EndsWith("_SpecGloss");
                bool pixelArt = model.Slug == "KelTecRFB";

                importer.GetSourceTextureWidthAndHeight(out int width, out int height);
                int maxSize = Mathf.NextPowerOfTwo(Mathf.Max(32, Mathf.Max(width, height)));

                var wanted = new
                {
                    type = normal ? TextureImporterType.NormalMap : TextureImporterType.Default,
                    srgb = !normal && !mask,
                    filter = pixelArt ? FilterMode.Point : FilterMode.Trilinear,
                    compression = pixelArt ? TextureImporterCompression.Uncompressed : TextureImporterCompression.Compressed,
                };

                if (importer.textureType == wanted.type && importer.sRGBTexture == wanted.srgb && importer.filterMode == wanted.filter
                    && importer.textureCompression == wanted.compression && importer.maxTextureSize == maxSize && importer.mipmapEnabled
                    && importer.anisoLevel == (pixelArt ? 0 : 4))
                    continue;

                importer.textureType = wanted.type;
                importer.sRGBTexture = wanted.srgb;
                importer.filterMode = wanted.filter;
                importer.textureCompression = wanted.compression;
                importer.maxTextureSize = maxSize;
                importer.mipmapEnabled = true;
                importer.anisoLevel = pixelArt ? 0 : 4;
                importer.alphaSource = mask ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
                importer.alphaIsTransparency = false;
                if (pixelArt) importer.wrapMode = TextureWrapMode.Clamp;
                importer.SaveAndReimport();
                changed++;
            }
            if (changed > 0) log.AppendLine("  textures reimported for " + model.Slug + ": " + changed);
        }

        private static Material BuildMaterial(ModelDef model, MaterialDef def)
        {
            string folder = Root + "/" + model.Slug + "/Materials";
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder(Root + "/" + model.Slug, "Materials");
            string path = folder + "/" + def.Name + ".mat";

            Shader shader = Shader.Find(def.SpecGloss != null ? "Standard (Specular setup)" : "Standard");
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader) { name = def.Name };
                AssetDatabase.CreateAsset(material, path);
            }
            material.shader = shader;
            material.SetColor("_Color", def.Color);

            Texture2D Tex(string name)
            {
                if (name == null) return null;
                string texturePath = FindTexture(model.Slug, name);
                Texture2D texture = texturePath == null ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
                if (texture == null) throw new FileNotFoundException("Texture '" + name + "' of " + model.Slug + " is missing.");
                return texture;
            }

            material.SetTexture("_MainTex", Tex(def.Albedo));

            Texture2D normal = Tex(def.Normal);
            material.SetTexture("_BumpMap", normal);
            material.SetFloat("_BumpScale", 1f);
            SetKeyword(material, "_NORMALMAP", normal != null);

            if (def.SpecGloss != null)
            {
                Texture2D specGloss = Tex(def.SpecGloss);
                material.SetTexture("_SpecGlossMap", specGloss);
                material.SetColor("_SpecColor", new Color(0.2f, 0.2f, 0.2f));
                SetKeyword(material, "_SPECGLOSSMAP", specGloss != null);
                material.SetFloat("_GlossMapScale", def.Smoothness);
                material.SetFloat("_Glossiness", def.Smoothness);
            }
            else
            {
                Texture2D metallicGloss = Tex(def.MetallicGloss);
                material.SetTexture("_MetallicGlossMap", metallicGloss);
                SetKeyword(material, "_METALLICGLOSSMAP", metallicGloss != null);
                material.SetFloat("_Metallic", def.Metallic);
                material.SetFloat("_Glossiness", def.Smoothness);
                material.SetFloat("_GlossMapScale", metallicGloss != null ? def.Smoothness : 1f);
            }
            material.SetFloat("_SmoothnessTextureChannel", 0f);

            bool emissive = def.Emission.maxColorComponent > 0f;
            material.SetColor("_EmissionColor", def.Emission);
            SetKeyword(material, "_EMISSION", emissive);
            material.globalIlluminationFlags = emissive ? MaterialGlobalIlluminationFlags.RealtimeEmissive : MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            material.enableInstancing = true;

            EditorUtility.SetDirty(material);
            return material;
        }

        private static string FindTexture(string slug, string name)
        {
            foreach (string ext in new[] { ".jpg", ".png" })
            {
                string path = Root + "/" + slug + "/Textures/" + name + ext;
                if (System.IO.File.Exists(path)) return path;
            }
            return null;
        }

        private static void SetKeyword(Material material, string keyword, bool on)
        {
            if (on) material.EnableKeyword(keyword);
            else material.DisableKeyword(keyword);
        }

        private static void ConfigureModel(ModelDef model, Dictionary<string, Material> materials, StringBuilder log)
        {
            var importer = (ModelImporter)AssetImporter.GetAtPath(model.Path)
                           ?? throw new FileNotFoundException("Weapon model " + model.Path + " is missing.");

            bool changed = false;
            void Set<T>(T current, T wanted, System.Action<T> apply)
            {
                if (EqualityComparer<T>.Default.Equals(current, wanted)) return;
                apply(wanted);
                changed = true;
            }

            Set(importer.importAnimation, false, v => importer.importAnimation = v);
            Set(importer.animationType, ModelImporterAnimationType.None, v => importer.animationType = v);
            Set(importer.importBlendShapes, false, v => importer.importBlendShapes = v);
            Set(importer.importVisibility, false, v => importer.importVisibility = v);
            Set(importer.importCameras, false, v => importer.importCameras = v);
            Set(importer.importLights, false, v => importer.importLights = v);
            Set(importer.isReadable, true, v => importer.isReadable = v);
            Set(importer.meshCompression, ModelImporterMeshCompression.Off, v => importer.meshCompression = v);
            Set(importer.importNormals, model.CalculateNormals ? ModelImporterNormals.Calculate : ModelImporterNormals.Import, v => importer.importNormals = v);
            Set(importer.importTangents, ModelImporterTangents.CalculateMikk, v => importer.importTangents = v);
            Set(importer.materialImportMode, ModelImporterMaterialImportMode.ImportViaMaterialDescription, v => importer.materialImportMode = v);
            Set(importer.materialLocation, ModelImporterMaterialLocation.InPrefab, v => importer.materialLocation = v);

            var existing = importer.GetExternalObjectMap();
            foreach (MaterialDef def in model.Materials)
            {
                foreach (string source in def.Sources)
                {
                    var id = new AssetImporter.SourceAssetIdentifier(typeof(Material), source);
                    if (existing.TryGetValue(id, out Object mapped) && mapped == materials[def.Name]) continue;
                    importer.AddRemap(id, materials[def.Name]);
                    changed = true;
                }
            }

            if (changed)
            {
                importer.SaveAndReimport();
                log.AppendLine("  model reimported: " + model.Path);
            }
        }
    }
}
