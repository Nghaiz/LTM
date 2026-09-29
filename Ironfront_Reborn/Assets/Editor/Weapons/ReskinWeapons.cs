using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Ironfront.Tools.Weapons
{
    /// <summary>
    /// Replaces the look of every loadout item with the models under <c>Assets/WeaponModels</c>,
    /// and changes nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What changes.</b> The mesh and the materials on the nodes that drew each weapon, and on
    /// the projectiles that share those meshes (the thrown grenades, ammo bag and medipack, the
    /// SMAW's rocket). Each new mesh is baked into its node's own space (see
    /// <see cref="BakedGeometry"/>), so the node, its path, its components and every animation
    /// curve that drives it are the prefab's own.
    /// </para>
    /// <para>
    /// <b>What does not.</b> Muzzles, throw points, IK targets, scope overlays, colliders,
    /// rigidbodies, audio, particle systems, the Animator and every <c>Weapon</c> field. Shots still
    /// leave from the muzzle they always did, thrown items keep their box colliders, and a bot's
    /// weapon (<c>Actor.SpawnWeapon</c>, <c>CullFpsObjects</c>) and a remote player's
    /// (<c>IronfrontNetBindings.EquipWeapon</c>) are built from the same nodes as before.
    /// </para>
    /// <para>
    /// <b>How a model is placed.</b> A gun is turned so its barrel runs down the body node's -Y and
    /// its top up +Z, scaled to the old gun's length and slid so the muzzles meet. Then its sight
    /// is put on the camera line of the weapon's own aim clip: the front-sight tip for iron
    /// sights, the scope's centre for a scope. That is what keeps aiming down the sights exact
    /// with animations authored for the old guns (the RK-44's old front sight sat 0.9 mm off that
    /// line). A weapon aimed through a full-screen overlay (the scoped rifles) is placed by its
    /// bore instead. Everything else is fitted to the old model's box. Moving parts — magazines,
    /// bolts, slides, the pump, pins, the SMAW's rocket — go to the node their animation drives,
    /// placed where they sit on the gun in the hip pose.
    /// </para>
    /// <para>
    /// Idempotent: a second run rebuilds the same meshes into the same assets, whose GUIDs are
    /// kept, so the prefabs' references do not churn.
    /// </para>
    /// </remarks>
    public static class ReskinWeapons
    {
        private const string BakedFolder = WeaponModelCatalog.Root + "/Baked";
        private const string ReportFile = "reskin-weapons.txt";

        /// <summary>Every first-person weapon body in the project points its barrel down -Y, top up +Z.</summary>
        private static readonly Vector3 GunForward = Vector3.down;
        private static readonly Vector3 GunUp = Vector3.forward;

        [MenuItem("Ironfront/Weapons/Reskin weapons with the new models")]
        public static void RunFromMenu() => Execute(exitOnFailure: false);

        /// <summary>The <c>-executeMethod</c> entry point.</summary>
        public static void Run() => Execute(Application.isBatchMode);

        private static void Execute(bool exitOnFailure)
        {
            var log = new StringBuilder();
            bool ok;
            try
            {
                Build(log);
                ok = true;
            }
            catch (Exception ex)
            {
                log.AppendLine("FAILED: " + ex);
                ok = false;
            }

            File.WriteAllText(ReportFile, log.ToString());
            if (ok) Debug.Log("[reskin-weapons]\n" + log);
            else Debug.LogError("[reskin-weapons] FAILED\n" + log);
            if (!ok && exitOnFailure) EditorApplication.Exit(1);
        }

        private static void Build(StringBuilder log)
        {
            log.AppendLine("import:");
            WeaponModelCatalog.Configure(log);
            if (!AssetDatabase.IsValidFolder(BakedFolder)) AssetDatabase.CreateFolder(WeaponModelCatalog.Root, "Baked");

            Rk44(log);
            Sind7(log);
            Eagle76(log);
            BeuAw1(log);
            SlDefender(log);
            SignalDmr(log);
            ReconLrr(log);
            BilScalpel(log);
            Frag(log);
            Spearhead(log);
            Binocs(log);
            AmmoBag(log);
            Medipack(log);
            Wrenches(log);
            Projectiles(log);

            AssetDatabase.SaveAssets();
            log.AppendLine("done.");
        }

        // ------------------------------------------------------------------ weapons

        private static void Rk44(StringBuilder log)
        {
            using (var w = new PrefabEdit("ak", "AK", "ak_hip", log))
            {
                var model = new SourceModel("AKM");
                // The sling is modelled hanging free under the gun; in first person it would cut
                // through both arms, and nothing animates it.
                model.Take("Bend");
                List<ModelPiece> magazine = model.Take("mag");
                List<ModelPiece> bolt = model.Take("bolt");
                List<ModelPiece> body = model.Rest();

                Quaternion turn = AxisMath.Frame(Vector3.back, Vector3.up, GunForward, GunUp);
                Matrix4x4 fit = FitGun(w, "RK-44", body.Concat(magazine).Concat(bolt), turn, w.OldBounds("AK"),
                    Sight.FrontSight, w.AimLine("ak_aim"), "AK/Muzzle Flash", null, log);

                w.Assign("AK", Bake(body, fit));
                w.Assign("AK/Magazine", Bake(magazine, w.NodeFromBody("AK/Magazine") * fit));
                w.Assign("AK/Slide", Bake(bolt, w.NodeFromBody("AK/Slide") * fit));
                w.Save();
            }
        }

        /// <summary>S-IND7 and S-IND7 [SUP]: one pistol, the second with the suppressor node shown.</summary>
        private static void Sind7(StringBuilder log)
        {
            foreach (string prefab in new[] { "mk25", "mk25 suppressed" })
            {
                using (var w = new PrefabEdit(prefab, "MK25", "mk25_hip", log))
                {
                    var model = new SourceModel("P226");
                    // The barrel rides with the slide, so a slide that runs back on a shot does not
                    // leave it standing proud of the frame.
                    List<ModelPiece> slide = model.Take("SIG Sauer P226 X-Five 1 Slide 1", "SIG Sauer P226 X-Five 1 Barrel 1");
                    List<ModelPiece> magazine = model.Take("SIG Sauer P226 X-Five 1 Mag 1");
                    List<ModelPiece> hammer = model.Take("SIG Sauer P226 X-Five 1 Hammer 1");
                    List<ModelPiece> frame = model.Rest();

                    Quaternion turn = AxisMath.Frame(Vector3.forward, Vector3.up, GunForward, GunUp);
                    // The sights are on the slide, so the slide is part of what is measured. Measured
                    // front to back: [SUP]'s muzzle is halfway down its suppressor, and both prefabs
                    // must draw the same pistol.
                    Matrix4x4 fit = FitGun(w, prefab == "mk25" ? "S-IND7" : "S-IND7 [SUP]", frame.Concat(slide), turn,
                        w.OldBounds("MK25", "MK25/Slide"), Sight.FrontSight, w.AimLine("mk25_aim"), "MK25/Muzzle", null, log,
                        frontAtMuzzle: false);

                    w.Assign("MK25", Bake(frame, fit));
                    w.Assign("MK25/Slide", Bake(slide, w.NodeFromBody("MK25/Slide") * fit));
                    w.Assign("MK25/Magazine", Bake(magazine, w.NodeFromBody("MK25/Magazine") * fit));
                    w.Assign("MK25/Hammer", Bake(hammer, w.NodeFromBody("MK25/Hammer") * fit));

                    // Both prefabs carry the suppressor node; only [SUP] shows it. The long
                    // configuration of the Obsidian 9 (two tube sections) is the old one's length.
                    List<ModelPiece> suppressor = new SourceModel("Obsidian9").Take(
                        "LP Obsidian Body001", "LP Obsidian Fixed Mount001", "LP Obsidian Front Cap001",
                        "LP Obsidian Tube 005", "LP Obsidian Tube 006");
                    Quaternion bore = AxisMath.Frame(Vector3.up, Vector3.forward, GunForward, GunUp);
                    Matrix4x4 fitSuppressor = FitBox(suppressor, bore, w.OldMeshBounds("MK25/Suppressor"), 1);
                    w.Assign("MK25/Suppressor", Bake(suppressor, fitSuppressor));
                    w.Save();
                }
            }
        }

        /// <summary>76 EAGLE, the pump shotgun.</summary>
        private static void Eagle76(StringBuilder log)
        {
            using (var w = new PrefabEdit("shotgun", "Shotgun", "shotgun_hip", log))
            {
                var model = new SourceModel("FabarmFP6");
                List<ModelPiece> shell = model.Take("12 Gauge Shell 1");
                List<ModelPiece> pump = model.Take("FABARM FP6 1 Forend 1");
                List<ModelPiece> body = model.Rest();

                Quaternion turn = AxisMath.Frame(Vector3.forward, Vector3.up, GunForward, GunUp);
                Matrix4x4 fit = FitGun(w, "76 EAGLE", body.Concat(pump), turn, w.OldBounds("Shotgun"),
                    Sight.FrontSight, w.AimLine("shotgun_aim"), "Shotgun/Muzzle", null, log);

                w.Assign("Shotgun", Bake(body, fit));
                w.Assign("Shotgun/Pump", Bake(pump, w.NodeFromBody("Shotgun/Pump") * fit));

                // The shell the loading animation carries in the hand is a node of its own.
                w.Assign("Shell", Bake(shell, FitBox(shell, turn, w.OldMeshBounds("Shell"), 1)));
                w.Save();
            }
        }

        /// <summary>BEU AW1, the SMAW: the encased rocket at the back is what the reload swaps.</summary>
        private static void BeuAw1(StringBuilder log)
        {
            using (var w = new PrefabEdit("smaw", "Cylinder", "smaw_hip", log))
            {
                var model = new SourceModel("SMAW");
                List<ModelPiece> rocket = model.Take("SMAW_1_Rocket_Case_1", "SMAW_1_Rocket_1");
                List<ModelPiece> body = model.Rest();
                List<ModelPiece> scope = body.Where(p => p.Node.name == "SMAW_1_Scope_1").ToList();

                Quaternion turn = AxisMath.Frame(Vector3.forward, Vector3.up, GunForward, GunUp);
                Matrix4x4 fit = FitGun(w, "BEU AW1", body.Concat(rocket), turn, w.OldBounds("Cylinder", "Cylinder/Cylinder_001"),
                    Sight.Part, w.AimLine("smaw_aim"), "Cylinder/Muzzle", scope, log, frontAtMuzzle: false);

                w.Assign("Cylinder", Bake(body, fit));
                w.Assign("Cylinder/Cylinder_001", Bake(rocket, w.NodeFromBody("Cylinder/Cylinder_001") * fit));
                w.Save();
            }
        }

        /// <summary>SL-DEFENDER, the bolt-action sniper: aimed through the full-screen scope overlay.</summary>
        private static void SlDefender(StringBuilder log)
        {
            using (var w = new PrefabEdit("sniper", "P90AW", "sniper_hip", log))
            {
                var model = new SourceModel("AWM");
                List<ModelPiece> handle = model.Take("bolt_handle");
                List<ModelPiece> bolt = model.Take("bolt");
                List<ModelPiece> magazine = model.Take("mag");
                List<ModelPiece> scope = model.Take("scope");
                List<ModelPiece> body = model.Rest();

                Quaternion turn = AxisMath.Frame(Vector3.forward, Vector3.up, GunForward, GunUp);
                // The scope overlay replaces the gun while aiming, but the gun is seen coming up to
                // the eye first, so its scope still goes on the camera line.
                Matrix4x4 fit = FitGun(w, "SL-DEFENDER", body.Concat(bolt).Concat(handle).Concat(magazine).Concat(scope), turn,
                    w.OldBounds("P90AW"), Sight.Part, w.AimLine("sniper_aim"), "P90AW/Muzzle", scope, log);

                w.Assign("P90AW", Bake(body, fit));
                w.Assign("P90AW/Bolt", Bake(bolt, w.NodeFromBody("P90AW/Bolt") * fit));
                w.Assign("P90AW/Bolt/Bolt_001", Bake(handle, w.NodeFromBody("P90AW/Bolt/Bolt_001") * fit));
                w.Assign("P90AW/Magazine", Bake(magazine, w.NodeFromBody("P90AW/Magazine") * fit));
                w.Assign("P90AW/Scope", Bake(scope, w.NodeFromBody("P90AW/Scope") * fit));
                w.Save();
            }
        }

        /// <summary>
        /// SIGNAL DMR: the SIG 716i, which ships with folding irons only, under the DMR's own optic.
        /// </summary>
        /// <remarks>
        /// The DMR aims through its optic with a reticle quad (<c>Acog Scope</c>) that its aim clip
        /// puts on the camera line; there is no overlay. Aiming is only unchanged if that optic is
        /// still there, and none of the new models is one a camera can look through (the PN 5x80's
        /// objective is capped). So the optic's own triangles are carried over from the old mesh,
        /// where the quad already sits, and the rifle is fitted under it by its bore.
        /// </remarks>
        private static void SignalDmr(StringBuilder log)
        {
            using (var w = new PrefabEdit("DMR", "DMR", "DMR Idle", log))
            {
                var model = new SourceModel("SIG716");
                List<ModelPiece> magazine = model.TakeRegion(SigMagazine);
                // Folded down under an optic, as on the real rifle; standing, they fill its view.
                model.TakeRegion(SigFrontSight);
                model.TakeRegion(SigRearSight);
                List<ModelPiece> body = model.Rest();

                List<ModelPiece> optic = w.OldPieces("DMR", OldMaterial("Dark Plastic"), OldMaterial("Dark Metal"))
                    .Select(p => p.SplitByComponents(DmrOptic).inside).ToList();
                float opticFoot = BoundsOf(optic.SelectMany(p => p.ModelPositions())).min.z;

                // Bore on the old muzzle first, then down (or up) until the rail meets the optic's foot.
                Quaternion turn = AxisMath.Frame(Vector3.left, Vector3.up, GunForward, GunUp);
                (Vector3, Vector3) aim = w.AimLine("DMR Aim");
                Matrix4x4 fit = FitGun(w, "SIGNAL DMR (bore)", body.Concat(magazine), turn, w.OldBounds("DMR"),
                    Sight.Bore, aim, "DMR/Muzzle Flash", null, log);
                float railTop = body.SelectMany(p => p.ModelPositions()).Where(p => SigRail.Contains(p))
                    .Select(p => fit.MultiplyPoint3x4(p).z).Max();
                fit = FitGun(w, "SIGNAL DMR", body.Concat(magazine), turn, w.OldBounds("DMR"),
                    Sight.Bore, aim, "DMR/Muzzle Flash", null, log, nudge: new Vector3(0f, 0f, opticFoot - railTop));

                BakedGeometry geometry = Bake(body, fit);
                foreach (ModelPiece p in optic) geometry.Add(p.Mesh, p.Triangles, Matrix4x4.identity, p.Material);
                log.AppendLine("  SIGNAL DMR: optic carried over (" + optic.Sum(p => p.Triangles.Length / 3)
                               + " triangles), rail lowered " + (railTop - opticFoot).ToString("F4") + " onto its foot");

                w.Assign("DMR", geometry);
                w.Assign("DMR/DMR_Magazine", Bake(magazine, w.NodeFromBody("DMR/DMR_Magazine") * fit));
                w.Save();
            }
        }

        /// <summary>
        /// Parts of the SIG 716, which is one mesh, by the centre of their connected shells in its
        /// model's space (barrel along -X, top +Y): the magazine, the two folding sights and the
        /// upper receiver's rail.
        /// </summary>
        private static readonly Bounds SigMagazine = MinMax(new Vector3(-1.6f, -2.6f, -12f), new Vector3(0.9f, 0.1f, -9f));
        private static readonly Bounds SigFrontSight = MinMax(new Vector3(-9.9f, 2.2f, -12f), new Vector3(-8.2f, 3.2f, -9f));
        private static readonly Bounds SigRearSight = MinMax(new Vector3(2.6f, 2.2f, -12f), new Vector3(3.2f, 3.2f, -9f));
        private static readonly Bounds SigRail = MinMax(new Vector3(-0.5f, 1.9f, -12f), new Vector3(2.5f, 2.6f, -9f));

        /// <summary>The old DMR mesh's optic and its mount, in the DMR node's space.</summary>
        private static readonly Bounds DmrOptic = MinMax(new Vector3(-0.08f, -0.42f, 0.115f), new Vector3(0.08f, 0.12f, 0.32f));

        /// <summary>
        /// RECON LRR: the KelTec RFB, with the PN 5x80 night-vision scope on its rail.
        /// </summary>
        /// <remarks>
        /// The RFB model has folding irons only, and RECON LRR is a <c>ScopedWeapon</c>: aiming
        /// swaps the gun for a full-screen scope overlay. A scope on the rail is what makes the gun
        /// in the hand match the view through it; the PN 5x80 is the pack's only standalone optic,
        /// and an overlay-aimed weapon is the one place its capped objective does not matter.
        /// </remarks>
        private static void ReconLrr(StringBuilder log)
        {
            using (var w = new PrefabEdit("RFB", "RFB", "RFB Idle", log))
            {
                var model = new SourceModel("KelTecRFB");
                model.TakeNodes(b => b.center.y > RfbSightsAbove);
                List<ModelPiece> magazine = model.TakeNodes(b => RfbMagazine.Contains(b.center));
                List<ModelPiece> body = model.Rest();

                Quaternion turn = AxisMath.Frame(Vector3.back, Vector3.up, GunForward, GunUp);
                Matrix4x4 fit = FitGun(w, "RECON LRR", body.Concat(magazine), turn, w.OldBounds("RFB"),
                    Sight.Bore, w.AimLine("RFB Aim"), "RFB/Muzzle", null, log);

                BakedGeometry geometry = Bake(body, fit);

                // The scope: its mount's foot on the rail, centred over it, the eyecup to the back.
                // Its power cable and battery hang loose below the mount in the model; on a rifle
                // they would hang through the rail, so they stay behind.
                Bounds rail = BoundsOf(Points(body.Where(p => RfbRail.Contains(NodeCenter(p))), Quaternion.identity).Select(p => fit.MultiplyPoint3x4(p)));
                var scopeModel = new SourceModel("PN5x80");
                Bounds whole = scopeModel.Measure();
                scopeModel.TakeRegion(MinMax(whole.min - Vector3.one, new Vector3(whole.max.x + 1f, whole.min.y + 0.3f * whole.size.y, whole.max.z + 1f)));
                List<ModelPiece> scope = scopeModel.Rest();
                Quaternion scopeTurn = AxisMath.Frame(Vector3.forward, Vector3.up, GunForward, GunUp);
                Bounds scopeBox = BoundsOf(Points(scope, scopeTurn));
                float length = rail.size.y * RfbScopeToRail;
                float s = length / scopeBox.size.y;
                Vector3 t = new Vector3(-scopeBox.center.x * s, rail.center.y - scopeBox.center.y * s + RfbScopeSlide, rail.max.z - scopeBox.min.z * s);
                Matrix4x4 fitScope = Matrix4x4.TRS(t, scopeTurn, Vector3.one * s);
                foreach (ModelPiece p in scope) geometry.Add(p.Mesh, p.Triangles, fitScope * p.ToModel, p.Material);
                log.AppendLine("  RECON LRR: PN 5x80 on the rail, scale " + s.ToString("F3") + ", rail top z " + rail.max.z.ToString("F4"));

                w.Assign("RFB", geometry);
                w.Assign("RFB/RFB_Magazine", Bake(magazine, w.NodeFromBody("RFB/RFB_Magazine") * fit));
                // The old charging handle was a node of its own; the Blockbench model's is not.
                w.Clear("RFB/Cube");
                w.Save();
            }
        }

        /// <summary>Blockbench cubes of the RFB, by their centre in the model's space.</summary>
        private const float RfbSightsAbove = 0.615f;
        private static readonly Bounds RfbMagazine = MinMax(new Vector3(-1f, -0.2f, 0.9f), new Vector3(1f, 0.36f, 1.16f));
        private static readonly Bounds RfbRail = MinMax(new Vector3(-1f, 0.57f, -0.1f), new Vector3(1f, 0.62f, 0.65f));
        private const float RfbScopeToRail = 0.72f;
        private const float RfbScopeSlide = 0f;

        /// <summary>
        /// The Javelin file has its launcher pitched 24.2 degrees about X, big end cap down; levelled
        /// by that, both rims of its tube are circles round <see cref="JavelinTubeCentre"/> (X, Y).
        /// </summary>
        private const float JavelinPitch = 24.2f;
        private static readonly Vector2 JavelinTubeCentre = new Vector2(4.57f, 11.68f);

        /// <summary>BIL SCALPEL, the Javelin: aimed through the command unit's overlay.</summary>
        private static void BilScalpel(StringBuilder log)
        {
            using (var w = new PrefabEdit("javelin", "Javelin", "javelin_idle", log))
            {
                List<ModelPiece> launcher = new SourceModel("Javelin").Rest();

                // Levelled (see JavelinPitch), the tube runs along +Z toward the command unit's
                // end, X is the launcher's left-right and the unit's handles hang to -Y. It goes in
                // that way round, +Z forward: the unit then rides ahead of the shoulder on the
                // tube's left, as the old one did, which is where the clips put the arms' hands and
                // the aim clip puts the eye. The cost is that its eyepiece faces forward; a proper
                // rotation cannot keep the unit on the left and turn the eyepiece back as well.
                Quaternion pitch = Quaternion.AngleAxis(-JavelinPitch, Vector3.right);

                // The launch line is the muzzle's, which the old tube lay along: 10 degrees nose-up
                // in the node, not along its -Y.
                Vector3 muzzle = w.OldPoint("Javelin/Muzzle");
                Vector3 launch = w.OldForward("Javelin/Muzzle");
                Quaternion turn = AxisMath.Frame(pitch * Vector3.forward, pitch * Vector3.up,
                    launch, Vector3.ProjectOnPlane(GunUp, launch));

                // As long along that line as the old launcher, over the same stretch of it, with
                // the tube's axis on it.
                float[] oldAlong = w.OldVertices("Javelin").Select(p => Vector3.Dot(p - muzzle, launch)).ToArray();
                float[] newAlong = Points(launcher, turn).Select(p => Vector3.Dot(p, launch)).ToArray();
                float s = (oldAlong.Max() - oldAlong.Min()) / (newAlong.Max() - newAlong.Min());
                Vector3 tubeAxis = s * (turn * (pitch * new Vector3(JavelinTubeCentre.x, JavelinTubeCentre.y, 0f)));
                float slide = (oldAlong.Min() + oldAlong.Max()) / 2f - s * (newAlong.Min() + newAlong.Max()) / 2f
                              + Vector3.Dot(tubeAxis, launch);
                Matrix4x4 fit = Matrix4x4.TRS(muzzle - tubeAxis + launch * slide, turn, Vector3.one * s);
                log.AppendLine("  BIL SCALPEL: scale " + s.ToString("F4") + ", launch line " + launch.ToString("F3")
                               + ", " + (oldAlong.Max() - oldAlong.Min()).ToString("F3") + " m along it");

                w.Assign("Javelin", Bake(launcher, fit));
                w.Save();
            }
        }

        private static void Frag(StringBuilder log)
        {
            using (var w = new PrefabEdit("frag", "Grenade", "frag_hip", log))
            {
                var model = new SourceModel("M26");
                List<ModelPiece> pin = model.Take("m61_grenade:Grenade_Pin", "m61_grenade:Grenade_Wire");
                List<ModelPiece> body = model.Rest();
                // Fuse up +Z, spoon along +Y, as the old grenade.
                Quaternion turn = AxisMath.Frame(Vector3.up, Vector3.left, Vector3.forward, Vector3.up);
                Matrix4x4 fit = FitBox(body, turn, w.OldMeshBounds("Grenade"), 2);
                w.Assign("Grenade", Bake(body, fit));
                w.Assign("Grenade/Pin", Bake(pin, w.NodeFromBody("Grenade/Pin") * fit));
                w.Save();
            }
        }

        private static void Spearhead(StringBuilder log)
        {
            using (var w = new PrefabEdit("spearhead", "Grenade", "frag_hip", log))
            {
                var model = new SourceModel("M84");
                List<ModelPiece> pin = model.Take("Spiral_low", "Spiral.001_low", "Spiral.002_low", "Spiral.003_low", "Cylinder.006_low");
                List<ModelPiece> body = model.Rest();
                Quaternion turn = AxisMath.Frame(Vector3.up, Vector3.left, Vector3.forward, Vector3.up);
                Matrix4x4 fit = FitBox(body, turn, w.OldMeshBounds("Grenade"), 2);
                w.Assign("Grenade", Bake(body, fit));
                w.Assign("Grenade/Pin", Bake(pin, w.NodeFromBody("Grenade/Pin") * fit));
                w.Save();
            }
        }

        private static void Binocs(StringBuilder log)
        {
            using (var w = new PrefabEdit("Binocs", "Binocs", "Binocs Hip", log))
            {
                List<ModelPiece> binoculars = new SourceModel("Binoculars").Rest();
                // Objectives forward (-Y), eyepieces to the eye, the pair side by side along X.
                Quaternion turn = AxisMath.Frame(Vector3.left, Vector3.up, GunForward, GunUp);
                w.Assign("Binocs", Bake(binoculars, FitBox(binoculars, turn, w.OldMeshBounds("Binocs"), 0)));
                w.Save();
            }
        }

        private static void AmmoBag(StringBuilder log)
        {
            using (var w = new PrefabEdit("ammobox", "Ammobox", "Ammobox Hip", log))
            {
                var model = new SourceModel("AmmoBox556");
                // Sixty rounds: fifty-five in the tray, whose open end shows them, and five
                // scattered on the floor beside the box.
                model.Take("bullet_52_low", "bullet_53_low", "bullet_54_low", "bullet_59_low", "bullet_60_low");
                List<ModelPiece> box = model.Rest();
                // The label face to the camera (+Y of the node in the hip pose), its length across,
                // the print the right way up.
                Quaternion turn = Quaternion.Euler(0f, 180f, 0f);
                w.Assign("Ammobox", Bake(box, FitBox(box, turn, w.OldMeshBounds("Ammobox"), 0)));
                w.Save();
            }
        }

        private static void Medipack(StringBuilder log)
        {
            using (var w = new PrefabEdit("medipack", "Ammobox", "Ammobox Hip", log))
            {
                List<ModelPiece> kit = new SourceModel("FirstAidKit").Rest();
                // Handle up (+Z of the node), and the face with the red cross — which the model has
                // turned 23 degrees off its axes — to the camera (+Y of the node in the hip pose).
                // That face is found by the decal's place in the lid's texture.
                Vector3 crossFace = DecalNormal(kit.Where(p => p.Material.name == "FirstAidKit_Top"), new Rect(0.6f, 0.05f, 0.16f, 0.24f));
                Quaternion turn = AxisMath.Frame(crossFace, Vector3.up, Vector3.up, Vector3.forward);
                w.Assign("Ammobox", Bake(kit, FitBox(kit, turn, w.OldMeshBounds("Ammobox"), 0)));
                log.AppendLine("  MEDIPACK: red cross faces " + crossFace.ToString("F3") + " in the model");
                w.Save();
            }
        }

        /// <summary>WRENCH and the hidden SUPER WRENCH, which is the same wrench in gold.</summary>
        private static void Wrenches(StringBuilder log)
        {
            Material gold = AssetDatabase.LoadAssetAtPath<Material>("Assets/Material/Gold.mat")
                            ?? throw new FileNotFoundException("Assets/Material/Gold.mat, the Super Wrench's finish, is missing.");
            foreach ((string prefab, Material finish) in new[] { ("Wrench", (Material)null), ("Super Wrench", gold) })
            {
                using (var w = new PrefabEdit(prefab, "Wrench", "Wrench Idle", log))
                {
                    List<ModelPiece> wrench = new SourceModel("PipeWrench").Rest();
                    // Head up (+Z of the node), jaw opening to -Y.
                    Quaternion turn = AxisMath.Frame(Vector3.back, Vector3.right, Vector3.forward, Vector3.down);
                    w.Assign("Wrench", Bake(wrench, FitBox(wrench, turn, w.OldMeshBounds("Wrench"), 2), finish));
                    // The old wrench's adjusting jaw was a part of its own; the pipe wrench is one piece.
                    w.Clear("Wrench/Mover");
                    w.Save();
                }
            }
        }

        /// <summary>
        /// The thrown and fired things that drew the same mesh as the item in the hand.
        /// </summary>
        /// <remarks>
        /// Only the mesh and materials move across. The ammo bag's and medipack's box colliders are
        /// the gameplay (the resupply trigger sits on them) and stay as authored; the new box is
        /// fitted to the old one's bounds, which the collider was sized to.
        /// </remarks>
        private static void Projectiles(StringBuilder log)
        {
            Reuse("Frag Grenade", "", "frag", "Grenade", log);
            Reuse("Spearhead Grenade", "", "spearhead", "Grenade", log);
            Reuse("Ammobox Projectile", "Model", "ammobox", "Ammobox", log);
            Reuse("Medipack Projectile", "Model", "medipack", "Ammobox", log);

            using (var w = new PrefabEdit("rocket", "", null, log))
            {
                List<ModelPiece> rocket = new SourceModel("SMAW").Take("SMAW_1_Rocket_1");
                Quaternion turn = AxisMath.Frame(Vector3.forward, Vector3.up, Vector3.forward, Vector3.up);
                w.Assign("", Bake(rocket, FitBox(rocket, turn, w.OldMeshBounds(""), 2)));
                w.Save();
            }
        }

        private static void Reuse(string prefab, string node, string fromPrefab, string fromNode, StringBuilder log)
        {
            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefab/" + fromPrefab + ".prefab");
            Transform from = source.transform.Find(fromNode);
            Mesh mesh = from.GetComponent<MeshFilter>().sharedMesh;
            Material[] materials = from.GetComponent<MeshRenderer>().sharedMaterials;
            using (var w = new PrefabEdit(prefab, node, null, log))
            {
                w.SetMesh(node, mesh, materials);
                w.Save();
            }
        }

        // ------------------------------------------------------------------ fitting

        private enum Sight
        {
            /// <summary>The highest point near the muzzle, on the bore's centre line, goes on the aim line.</summary>
            FrontSight,
            /// <summary>The centre of the given parts (a scope) goes on the aim line.</summary>
            Part,
            /// <summary>The bore goes where the old muzzle is; for weapons aimed through an overlay.</summary>
            Bore,
        }

        /// <summary>
        /// The placement of a gun model in its body node: barrel down -Y, top up +Z, reaching from
        /// the old muzzle to the old butt, sight on the aim clip's camera line.
        /// </summary>
        /// <remarks>
        /// The muzzle node is where the flash is drawn and where a bot's round leaves, and it stays
        /// where it is; so the new barrel is made to end there rather than at the old mesh's front,
        /// which on some guns ran on past it (the old sniper's brake by 16 cm). A launcher's muzzle is
        /// inside its tube, so a launcher is measured front to back instead
        /// (<paramref name="frontAtMuzzle"/> false).
        /// </remarks>
        private static Matrix4x4 FitGun(PrefabEdit w, string label, IEnumerable<ModelPiece> fitPieces, Quaternion turn, Bounds old,
            Sight sight, (Vector3 origin, Vector3 direction) aim, string muzzlePath, IEnumerable<ModelPiece> sightPieces, StringBuilder log,
            float scale = 1f, Vector3 nudge = default, bool frontAtMuzzle = true)
        {
            Vector3 oldMuzzle = w.OldPoint(muzzlePath);
            float oldFront = frontAtMuzzle ? oldMuzzle.y : old.min.y;

            List<Vector3> points = Points(fitPieces, turn).ToList();
            Bounds raw = BoundsOf(points);
            float s = (old.max.y - oldFront) / raw.size.y * scale;
            for (int i = 0; i < points.Count; i++) points[i] *= s;

            float front = raw.min.y * s;
            float length = raw.size.y * s;
            Bounds bore = BoundsOf(points.Where(p => p.y < front + 0.02f * length));

            var t = new Vector3(0f, oldFront - front, 0f);
            string how;
            switch (sight)
            {
                case Sight.FrontSight:
                {
                    Vector3 tip = points.Where(p => p.y < front + 0.35f * length && Mathf.Abs(p.x - bore.center.x) < 0.006f)
                        .OrderByDescending(p => p.z).First();
                    Vector3 onLine = OnAimLine(aim, tip.y + t.y);
                    t.x = onLine.x - bore.center.x;
                    t.z = onLine.z - tip.z;
                    how = "front sight on the aim line";
                    break;
                }
                case Sight.Part:
                {
                    Bounds part = BoundsOf(Points(sightPieces, turn).Select(p => p * s));
                    Vector3 onLine = OnAimLine(aim, part.center.y + t.y);
                    t.x = onLine.x - part.center.x;
                    t.z = onLine.z - part.center.z;
                    how = "scope centre on the aim line";
                    break;
                }
                default:
                    t.x = oldMuzzle.x - bore.center.x;
                    t.z = oldMuzzle.z - bore.center.z;
                    how = "bore on the old muzzle";
                    break;
            }
            t += nudge;

            Vector3 newMuzzle = bore.center + t;
            log.AppendLine("  " + label + ": scale " + s.ToString("F4") + ", " + how + ", offset " + t.ToString("F4")
                           + "; new muzzle is " + (newMuzzle - oldMuzzle).ToString("F3") + " from the old one");
            return Matrix4x4.TRS(t, turn, Vector3.one * s);
        }

        private static Vector3 OnAimLine((Vector3 origin, Vector3 direction) aim, float y)
            => aim.origin + aim.direction * ((y - aim.origin.y) / aim.direction.y);

        /// <summary>
        /// Turns the pieces by <paramref name="turn"/>, scales them so their extent along
        /// <paramref name="axis"/> matches <paramref name="target"/>'s, and centres them on it.
        /// </summary>
        private static Matrix4x4 FitBox(IEnumerable<ModelPiece> pieces, Quaternion turn, Bounds target, int axis)
        {
            Bounds b = BoundsOf(Points(pieces, turn));
            float s = target.size[axis] / b.size[axis];
            return Matrix4x4.TRS(target.center - b.center * s, turn, Vector3.one * s);
        }

        private static BakedGeometry Bake(IEnumerable<ModelPiece> pieces, Matrix4x4 modelToNode, Material finish = null)
        {
            var geometry = new BakedGeometry();
            foreach (ModelPiece p in pieces)
            {
                Material material = finish != null ? finish : p.Material;
                if (material == null || !AssetDatabase.GetAssetPath(material).StartsWith(WeaponModelCatalog.Root) && finish == null)
                    throw new InvalidOperationException("'" + p.Node.name + "' is drawn with '" + (material == null ? "nothing" : material.name)
                        + "', which the catalog does not map. Add its source name to WeaponModelCatalog.");
                geometry.Add(p.Mesh, p.Triangles, modelToNode * p.ToModel, material);
            }
            return geometry;
        }

        private static IEnumerable<Vector3> Points(IEnumerable<ModelPiece> pieces, Quaternion turn)
            => pieces.SelectMany(p => p.ModelPositions()).Select(p => turn * p);

        private static Vector3 NodeCenter(ModelPiece p) => BoundsOf(p.ModelPositions()).center;

        /// <summary>
        /// The mean facing, in the model's space, of the triangles whose UVs fall in
        /// <paramref name="uvRect"/>: where a decal printed in that part of a texture looks.
        /// </summary>
        private static Vector3 DecalNormal(IEnumerable<ModelPiece> pieces, Rect uvRect)
        {
            Vector3 sum = Vector3.zero;
            foreach (ModelPiece p in pieces)
            {
                Vector3[] v = p.Mesh.vertices;
                Vector2[] uv = p.Mesh.uv;
                for (int k = 0; k + 2 < p.Triangles.Length; k += 3)
                {
                    int a = p.Triangles[k], b = p.Triangles[k + 1], c = p.Triangles[k + 2];
                    if (!uvRect.Contains((uv[a] + uv[b] + uv[c]) / 3f)) continue;
                    Vector3 pa = p.ToModel.MultiplyPoint3x4(v[a]);
                    sum += Vector3.Cross(p.ToModel.MultiplyPoint3x4(v[b]) - pa, p.ToModel.MultiplyPoint3x4(v[c]) - pa);
                }
            }
            if (sum == Vector3.zero) throw new InvalidOperationException("No triangle is textured from " + uvRect + ".");
            return sum.normalized;
        }

        private static Material OldMaterial(string name)
            => AssetDatabase.LoadAssetAtPath<Material>("Assets/Material/" + name + ".mat")
               ?? throw new FileNotFoundException("Assets/Material/" + name + ".mat is missing.");

        private static Bounds BoundsOf(IEnumerable<Vector3> points)
        {
            bool any = false;
            var b = new Bounds();
            foreach (Vector3 p in points)
            {
                if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
                else b.Encapsulate(p);
            }
            if (!any) throw new InvalidOperationException("Nothing to measure: a part list came out empty.");
            return b;
        }

        private static Bounds MinMax(Vector3 min, Vector3 max)
        {
            var b = new Bounds();
            b.SetMinMax(min, max);
            return b;
        }

        // ------------------------------------------------------------------ sources and targets

        /// <summary>A model's pieces, handed out part by part; whatever is left is the body.</summary>
        private sealed class SourceModel
        {
            private readonly string _slug;
            private readonly List<ModelPiece> _pieces;

            public SourceModel(string slug)
            {
                _slug = slug;
                WeaponModelCatalog.ModelDef def = WeaponModelCatalog.Model(slug);
                GameObject root = AssetDatabase.LoadAssetAtPath<GameObject>(def.Path)
                                  ?? throw new FileNotFoundException(def.Path + " is missing.");
                _pieces = ModelPiece.Gather(root);
            }

            /// <summary>The pieces of the named nodes and of everything under them.</summary>
            public List<ModelPiece> Take(params string[] nodes)
            {
                foreach (string node in nodes)
                    if (!_pieces.Any(p => IsUnder(p.Node, node)))
                        throw new InvalidOperationException(_slug + " has no node '" + node + "' (left to take: "
                            + string.Join(", ", _pieces.Select(p => p.Node.name).Distinct()) + ").");
                List<ModelPiece> taken = _pieces.Where(p => nodes.Any(n => IsUnder(p.Node, n))).ToList();
                foreach (ModelPiece p in taken) _pieces.Remove(p);
                return taken;
            }

            /// <summary>Whole nodes whose model-space bounds pass <paramref name="test"/>.</summary>
            public List<ModelPiece> TakeNodes(Func<Bounds, bool> test)
            {
                var byNode = _pieces.GroupBy(p => p.Node).ToList();
                var taken = new List<ModelPiece>();
                foreach (var group in byNode)
                    if (test(BoundsOf(group.SelectMany(p => p.ModelPositions()))))
                        taken.AddRange(group);
                if (taken.Count == 0) throw new InvalidOperationException(_slug + ": no node matched a region.");
                foreach (ModelPiece p in taken) _pieces.Remove(p);
                return taken;
            }

            /// <summary>The connected parts whose centre is inside <paramref name="modelBox"/>.</summary>
            public List<ModelPiece> TakeRegion(Bounds modelBox)
            {
                var taken = new List<ModelPiece>();
                for (int i = _pieces.Count - 1; i >= 0; i--)
                {
                    (ModelPiece inside, ModelPiece outside) = _pieces[i].SplitByComponents(modelBox);
                    if (inside.Triangles.Length == 0) continue;
                    taken.Add(inside);
                    if (outside.Triangles.Length == 0) _pieces.RemoveAt(i);
                    else _pieces[i] = outside;
                }
                if (taken.Count == 0) throw new InvalidOperationException(_slug + ": nothing inside " + modelBox + ".");
                return taken;
            }

            public List<ModelPiece> Rest()
            {
                List<ModelPiece> rest = _pieces.ToList();
                _pieces.Clear();
                return rest;
            }

            /// <summary>The bounds of what is left, in the model's space.</summary>
            public Bounds Measure() => BoundsOf(_pieces.SelectMany(p => p.ModelPositions()));

            private static bool IsUnder(Transform t, string name)
            {
                for (; t != null; t = t.parent)
                    if (t.name == name) return true;
                return false;
            }
        }

        /// <summary>
        /// One weapon prefab opened for editing, and a second, throwaway copy of it the animation
        /// clips are sampled on.
        /// </summary>
        /// <remarks>
        /// Parts are placed relative to their body in the pose the Animator actually holds the gun
        /// in (the hip clip), not the prefab's stored transforms, which differ for some nodes (the
        /// S-IND7's hammer by 50 degrees). Both copies live in preview scenes, so no open scene is
        /// touched.
        /// </remarks>
        private sealed class PrefabEdit : IDisposable
        {
            private readonly string _name;
            private readonly string _path;
            private readonly string _body;
            private readonly string _hipClip;
            private readonly StringBuilder _log;
            private readonly GameObject _contents;
            private readonly GameObject _pose;
            private readonly AnimationClip[] _clips;
            private bool _saved;

            public PrefabEdit(string name, string body, string hipClip, StringBuilder log)
            {
                _name = name;
                _path = "Assets/Prefab/" + name + ".prefab";
                _body = body;
                _hipClip = hipClip;
                _log = log;
                if (AssetDatabase.LoadAssetAtPath<GameObject>(_path) == null)
                    throw new FileNotFoundException(_path + " is missing.");
                _contents = PrefabUtility.LoadPrefabContents(_path);
                _pose = PrefabUtility.LoadPrefabContents(_path);
                _pose.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                Animator animator = _pose.GetComponent<Animator>();
                _clips = animator != null && animator.runtimeAnimatorController != null
                    ? animator.runtimeAnimatorController.animationClips
                    : new AnimationClip[0];
                if (_hipClip != null) Sample(_hipClip);
                _log.AppendLine(name + ":");
            }

            private void Sample(string clip)
            {
                AnimationClip found = _clips.FirstOrDefault(c => c.name == clip)
                                      ?? throw new InvalidOperationException(_name + " has no clip '" + clip + "'.");
                found.SampleAnimation(_pose, 0f);
            }

            private static Transform Find(GameObject root, string path, string prefab)
            {
                if (path == "") return root.transform;
                return root.transform.Find(path) ?? throw new InvalidOperationException(prefab + " has no '" + path + "'.");
            }

            /// <summary>The camera's line of sight in the aim clip, in the body node's space.</summary>
            public (Vector3 origin, Vector3 direction) AimLine(string aimClip)
            {
                Sample(aimClip);
                Transform body = Find(_pose, _body, _name);
                var line = (body.InverseTransformPoint(Vector3.zero), body.InverseTransformDirection(Vector3.forward).normalized);
                Sample(_hipClip);
                return line;
            }

            /// <summary>From the body's space to the node's, in the hip pose.</summary>
            public Matrix4x4 NodeFromBody(string path)
                => Find(_pose, path, _name).worldToLocalMatrix * Find(_pose, _body, _name).localToWorldMatrix;

            private Matrix4x4 BodyFromNode(string path) => NodeFromBody(path).inverse;

            /// <summary>The union of the old meshes on these nodes, in the body's space.</summary>
            public Bounds OldBounds(params string[] paths)
            {
                var corners = new List<Vector3>();
                foreach (string path in paths)
                {
                    Bounds b = OldMeshBounds(path);
                    Matrix4x4 m = BodyFromNode(path);
                    for (int i = 0; i < 8; i++)
                        corners.Add(m.MultiplyPoint3x4(b.center + Vector3.Scale(b.extents,
                            new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
                }
                return BoundsOf(corners);
            }

            /// <summary>The old mesh's bounds in its own node's space.</summary>
            public Bounds OldMeshBounds(string path) => OldMesh(path).bounds;

            /// <summary>
            /// The mesh this node drew before the reskin, from <see cref="OriginalMeshes"/>: never
            /// the node's current mesh, which after a first run is the reskin's own.
            /// </summary>
            private Mesh OldMesh(string path)
            {
                string key = _name + ":" + path;
                if (!OriginalMeshes.TryGetValue(key, out string asset))
                    throw new InvalidOperationException("No original mesh is recorded for " + key + "; add it to OriginalMeshes.");
                return AssetDatabase.LoadAssetAtPath<Mesh>(asset)
                       ?? throw new FileNotFoundException(asset + ", the mesh " + key + " is fitted to, is missing.");
            }

            /// <summary>The old mesh on a node, as pieces in that node's own space.</summary>
            public List<ModelPiece> OldPieces(string path, params Material[] materials)
            {
                Transform node = Find(_pose, path, _name);
                Mesh mesh = OldMesh(path);
                return Enumerable.Range(0, mesh.subMeshCount).Select(s => new ModelPiece
                {
                    Mesh = mesh, SubMesh = s, Material = materials[s], Node = node, ToModel = Matrix4x4.identity,
                    Triangles = mesh.GetTriangles(s),
                }).ToList();
            }

            /// <summary>A node's origin in the body's space (a muzzle, say), in the hip pose.</summary>
            public Vector3 OldPoint(string path) => BodyFromNode(path).MultiplyPoint3x4(Vector3.zero);

            /// <summary>The way a node faces (a muzzle's line of fire), in the body's space, in the hip pose.</summary>
            public Vector3 OldForward(string path) => BodyFromNode(path).MultiplyVector(Vector3.forward).normalized;

            /// <summary>The old mesh's vertices, in the body's space.</summary>
            public IEnumerable<Vector3> OldVertices(string path)
            {
                Matrix4x4 m = BodyFromNode(path);
                return OldMesh(path).vertices.Select(v => m.MultiplyPoint3x4(v));
            }

            public void Assign(string path, BakedGeometry geometry)
            {
                if (geometry.IsEmpty) throw new InvalidOperationException(_name + "/" + path + ": nothing to bake.");
                string asset = BakedFolder + "/" + _name + (path == "" ? "" : "." + path.Replace('/', '.')) + ".asset";
                Mesh mesh = SaveMesh(geometry, asset);
                SetMesh(path, mesh, geometry.Materials.ToArray());
                _log.AppendLine("  " + (path == "" ? "(root)" : path) + ": " + geometry.TriangleCount + " triangles, "
                                + geometry.Materials.Count + " materials -> " + asset);
            }

            public void SetMesh(string path, Mesh mesh, Material[] materials)
            {
                Transform node = Find(_contents, path, _name);
                MeshFilter filter = node.GetComponent<MeshFilter>();
                MeshRenderer renderer = node.GetComponent<MeshRenderer>();
                if (filter == null || renderer == null)
                    throw new InvalidOperationException(_name + "/" + path + " has no MeshFilter and MeshRenderer to reskin.");
                filter.sharedMesh = mesh;
                renderer.sharedMaterials = materials;
            }

            /// <summary>A node the new model has no separate part for: it keeps its components and draws nothing.</summary>
            public void Clear(string path)
            {
                Find(_contents, path, _name).GetComponent<MeshFilter>().sharedMesh = null;
                _log.AppendLine("  " + path + ": cleared (no separate part in the new model)");
            }

            public void Save()
            {
                PrefabUtility.SaveAsPrefabAsset(_contents, _path);
                _saved = true;
            }

            public void Dispose()
            {
                if (!_saved) _log.AppendLine("  NOT SAVED: " + _path);
                PrefabUtility.UnloadPrefabContents(_contents);
                PrefabUtility.UnloadPrefabContents(_pose);
            }
        }

        /// <summary>
        /// What each reskinned node drew before, and so what the new model is measured against.
        /// </summary>
        /// <remarks>
        /// Recorded rather than read off the prefab, because after one run the prefab draws the
        /// reskin: a second run that measured it would fit each model to the last fit and drift.
        /// The old meshes stay in <c>Assets/Mesh</c> for exactly this.
        /// </remarks>
        private static readonly Dictionary<string, string> OriginalMeshes = new Dictionary<string, string>
        {
            ["ak:AK"] = "Assets/Mesh/AK.asset",
            ["ak:AK/Magazine"] = "Assets/Mesh/Magazine.asset",
            ["ak:AK/Slide"] = "Assets/Mesh/Slide.asset",
            ["mk25:MK25"] = "Assets/Mesh/MK25.asset",
            ["mk25:MK25/Slide"] = "Assets/Mesh/Slide_0.asset",
            ["mk25:MK25/Magazine"] = "Assets/Mesh/Magazine_0.asset",
            ["mk25:MK25/Hammer"] = "Assets/Mesh/Hammer.asset",
            ["mk25:MK25/Suppressor"] = "Assets/Mesh/Suppressor.asset",
            ["mk25 suppressed:MK25"] = "Assets/Mesh/MK25.asset",
            ["mk25 suppressed:MK25/Slide"] = "Assets/Mesh/Slide_0.asset",
            ["mk25 suppressed:MK25/Magazine"] = "Assets/Mesh/Magazine_0.asset",
            ["mk25 suppressed:MK25/Hammer"] = "Assets/Mesh/Hammer.asset",
            ["mk25 suppressed:MK25/Suppressor"] = "Assets/Mesh/Suppressor.asset",
            ["shotgun:Shotgun"] = "Assets/Mesh/Shotgun.asset",
            ["shotgun:Shotgun/Pump"] = "Assets/Mesh/Pump.asset",
            ["shotgun:Shell"] = "Assets/Mesh/Shell.asset",
            ["smaw:Cylinder"] = "Assets/Mesh/Cylinder_0.asset",
            ["smaw:Cylinder/Cylinder_001"] = "Assets/Mesh/Cylinder_001.asset",
            ["sniper:P90AW"] = "Assets/Mesh/P90AW.asset",
            ["sniper:P90AW/Bolt"] = "Assets/Mesh/Bolt.asset",
            ["sniper:P90AW/Bolt/Bolt_001"] = "Assets/Mesh/Bolt_001.asset",
            ["sniper:P90AW/Magazine"] = "Assets/Mesh/Magazine_1.asset",
            ["sniper:P90AW/Scope"] = "Assets/Mesh/Scope.asset",
            ["DMR:DMR"] = "Assets/Mesh/DMR.asset",
            ["DMR:DMR/DMR_Magazine"] = "Assets/Mesh/DMR_Magazine.asset",
            ["RFB:RFB"] = "Assets/Mesh/RFB.asset",
            ["RFB:RFB/RFB_Magazine"] = "Assets/Mesh/RFB_Magazine.asset",
            ["javelin:Javelin"] = "Assets/Mesh/Javelin.asset",
            ["frag:Grenade"] = "Assets/Mesh/Grenade.asset",
            ["frag:Grenade/Pin"] = "Assets/Mesh/Pin.asset",
            ["spearhead:Grenade"] = "Assets/Mesh/Grenade_0.asset",
            ["spearhead:Grenade/Pin"] = "Assets/Mesh/Pin_0.asset",
            ["Binocs:Binocs"] = "Assets/Mesh/Binocs.asset",
            ["ammobox:Ammobox"] = "Assets/Mesh/Ammobox.asset",
            ["medipack:Ammobox"] = "Assets/Mesh/Ammobox.asset",
            ["Wrench:Wrench"] = "Assets/Mesh/Wrench.asset",
            ["Super Wrench:Wrench"] = "Assets/Mesh/Wrench.asset",
            ["rocket:"] = "Assets/Mesh/Cylinder.asset",
        };

        /// <summary>Writes a mesh asset, rewriting one already there in place so its GUID is kept.</summary>
        private static Mesh SaveMesh(BakedGeometry geometry, string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (mesh == null)
            {
                mesh = new Mesh();
                geometry.WriteTo(mesh, name);
                AssetDatabase.CreateAsset(mesh, path);
                return mesh;
            }
            geometry.WriteTo(mesh, name);
            EditorUtility.SetDirty(mesh);
            return mesh;
        }
    }
}
