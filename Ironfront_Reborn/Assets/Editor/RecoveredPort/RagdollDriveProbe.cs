// P25 -- how much do the two surviving candidate differences actually move the joint drive?
//
// WHY THIS EXISTS
//     The P25 plan blamed the ragdoll's feel on one line lost from ActiveRaggy.Awake:
//
//         jointDrive.mode = JointDriveMode.Position;
//
//     tools/ragdoll_drive_facts.ps1 shows that line was already dead in the engine the original
//     shipped on: in Unity 5.4.0f3's own UnityEngine.dll, JointDrive.mode is
//     [Obsolete("JointDriveMode is obsolete")], its setter's entire IL body is `ret`, and the
//     struct carries no field to store it in. Assigning it changed nothing then and its absence
//     changes nothing now. Every drive parameter that DOES reach PhysX is identical across the
//     two trees, on all 62 ConfigurableJoints.
//
//     That leaves exactly two candidate differences between the original build and this one:
//
//         (a) fixed timestep -- 0.02 (50 Hz) originally, 1/60 here, changed deliberately for
//             issue #123 and not up for reverting;
//         (b) JointDrive.useAcceleration -- a field Unity 6 has and 5.4 did not. ActiveRaggy
//             builds its drive with default(JointDrive), so here it is false.
//
//     This probe measures how much each of those two moves the drive, on the real ragdoll, at
//     the drive values the game actually uses. It bounds the problem. It does not solve it.
//
// WHAT THIS CANNOT TELL YOU, AND MUST NOT BE READ AS SAYING
//     This runs on Unity 6 only. It cannot observe what Unity 5.4's native drive did, because
//     that behaviour is not expressed anywhere in managed metadata. So it cannot say "the
//     original used acceleration drive". What it can say is how sensitive the ragdoll is to each
//     knob -- and a knob that moves nothing is not a candidate explanation for anything,
//     whichever way 5.4 had it set.
//
// WHY A LOCAL PHYSICS SCENE AND AN EXPLICIT dt
//     The plan proposed measuring the 50 Hz variable by temporarily editing TimeManager and
//     remembering not to commit it -- a risk its own table scores 15, because reverting the
//     60 Hz step would resurrect #123. None of that is necessary. An EditorSceneManager preview
//     scene carries its own physics scene, stepped by hand through PhysicsScene.Simulate(dt), so
//     the timestep is an argument to this probe and the project's TimeManager is never touched.
//     It also means the two timesteps are compared inside one run, with no Editor restart and
//     no project-settings churn between them.
//
// DETERMINISM
//     Every condition gets a freshly instantiated rig, so no rigidbody state survives from the
//     previous condition. Bodies start at rest in the bind pose; the drive is then commanded to
//     a pose displaced by a fixed angle about a fixed axis, and the chain's response is sampled
//     every step. Sleeping is disabled for the duration -- a body that falls asleep mid-run
//     would report a settle time that measures the sleep threshold instead of the drive.
//
// COMPARING ACROSS TIMESTEPS
//     Horizon and settle time are in SECONDS, never steps. 180 steps at 60 Hz and 180 steps at
//     50 Hz are not the same experiment, and a step-denominated "settle time" would report the
//     timestep back to you as if it were a result.
//
// Output: tools/recovered/ragdoll-drive-response.json
// Menu:   Ironfront/Recovered Port/Measure Ragdoll Drive Response

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ironfront.Tools.RecoveredPort
{
    public static class RagdollDriveProbe
    {
        /// <summary>The ragdoll actually used by players; its limb masses and inertias are the
        /// ones useAcceleration would or would not be dividing out.</summary>
        const string PrefabPath = "Assets/Prefab/Player Fps Actor.prefab";

        /// <summary>Simulated seconds per condition. Long enough for (50,1) -- the death drive,
        /// far the slowest -- to settle, so no condition is censored by the horizon.</summary>
        const float HorizonSeconds = 3f;

        /// <summary>Commanded pose offset, degrees about the joint's local X. Large enough that
        /// the response is well clear of solver noise, small enough to stay inside the joints'
        /// authored angular limits.</summary>
        const float TargetOffsetDegrees = 30f;

        /// <summary>Settled = max |angularVelocity| over the whole chain stays under this for
        /// SettleHoldSeconds. In rad/s.</summary>
        const float SettleAngVelThreshold = 0.05f;

        const float SettleHoldSeconds = 0.5f;

        /// <summary>A second, looser settle threshold. The tight one above turned out to be below
        /// the chain's residual solver jitter in every condition, so on its own it reports null
        /// everywhere and discriminates nothing. 1 rad/s is "visibly stopped moving", and it does
        /// separate the conditions. Both are reported; neither is quietly dropped.</summary>
        const float SettleAngVelLooseThreshold = 1f;

        // NAMING: these two are deliberately not called Condition and Result.
        // Assets/Editor compiles into Assembly-CSharp-Editor, which is a PREDEFINED assembly, and
        // tools/check-net-layering.ps1 RULE 6b matches predefined type names TEXTUALLY against
        // every identifier in Net/*. A type here named Result makes that gate fail on
        // ClientSeatRequester.cs and ClientVehicleStage.cs, which merely use the word -- a red
        // with nothing wrong in it, pointing at files nobody touched. Keep editor-side type names
        // domain-specific for that reason.
        struct DriveCondition
        {
            public string driveLabel;
            public float spring;
            public float damper;
            public float hz;
            public bool useAcceleration;
            public bool gravity;
        }

        struct DriveResponse
        {
            public bool valid;
            public string invalidReason;
            public float peakAngularVelocity;   // rad/s, max over chain over run
            public float finalAngularVelocity;  // rad/s, max over chain at the horizon
            public float settleSeconds;         // NaN if never settled inside the horizon
            public float settleSecondsLoose;    // NaN if never fell under the loose threshold
            public float finalMeanAngleError;   // degrees, mean over joints at the horizon
            public float peakMeanAngleError;    // degrees
            public int joints;
            public int drivenBodies;
            public int steps;
        }

        [MenuItem("Ironfront/Recovered Port/Measure Ragdoll Drive Response")]
        public static void Run()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
            {
                Debug.LogError("[P25] prefab not found: " + PrefabPath);
                return;
            }

            // The three drive settings the game actually installs, from the SetDrive call sites
            // recorded in tools/recovered/ragdoll-drive-facts.json.
            var drives = new[]
            {
                new { label = "awake-1000-3", spring = 1000f, damper = 3f },
                new { label = "alive-700-3",  spring = 700f,  damper = 3f },
                new { label = "dead-50-1",    spring = 50f,   damper = 1f },

                // DRIVE CONTROL. With no drive at all the chain must stay in its bind pose, and
                // it does: peak angular velocity collapses from ~44 rad/s to ~0.008, and the
                // commanded 30 degrees is never approached. That is what licenses reading the
                // rows above as measurements OF THE DRIVE rather than of something else moving.
                //
                // It is NOT a gravity control, and must not be read as one. With the drive off the
                // chain does not visibly move under gravity either, so "gravity on and gravity
                // off look the same" here has two candidate causes and this row cannot separate
                // them. Gravity is instead proven live, separately, by HarnessSelfTest.
                //
                // Worth knowing while reading these rows: every angular limit on all 12 joints of
                // this ragdoll is zero with motion = Limited, in BOTH trees -- it is how
                // Ravenfield's ragdoll has always been built, not a divergence. Those zero limits
                // do not stop the drive: commanded 30 degrees, the chain lands within 0.84 deg at
                // maximumForce 1e13 and within 0.85 deg at maximumForce 100, so the shipped 1e13
                // is not what is overcoming them. Why gravity alone does not move the chain while
                // a 100-unit drive does is not established here, and no mechanism is claimed.
                new { label = "control-none-0-0", spring = 0f, damper = 0f },
            };

            var conditions = new List<DriveCondition>();
            foreach (var d in drives)
                foreach (var hz in new[] { 60f, 50f })
                    foreach (var accel in new[] { false, true })
                        foreach (var grav in new[] { false, true })
                            conditions.Add(new DriveCondition
                            {
                                driveLabel = d.label, spring = d.spring, damper = d.damper,
                                hz = hz, useAcceleration = accel, gravity = grav,
                            });

            var rows = new List<string>();
            var failures = 0;

            // SceneManager.CreateScene is play-mode only, so the isolated scene comes from
            // EditorSceneManager.NewPreviewScene -- which also keeps the probe out of whatever
            // scenes the user has open. A preview scene carries its own physics scene, which is
            // what makes stepping it by hand safe; if that ever stops being true, the run is
            // reported invalid rather than silently falling back onto the shared physics world.
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            try
            {
                var physics = scene.GetPhysicsScene();
                if (!physics.IsValid())
                {
                    Debug.LogError("[P25] preview scene has no valid physics scene; refusing to step the shared physics world.");
                    return;
                }
                for (var i = 0; i < conditions.Count; i++)
                {
                    var c = conditions[i];
                    EditorUtility.DisplayProgressBar(
                        "P25 ragdoll drive",
                        string.Format(CultureInfo.InvariantCulture,
                            "{0} @ {1} Hz accel={2} gravity={3}", c.driveLabel, c.hz, c.useAcceleration, c.gravity),
                        (float)i / conditions.Count);

                    var r = Measure(prefab, scene, physics, c);
                    if (!r.valid) failures++;
                    rows.Add(Row(c, r));
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                EditorSceneManagerCloseQuiet(scene);
            }

            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"generated\": \"").Append(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)).Append("\",\n");
            sb.Append("  \"producedBy\": \"Ironfront_Reborn/Assets/Editor/RecoveredPort/RagdollDriveProbe.cs\",\n");
            sb.Append("  \"engine\": \"").Append(Application.unityVersion).Append("\",\n");
            sb.Append("  \"prefab\": \"").Append(PrefabPath).Append("\",\n");
            sb.Append("  \"question\": \"How far do fixed timestep (50 vs 60 Hz) and JointDrive.useAcceleration move the slerp drive, at the drive values the game uses?\",\n");
            sb.Append("  \"cannotAnswer\": \"What Unity 5.4's native drive did. That is not expressed in managed metadata; this measures sensitivity to each knob on Unity 6 only.\",\n");
            sb.Append("  \"horizonSeconds\": ").Append(F(HorizonSeconds)).Append(",\n");
            sb.Append("  \"targetOffsetDegrees\": ").Append(F(TargetOffsetDegrees)).Append(",\n");
            sb.Append("  \"settleAngVelThresholdRadPerSec\": ").Append(F(SettleAngVelThreshold)).Append(",\n");
            sb.Append("  \"invalidConditions\": ").Append(failures.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"harnessSelfTest\": ").Append(HarnessSelfTest()).Append(",\n");
            sb.Append("  \"runs\": [\n    ").Append(string.Join(",\n    ", rows)).Append("\n  ]\n}\n");

            var outPath = Path.Combine(RestoreStaticFlags.RepoRoot(), "tools/recovered/ragdoll-drive-response.json");
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
            Debug.Log("[P25] wrote " + outPath + " (" + rows.Count + " conditions, " + failures + " invalid)");
        }

        static DriveResponse Measure(GameObject prefab, Scene scene, PhysicsScene physics, DriveCondition c)
        {
            var root = UnityEngine.Object.Instantiate(prefab);
            SceneManager.MoveGameObjectToScene(root, scene);
            root.transform.position = new Vector3(0f, 0f, 0f);
            root.transform.rotation = Quaternion.identity;

            try
            {
                var joints = root.GetComponentsInChildren<ConfigurableJoint>(true);
                if (joints.Length == 0)
                    return new DriveResponse { valid = false, invalidReason = "no ConfigurableJoint in prefab instance" };

                // EVERY rigidbody in the chain, not just the ones that own a joint. The hip is
                // the root of the ragdoll and carries no ConfigurableJoint, so a joint-only sweep
                // leaves it on its authored gravity and its authored sleep threshold -- which
                // makes "gravity = false" quietly mean "gravity off everywhere except the one
                // body that drags the rest down", and puts a free-fall confound in every row.
                var all = root.GetComponentsInChildren<Rigidbody>(true);
                if (all.Length == 0)
                    return new DriveResponse { valid = false, invalidReason = "no Rigidbody under prefab root" };

                // Anchor the root. Without this the whole ragdoll is in free fall, where every
                // body accelerates equally and the joints see no differential load -- so the
                // gravity condition would measure nothing, and the drive's ability to hold a pose
                // against real weight would never be exercised. Kinematic root = limbs hang from
                // a fixed hip, which is the load the live drive (700, 3) actually works against.
                Rigidbody rootBody = null;
                foreach (var rb in all)
                    if (rb.GetComponent<ConfigurableJoint>() == null) { rootBody = rb; break; }

                var bodies = new List<Rigidbody>(all.Length);
                foreach (var rb in all)
                {
                    if (rb == rootBody) { rb.isKinematic = true; continue; }
                    rb.isKinematic = false;
                    rb.useGravity = c.gravity;
                    rb.sleepThreshold = 0f;          // a sleeping body would time the sleep threshold
                    rb.linearVelocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    bodies.Add(rb);
                }
                if (bodies.Count == 0)
                    return new DriveResponse { valid = false, invalidReason = "no non-root Rigidbody to drive" };

                // The drive under test, built exactly as ActiveRaggy builds it: a default struct,
                // then maximumForce, then spring/damper. The only addition is useAcceleration,
                // which is the variable.
                var drive = default(JointDrive);
                drive.maximumForce = 1E+13f;         // ActiveRaggy.MAXIMUM_DRIVE_FORCE
                drive.positionSpring = c.spring;
                drive.positionDamper = c.damper;
                drive.useAcceleration = c.useAcceleration;

                var starts = new Quaternion[joints.Length];
                for (var i = 0; i < joints.Length; i++)
                {
                    joints[i].rotationDriveMode = RotationDriveMode.Slerp;
                    joints[i].slerpDrive = drive;
                    starts[i] = joints[i].transform.localRotation;
                }

                // Command a displaced pose through the same extension the game uses, so the
                // target-pose maths under test is the shipped maths, not a re-derivation.
                var offset = Quaternion.AngleAxis(TargetOffsetDegrees, Vector3.right);
                var targets = new Quaternion[joints.Length];
                for (var i = 0; i < joints.Length; i++)
                {
                    targets[i] = starts[i] * offset;
                    joints[i].SetTargetRotationLocal(targets[i], starts[i]);
                }

                var dt = 1f / c.hz;
                var steps = Mathf.RoundToInt(HorizonSeconds / dt);
                var holdSteps = Mathf.RoundToInt(SettleHoldSeconds / dt);

                var peakAngVel = 0f;
                var lastAngVel = 0f;
                var peakErr = 0f;
                var settleStep = -1;
                var quietFor = 0;
                var settleStepLoose = -1;
                var quietForLoose = 0;
                var meanErr = 0f;

                for (var s = 0; s < steps; s++)
                {
                    physics.Simulate(dt);

                    var maxAngVel = 0f;
                    foreach (var rb in bodies)
                    {
                        var m = rb.angularVelocity.magnitude;
                        if (m > maxAngVel) maxAngVel = m;
                    }
                    if (maxAngVel > peakAngVel) peakAngVel = maxAngVel;
                    lastAngVel = maxAngVel;

                    var sum = 0f;
                    for (var i = 0; i < joints.Length; i++)
                        sum += Quaternion.Angle(joints[i].transform.localRotation, targets[i]);
                    meanErr = sum / joints.Length;
                    if (meanErr > peakErr) peakErr = meanErr;

                    if (maxAngVel < SettleAngVelThreshold)
                    {
                        quietFor++;
                        if (quietFor >= holdSteps && settleStep < 0) settleStep = s - holdSteps + 1;
                    }
                    else quietFor = 0;

                    if (maxAngVel < SettleAngVelLooseThreshold)
                    {
                        quietForLoose++;
                        if (quietForLoose >= holdSteps && settleStepLoose < 0) settleStepLoose = s - holdSteps + 1;
                    }
                    else quietForLoose = 0;
                }

                return new DriveResponse
                {
                    valid = true,
                    peakAngularVelocity = peakAngVel,
                    finalAngularVelocity = lastAngVel,
                    settleSeconds = settleStep < 0 ? float.NaN : settleStep * dt,
                    settleSecondsLoose = settleStepLoose < 0 ? float.NaN : settleStepLoose * dt,
                    finalMeanAngleError = meanErr,
                    peakMeanAngleError = peakErr,
                    joints = joints.Length,
                    drivenBodies = bodies.Count,
                    steps = steps,
                };
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        static string Row(DriveCondition c, DriveResponse r)
        {
            var sb = new StringBuilder();
            sb.Append("{ \"drive\": \"").Append(c.driveLabel).Append("\"");
            sb.Append(", \"spring\": ").Append(F(c.spring));
            sb.Append(", \"damper\": ").Append(F(c.damper));
            sb.Append(", \"hz\": ").Append(F(c.hz));
            sb.Append(", \"useAcceleration\": ").Append(c.useAcceleration ? "true" : "false");
            sb.Append(", \"gravity\": ").Append(c.gravity ? "true" : "false");
            sb.Append(", \"valid\": ").Append(r.valid ? "true" : "false");
            if (!r.valid) { sb.Append(", \"invalidReason\": \"").Append(r.invalidReason).Append("\" }"); return sb.ToString(); }
            sb.Append(", \"joints\": ").Append(r.joints.ToString(CultureInfo.InvariantCulture));
            sb.Append(", \"drivenBodies\": ").Append(r.drivenBodies.ToString(CultureInfo.InvariantCulture));
            sb.Append(", \"steps\": ").Append(r.steps.ToString(CultureInfo.InvariantCulture));
            sb.Append(", \"peakAngularVelocityRadPerSec\": ").Append(F(r.peakAngularVelocity));
            sb.Append(", \"finalAngularVelocityRadPerSec\": ").Append(F(r.finalAngularVelocity));
            sb.Append(", \"settleSeconds\": ").Append(float.IsNaN(r.settleSeconds) ? "null" : F(r.settleSeconds));
            sb.Append(", \"settleSecondsLoose\": ").Append(float.IsNaN(r.settleSecondsLoose) ? "null" : F(r.settleSecondsLoose));
            sb.Append(", \"peakMeanAngleErrorDeg\": ").Append(F(r.peakMeanAngleError));
            sb.Append(", \"finalMeanAngleErrorDeg\": ").Append(F(r.finalMeanAngleError));
            sb.Append(" }");
            return sb.ToString();
        }

        /// <summary>
        /// Prove the preview scene's physics actually has gravity, by dropping one free body for
        /// one second and checking it fell about 4.9 m.
        ///
        /// This exists because the drive control above CANNOT establish it: the ragdoll's joint
        /// limits are all zero, so with the drive off the chain does not move under gravity
        /// either, and "gravity on and gravity off look the same" has two possible causes --
        /// a strong drive, or a dead switch. Without this check the gravity column would be
        /// exactly the kind of green that proves nothing.
        /// </summary>
        static string HarnessSelfTest()
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            try
            {
                var ps = scene.GetPhysicsScene();
                var go = new GameObject("gravity-self-test");
                SceneManager.MoveGameObjectToScene(go, scene);
                go.transform.position = Vector3.zero;
                var rb = go.AddComponent<Rigidbody>();
                rb.useGravity = true;
                rb.isKinematic = false;
                for (var i = 0; i < 60; i++) ps.Simulate(1f / 60f);

                var fell = -go.transform.position.y;
                var expected = 0.5f * 9.81f;                 // ~4.905 m in 1 s
                var ok = Mathf.Abs(fell - expected) < 0.25f;
                return "{ \"freeBodyFellMetresIn1s\": " + F(fell)
                     + ", \"expectedMetres\": " + F(expected)
                     + ", \"gravityConfirmed\": " + (ok ? "true" : "false") + " }";
            }
            finally { EditorSceneManagerCloseQuiet(scene); }
        }

        static string F(float v) { return v.ToString("0.#####", CultureInfo.InvariantCulture); }

        static void EditorSceneManagerCloseQuiet(Scene scene)
        {
            try { UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene); }
            catch (Exception e) { Debug.LogWarning("[P25] probe scene close: " + e.Message); }
        }
    }
}
