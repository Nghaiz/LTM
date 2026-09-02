using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ironfront.Tools.ClientWiringGate
{
    /// <summary>
    /// The authoring checks for the P15 menu Canvas: one per screen, over the fields that decide
    /// whether its controls do anything. P15 3.4.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why these exist at all.</b> The nine existing <see cref="AssetWiringDetectors"/> checks
    /// passed <c>Ingame UI Container.prefab</c> with <c>capturePointMarkerPrefab</c> null
    /// (P3 § 3.3): an authoring gate only sees the fields it was told about, and is otherwise
    /// exactly as green as one that checks nothing. Four new screens with twenty-two references
    /// between them is precisely the surface that failure lives on.
    /// </para>
    /// <para>
    /// <b>The shape is <c>ScoreUiTextRefsAreAssigned</c>'s, and that shape was earned.</b> Its
    /// first draft compared each field against its own fallback and was proved green, by
    /// mutation, on two authorings it exists to forbid — cross-swapped assignments, and fileIDs
    /// naming no object at all (which Unity deserializes to null, so the gate reported clean
    /// while the field was effectively unassigned). So every field below is graded on three
    /// clauses, not one:
    /// </para>
    /// <list type="number">
    /// <item>assigned at all — <c>fileID: 0</c> is the unassigned case;</item>
    /// <item>the anchor resolves to an object that exists in the asset — a fileID naming nothing
    /// loads as null and is the unassigned case wearing a number;</item>
    /// <item>no two fields on one screen name the same object — a Log-in button that is also the
    /// Create-account button resolves perfectly and is still one missing control.</item>
    /// </list>
    /// <para>
    /// <b>Why fields and not authored <c>m_OnClick</c> entries.</b> The screens wire their buttons
    /// with <c>AddListener</c> over a serialized <c>Button</c> reference, so the field IS the
    /// whole failure surface — an unassigned one means no listener and a dead button, and there
    /// is nothing else to check. The authored-persistent-call alternative would additionally
    /// store the method name and the assembly-qualified type name as strings, and a rename breaks
    /// those silently in a way no YAML check can grade. This detector can therefore see every
    /// fault its screens can have, which is the property <c>green-that-proves-nothing.md</c> asks
    /// for and the reason the wiring is shaped this way rather than the check being bent to fit.
    /// </para>
    /// <para>
    /// <b>Script guids are read from the <c>.cs.meta</c> beside each source file</b>, unlike the
    /// hardcoded constants in <see cref="AssetWiringDetectors"/>. Same reason those are guids at
    /// all — the YAML carries a guid, not a type name, and these assemblies are not loadable here
    /// — but a constant transcribed once can go stale silently if a script is ever reimported
    /// with a new guid, and the failure mode of a stale constant is a check that matches zero
    /// instances and reports nothing. Reading the meta cannot drift, and a missing meta throws
    /// <see cref="AssetGateUnknownException"/> rather than passing.
    /// </para>
    /// </remarks>
    public static class MenuScreenWiringDetectors
    {
        /// <summary>The ledger row these findings are filed under.</summary>
        private const string Row = "P15";

        /// <summary>
        /// A screen: the script that draws it, and every reference that must be authored on it.
        /// </summary>
        /// <remarks>
        /// <c>Consequence</c> is per FIELD rather than per screen because "this button does
        /// nothing" and "the error line never renders" send a reader to different places, and a
        /// gate whose message is generic costs the reader the investigation the gate was supposed
        /// to have done.
        /// </remarks>
        private readonly struct Screen
        {
            public Screen(string name, string sourcePath, params (string Field, string Consequence)[] fields)
            {
                Name = name;
                SourcePath = sourcePath;
                Fields = fields;
            }

            public string Name { get; }

            /// <summary>Relative to the Assets root, so the guid is read from its <c>.meta</c>.</summary>
            public string SourcePath { get; }

            public (string Field, string Consequence)[] Fields { get; }
        }

        private static readonly Screen[] Screens =
        {
            new Screen(
                "MenuScreenController", "Scripts/Net/Client/Menu/MenuScreenController.cs",
                ("_titleScreen",
                 "the Title screen never becomes visible, so a player reaching the menu sees "
                 + "nothing at all and criterion 1 fails on the pixels"),
                ("_loginScreen",
                 "Multiplayer moves the flow to LoginScreen and no form appears, which reads as "
                 + "the button being broken rather than the screen being unassigned"),
                ("_registerScreen",
                 "'Create an account' does nothing visible, so criterion 2 has no way in"),
                ("_authenticatingScreen",
                 "the screen goes blank while the master is answering, which is indistinguishable "
                 + "from a hang"),
                ("_lobbyScreen",
                 "a successful login lands on an empty screen, so criterion 2's second shot has "
                 + "nothing to show"),
                ("_practiceBackBar",
                 "Practice reveals the legacy menu with no way back to multiplayer short of "
                 + "restarting the game"),
                ("_practiceBackButton",
                 "the Back bar renders but its button is unwired, which is worse than no bar: the "
                 + "player presses it"),
                ("_signedInText",
                 "the Lobby screen cannot say WHO is signed in, which is the evidence criterion 2 "
                 + "is graded on")),

            new Screen(
                "MenuTitleScreen", "Scripts/Net/Client/Menu/MenuTitleScreen.cs",
                ("_controller",
                 "both Title buttons are wired to a null controller, so the primary path into "
                 + "multiplayer does nothing — F1 exactly as it was before this phase"),
                ("_multiplayerButton",
                 "no listener is added to the primary action, so there is still no way into "
                 + "multiplayer from the menu (criterion 1)"),
                ("_practiceButton",
                 "Practice is unreachable and the offline game loses its entry (criterion 5)")),

            new Screen(
                "MenuLoginScreen", "Scripts/Net/Client/Menu/MenuLoginScreen.cs",
                ("_controller",
                 "Log in and Create an account are both inert, so no account can be used or made"),
                ("_usernameField",
                 "the username reads as empty, so every login is refused with a message about a "
                 + "field the player did fill in"),
                ("_passwordField",
                 "the password reads as empty AND is never cleared, so it is neither sent nor "
                 + "dropped"),
                ("_logInButton",
                 "no listener is added, so the login form cannot be submitted at all"),
                ("_createAccountButton",
                 "the register screen is unreachable, so criterion 2 cannot be performed"),
                ("_errorText",
                 "a wrong password produces NO visible message, which is criterion 3 failing "
                 + "exactly as the M3 clause describes")),

            new Screen(
                "MenuRegisterScreen", "Scripts/Net/Client/Menu/MenuRegisterScreen.cs",
                ("_controller",
                 "Create and Back are both inert, so an account cannot be made from the UI"),
                ("_usernameField",
                 "the username reads as empty and the master refuses it as invalid"),
                ("_passwordField",
                 "the password reads as empty, so the account is created against a hash of "
                 + "nothing — or refused, depending on the master"),
                ("_confirmPasswordField",
                 "the confirmation reads as empty and never matches, so registration is blocked "
                 + "by a check meant to catch typos"),
                ("_displayNameField",
                 "the display name is always blank, so the optional field silently is not one"),
                ("_createButton",
                 "no listener is added, so the register form cannot be submitted (criterion 2)"),
                ("_backButton",
                 "there is no way back to the login form once the register screen is up"),
                ("_errorText",
                 "'that username is already taken' renders nowhere, so a failed registration "
                 + "looks like a frozen button")),
        };

        /// <summary>
        /// <b>P15</b> — every reference each menu screen needs is assigned, resolves to an object
        /// that exists, and is not an object another field on the same screen already drives.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Zero instances is a finding, not an exception</b> — the call
        /// <c>ScoreUiTextRefsAreAssigned</c> makes, for its reason: an absent component and an
        /// unassigned field render the same nothing, so a check satisfiable by deleting the
        /// screen would be satisfiable by deleting the menu. Deleting the Canvas is precisely the
        /// regression this phase's criterion 1 forbids, so it must be the loudest case, not the
        /// quietest.
        /// </para>
        /// <para>
        /// <b>What this deliberately does not check: where the control sits.</b> A Button
        /// reference pointing at a genuine, unclaimed Button that lives on a different screen
        /// passes every clause here and is still wrong. That is the same boundary
        /// <c>ScoreUiTextRefsAreAssigned</c> draws and for the same reason — descendant-of-this-
        /// panel is a LAYOUT invariant, YAML can say a reference resolves but never that a player
        /// can see or reach it, and encoding it would fail a legitimate reorganisation. Criteria
        /// 1, 2, 3 and 5 are screenshots because of exactly this gap.
        /// </para>
        /// </remarks>
        public static IEnumerable<GateFinding> MenuScreenRefsAreAssigned(UnityAssetIndex index)
        {
            var findings = new List<GateFinding>();

            foreach (Screen screen in Screens)
            {
                string guid = ScriptGuid(index, screen.SourcePath);
                int seen = 0;

                foreach ((UnityAssetDocument document, string path)
                         in AssetWiringDetectors.Instances(index, guid))
                {
                    seen++;
                    findings.AddRange(Grade(index, screen, document, path));
                }

                if (seen == 0)
                    findings.Add(new GateFinding(
                        Row, "(nothing)", 0,
                        $"{screen.Name} is on no GameObject in any scene or prefab, so its "
                        + "references are unassignable by construction and that screen does not "
                        + "exist. Run 'Ironfront/Net/Build multiplayer menu Canvas' (P15 3.2)."));
            }

            return findings;
        }

        private static IEnumerable<GateFinding> Grade(
            UnityAssetIndex index, Screen screen, UnityAssetDocument document, string path)
        {
            var findings = new List<GateFinding>();

            foreach ((string field, string consequence) in screen.Fields)
            {
                UnityObjectRef? maybe = document.Reference(field);

                if (maybe == null || maybe.Value.IsNull)
                {
                    findings.Add(new GateFinding(
                        Row, AssetWiringDetectors.Rel(index, path), 0,
                        $"{screen.Name}.{field} is unassigned, so {consequence} (P15 3.4)."));
                    continue;
                }

                UnityObjectRef assigned = maybe.Value;

                // A reference into another asset is legal YAML but wrong here: every one of these
                // fields names an object on the same Canvas, authored by the same builder run.
                string? target = assigned.Guid == null ? path : index.PathOf(assigned.Guid);

                if (target == null)
                    throw new AssetGateUnknownException(
                        $"{path}: {screen.Name}.{field} names guid {assigned.Guid}, which no "
                        + "asset in the tree carries. The reference is dangling; this check "
                        + "cannot grade it.");

                bool resolves = index.Documents(target)
                    .Any(d => d.AnchorId == assigned.FileId);

                if (!resolves)
                    findings.Add(new GateFinding(
                        Row, AssetWiringDetectors.Rel(index, path), 0,
                        $"{screen.Name}.{field} names fileID {assigned.FileId}, which no object "
                        + $"in {AssetWiringDetectors.Rel(index, target)} carries. Unity loads "
                        + $"that as null, so {consequence} — and it reads exactly like the "
                        + "unassigned case at runtime (P15 3.4)."));

                foreach ((string other, string _) in screen.Fields)
                {
                    if (other == field) continue;

                    UnityObjectRef? held = document.Reference(other);
                    if (held == null || held.Value.IsNull) continue;
                    if (held.Value.FileId != assigned.FileId) continue;
                    if (!string.Equals(held.Value.Guid, assigned.Guid,
                                       StringComparison.OrdinalIgnoreCase)) continue;

                    findings.Add(new GateFinding(
                        Row, AssetWiringDetectors.Rel(index, path), 0,
                        $"{screen.Name}.{field} points at the same object as {other}. Two "
                        + "controls cannot be one object: whichever is written last wins, so "
                        + $"this does not add a control — it takes one over, and {consequence} "
                        + "(P15 3.4)."));
                }
            }

            return findings;
        }

        /// <summary>
        /// The guid Unity assigned <paramref name="sourcePath"/>, read from its <c>.meta</c>.
        /// </summary>
        /// <remarks>
        /// A missing meta or a meta with no guid throws rather than returning something that
        /// would match nothing: a guid that matches no instance produces the "on no GameObject"
        /// finding above, which would send a reader looking for an unauthored Canvas when the
        /// real fault is a moved source file.
        /// </remarks>
        private static string ScriptGuid(UnityAssetIndex index, string sourcePath)
        {
            string meta = Path.Combine(index.AssetsRoot, sourcePath.Replace('/', Path.DirectorySeparatorChar))
                          + ".meta";

            if (!File.Exists(meta))
                throw new AssetGateUnknownException(
                    $"no .meta beside '{sourcePath}', so its script guid cannot be read and the "
                    + "menu screens cannot be graded. Has the file moved?");

            foreach (string line in File.ReadLines(meta))
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith("guid:", StringComparison.Ordinal)) continue;

                string guid = trimmed.Substring("guid:".Length).Trim();
                if (guid.Length > 0) return guid;
            }

            throw new AssetGateUnknownException(
                $"'{sourcePath}.meta' carries no guid line, so the menu screens cannot be graded.");
        }
    }
}
