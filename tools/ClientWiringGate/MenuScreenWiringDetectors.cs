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
                : this(name, sourcePath, System.Array.Empty<(string, int, string)>(), fields)
            {
            }

            public Screen(
                string name,
                string sourcePath,
                (string Field, int Length, string Consequence)[] arrays,
                params (string Field, string Consequence)[] fields)
            {
                Name = name;
                SourcePath = sourcePath;
                Arrays = arrays;
                Fields = fields;
            }

            public string Name { get; }

            /// <summary>Relative to the Assets root, so the guid is read from its <c>.meta</c>.</summary>
            public string SourcePath { get; }

            public (string Field, string Consequence)[] Fields { get; }

            /// <summary>
            /// Serialized reference ARRAYS, with the length the screen must have.
            /// </summary>
            /// <remarks>
            /// Graded separately from <see cref="Fields"/> because an array has a failure the
            /// single references do not: the right entries at the WRONG LENGTH. A roster sized
            /// six on a sixteen-seat room drops four players with no null anywhere in the asset,
            /// so every clause a single field is graded on would pass.
            /// </remarks>
            public (string Field, int Length, string Consequence)[] Arrays { get; }
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

            // ---------------------------------------------------------------- P16 3.7

            new Screen(
                "MenuRoomBrowserScreen", "Scripts/Net/Client/Menu/MenuRoomBrowserScreen.cs",
                new[]
                {
                    ("_roomButtons", RoomBrowserRows,
                     "the rows a player presses to join are missing or the wrong number of them "
                     + "exists, so some rooms are listed with no way in and criterion 1 shows a "
                     + "list that cannot be used"),
                    ("_roomLabels", RoomBrowserRows,
                     "a row renders as a blank button: the name, map, players, lifecycle and "
                     + "lock glyph criterion 1 is graded on all have nowhere to be written"),
                },
                ("_controller",
                 "every control on the browser is inert, so there is no way from the signed-in "
                 + "screen into a room at all -- F2 exactly as the audit found it"),
                ("_refreshButton",
                 "the list can never be re-fetched, so a room created on the OTHER machine never "
                 + "appears and criterion 2 cannot be performed"),
                ("_createRoomButton",
                 "the create-room form is unreachable, so criterion 2's first step -- make a "
                 + "room from the UI -- has no button"),
                ("_pingText",
                 "the labelled master round trip renders nowhere, which is half of criterion 1"),
                ("_overflowText",
                 "a ninth room is dropped SILENTLY rather than counted, so the browser lies "
                 + "about what the master returned"),
                ("_errorText",
                 "'that room is full' and the private-room prompt render nowhere, so a refused "
                 + "click looks like a dead button"),
                ("_passwordPrompt",
                 "a private room can never be entered: the prompt cannot be shown, so criterion "
                 + "7 has nothing to type into"),
                ("_passwordField",
                 "the prompt appears with no field, so the password reads as empty and the join "
                 + "is refused for a reason the player cannot fix"),
                ("_passwordJoinButton",
                 "the password prompt has no way to submit, so a private room is unenterable "
                 + "even with the right password (criterion 7)"),
                ("_passwordCancelButton",
                 "the prompt cannot be dismissed, so a mis-click on a private room traps the "
                 + "player on the browser")),

            new Screen(
                "MenuCreateRoomScreen", "Scripts/Net/Client/Menu/MenuCreateRoomScreen.cs",
                ("_controller",
                 "Create and Back are both inert, so a room cannot be made from the UI and "
                 + "criterion 2 cannot start"),
                ("_nameField",
                 "the room name reads as empty and the form refuses itself, blaming the player "
                 + "for a field they did fill in"),
                ("_mapDropdown",
                 "the map cannot be chosen, so every room is made on the default and P18's "
                 + "Island is unreachable from the UI"),
                ("_maxPlayersField",
                 "the seat count reads as empty, so criterion 8's even-number check has no "
                 + "input to refuse and no screenshot to be graded on"),
                ("_botCountField",
                 "the bot count is always zero, so the field silently is not one"),
                ("_privateToggle",
                 "no room can be made private, so criterion 7 has no private room to join"),
                ("_passwordField",
                 "a private room is created with an empty password, which the master refuses -- "
                 + "or worse, accepts, leaving a room nobody can enter"),
                ("_createButton",
                 "no listener is added, so the form cannot be submitted at all"),
                ("_backButton",
                 "there is no way back to the browser once the form is up"),
                ("_errorText",
                 "'players must be an even number' renders nowhere, so criterion 8 fails on the "
                 + "pixels even though the check runs")),

            new Screen(
                "MenuRoomLobbyScreen", "Scripts/Net/Client/Menu/MenuRoomLobbyScreen.cs",
                new[]
                {
                    ("_teamZeroRows", RosterRowsPerSide,
                     "team 1's roster cannot show every member a full room can hold, so "
                     + "criteria 2 and 3 are graded on a column that silently truncates"),
                    ("_teamOneRows", RosterRowsPerSide,
                     "team 2's roster cannot show every member a full room can hold, so a "
                     + "player who switches side can vanish from BOTH columns"),
                },
                ("_controller",
                 "ready, switch side, chat and leave are all inert, so a player who reaches a "
                 + "room can do nothing in it and criterion 2 stops one step short"),
                ("_teamZeroHeading",
                 "team 1's column is unlabelled and uncoloured, so the two sides criterion 3 is "
                 + "graded on are told apart by position alone"),
                ("_teamOneHeading",
                 "team 2's column is unlabelled and uncoloured, with the same consequence"),
                ("_switchSideButton",
                 "there is no way to change side, so criteria 3, 4 and 5 have no control to "
                 + "press"),
                ("_switchSideLabel",
                 "the control cannot say SIDES LOCKED, so criterion 5's screenshot shows a "
                 + "greyed button with no stated reason"),
                ("_readyButton",
                 "nobody can mark ready, so P14's start rule is never satisfied and criterion "
                 + "2's match never begins"),
                ("_readyLabel",
                 "the button cannot say whether pressing it marks ready or unready, so its state "
                 + "is invisible in the screenshot criterion 2 is graded on"),
                ("_leaveButton",
                 "a player who joins a room can only leave it by quitting the process"),
                ("_headingText",
                 "the room's name renders nowhere, so two screenshots from two machines cannot "
                 + "be shown to be of the SAME room"),
                ("_statusText",
                 "the lifecycle and the start condition render nowhere, so a room waiting for a "
                 + "second player is indistinguishable from one that is stuck"),
                ("_errorText",
                 "the refusals in criteria 4 and 5 render nowhere, which is those two criteria "
                 + "failing exactly as written"),
                ("_chatLog",
                 "a delivered chat line is dropped rather than shown, so criterion 6 fails on "
                 + "the receiving machine"),
                ("_chatField",
                 "nothing can be typed, so criterion 6 has no message to send"),
                ("_chatSendButton",
                 "no listener is added, so lobby chat cannot be sent at all")),
        };

        /// <summary>
        /// Row counts the screens declare in code, mirrored here.
        /// </summary>
        /// <remarks>
        /// Transcribed rather than referenced because this tool cannot load the Unity assemblies
        /// the constants live in -- the same reason script guids are read from <c>.meta</c>. The
        /// transcription is guarded: <see cref="RowCountsMatchTheScreens"/> reads the numbers back
        /// out of the source files, so a change on either side fails the gate rather than
        /// silently grading the old shape.
        /// </remarks>
        private const int RoomBrowserRows = 8;

        /// <summary>Half of <c>ProtocolConstants.MAX_PLAYERS</c>. Guarded as above.</summary>
        private const int RosterRowsPerSide = 8;

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

            findings.AddRange(GradeArrays(index, screen, document, path));

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
        /// Grades one screen's serialized reference ARRAYS. P16 3.7.
        /// </summary>
        /// <remarks>
        /// Four clauses, and the first is the one a per-field check cannot express: the array is
        /// the RIGHT LENGTH. A roster of six rows on a sixteen-seat room, or a browser of four
        /// rows, contains no null and no duplicate and is still a screen that hides players from
        /// the two people comparing screenshots.
        /// </remarks>
        private static IEnumerable<GateFinding> GradeArrays(
            UnityAssetIndex index, Screen screen, UnityAssetDocument document, string path)
        {
            var findings = new List<GateFinding>();
            string rel = AssetWiringDetectors.Rel(index, path);

            foreach ((string field, int length, string consequence) in screen.Arrays)
            {
                IReadOnlyList<UnityObjectRef>? entries = document.ReferenceArray(field);

                if (entries == null)
                {
                    findings.Add(new GateFinding(
                        Row, rel, 0,
                        $"{screen.Name}.{field} is not an authored array at all, so "
                        + $"{consequence} (P16 3.7)."));
                    continue;
                }

                if (entries.Count != length)
                {
                    findings.Add(new GateFinding(
                        Row, rel, 0,
                        $"{screen.Name}.{field} holds {entries.Count} entries and the screen "
                        + $"needs {length}. Nothing in the asset is null and nothing is "
                        + $"duplicated, so no other clause here can see it -- and "
                        + $"{consequence} (P16 3.7)."));
                    continue;
                }

                var seen = new Dictionary<string, int>(StringComparer.Ordinal);

                for (int i = 0; i < entries.Count; i++)
                {
                    UnityObjectRef entry = entries[i];

                    if (entry.IsNull)
                    {
                        findings.Add(new GateFinding(
                            Row, rel, 0,
                            $"{screen.Name}.{field}[{i}] is unassigned, so {consequence} "
                            + "(P16 3.7)."));
                        continue;
                    }

                    string? target = entry.Guid == null ? path : index.PathOf(entry.Guid);

                    if (target == null)
                        throw new AssetGateUnknownException(
                            $"{path}: {screen.Name}.{field}[{i}] names guid {entry.Guid}, which "
                            + "no asset in the tree carries. The reference is dangling; this "
                            + "check cannot grade it.");

                    if (!index.Documents(target).Any(d => d.AnchorId == entry.FileId))
                        findings.Add(new GateFinding(
                            Row, rel, 0,
                            $"{screen.Name}.{field}[{i}] names fileID {entry.FileId}, which no "
                            + $"object in {AssetWiringDetectors.Rel(index, target)} carries. "
                            + $"Unity loads that as null, so {consequence} -- and it reads "
                            + "exactly like the unassigned case at runtime (P16 3.7)."));

                    string key = entry.Guid + "/" + entry.FileId;
                    if (seen.TryGetValue(key, out int first))
                        findings.Add(new GateFinding(
                            Row, rel, 0,
                            $"{screen.Name}.{field}[{i}] points at the same object as [{first}]. "
                            + "Two rows cannot be one object: whichever is written last wins, so "
                            + $"this row does not exist and {consequence} (P16 3.7)."));
                    else
                        seen.Add(key, i);
                }
            }

            return findings;
        }

        /// <summary>
        /// <b>P16</b> — the roster's colours come from <c>ITeamPalette</c>, not from the asset.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Criterion 10, and the reason it needs a check of its own.</b> Every other clause
        /// here grades whether a reference RESOLVES. A roster whose two columns were authored red
        /// and blue in the scene resolves perfectly, renders correctly on the day, and is a
        /// second copy of the team-colour mapping that <c>ColorScheme.TeamColor</c> already owns
        /// (contracts § 6.3) -- so the palette seam becomes decoration and the two copies drift
        /// the first time the game's colours change.
        /// </para>
        /// <para>
        /// <b>Two clauses, because either alone is a green that proves nothing.</b> The asset
        /// clause says the authored colours are all the SAME, which is what an unpainted roster
        /// looks like; on its own it also passes an all-grey roster whose runtime colouring was
        /// deleted. The source clause says the runtime colouring is still there; on its own it
        /// passes a screen that calls the palette AND has red baked into the asset underneath.
        /// Together they pin the failure from both sides.
        /// </para>
        /// <para>
        /// <b>Identity, not a transcribed constant.</b> Comparing against the builder's own ink
        /// value would be a fourth copy of a colour, and would go stale the day the menu is
        /// restyled. What is asserted is that the roster is uniform, which stays true under any
        /// restyle and false under exactly the authoring this forbids.
        /// </para>
        /// </remarks>
        public static IEnumerable<GateFinding> RoomLobbyTeamColoursComeFromThePalette(
            UnityAssetIndex index)
        {
            const string Source = "Scripts/Net/Client/Menu/MenuRoomLobbyScreen.cs";
            const string PaletteCall = "NetClientBindings.TeamColourRgb";

            var findings = new List<GateFinding>();

            string sourceFile = Path.Combine(
                index.AssetsRoot, Source.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(sourceFile))
                throw new AssetGateUnknownException(
                    $"no '{Source}' to read, so criterion 10's runtime half cannot be graded. "
                    + "Has the screen moved?");

            if (!File.ReadAllText(sourceFile).Contains(PaletteCall, StringComparison.Ordinal))
                findings.Add(new GateFinding(
                    Row, Source, 0,
                    $"MenuRoomLobbyScreen no longer calls {PaletteCall}, so the roster renders "
                    + "in whatever colour the scene authored and the ITeamPalette seam is "
                    + "decoration. Criterion 10 (P16 3.7)."));

            string guid = ScriptGuid(index, Source);
            int seen = 0;

            foreach ((UnityAssetDocument document, string path)
                     in AssetWiringDetectors.Instances(index, guid))
            {
                seen++;
                findings.AddRange(GradeColours(index, document, path));
            }

            if (seen == 0)
                findings.Add(new GateFinding(
                    Row, "(nothing)", 0,
                    "MenuRoomLobbyScreen is on no GameObject in any scene or prefab, so there is "
                    + "no roster to colour and criteria 2, 3, 4, 5 and 6 have no screen. Run "
                    + "'Ironfront/Net/Build multiplayer menu Canvas' (P16 3.4)."));

            return findings;
        }

        private static IEnumerable<GateFinding> GradeColours(
            UnityAssetIndex index, UnityAssetDocument document, string path)
        {
            var findings = new List<GateFinding>();
            string rel = AssetWiringDetectors.Rel(index, path);

            var colours = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (string field in new[]
                     { "_teamZeroHeading", "_teamOneHeading", "_teamZeroRows", "_teamOneRows" })
            {
                foreach (UnityObjectRef reference in Referenced(document, field))
                {
                    if (reference.IsNull) continue;

                    // A colour lives on the Text component in the SAME asset; the assigned-and-
                    // resolves clauses above already report a reference that leaves it.
                    if (reference.Guid != null) continue;

                    UnityAssetDocument? text = index.Documents(path)
                        .FirstOrDefault(d => d.AnchorId == reference.FileId);

                    string? colour = text?.Scalar("m_Color");
                    if (colour == null) continue;

                    if (!colours.ContainsKey(colour)) colours.Add(colour, $"{field} ({reference.FileId})");
                }
            }

            if (colours.Count > 1)
                findings.Add(new GateFinding(
                    Row, rel, 0,
                    "the roster carries " + colours.Count + " different authored colours ("
                    + string.Join("; ", colours.Select(pair => $"{pair.Value} = {pair.Key}"))
                    + "). A team colour authored in the scene is a second copy of the mapping "
                    + "ColorScheme.TeamColor owns, and it is the copy that wins at load: "
                    + "ITeamPalette then decorates a decision the asset has already made. "
                    + "Criterion 10 (P16 3.7)."));

            return findings;
        }

        /// <summary>One field's references, whether it is a single reference or an array.</summary>
        private static IEnumerable<UnityObjectRef> Referenced(
            UnityAssetDocument document, string field)
        {
            IReadOnlyList<UnityObjectRef>? array = document.ReferenceArray(field);
            if (array != null) return array;

            UnityObjectRef? single = document.Reference(field);
            return single == null
                ? System.Array.Empty<UnityObjectRef>()
                : new[] { single.Value };
        }

        /// <summary>
        /// <b>P16</b> — the row counts transcribed above still match the screens' own constants.
        /// </summary>
        /// <remarks>
        /// The transcription exists because this tool cannot load the Unity assemblies. A stale
        /// one fails in the quietest possible way: the gate would assert the OLD length, pass a
        /// correctly-rebuilt Canvas, and report a screen as fully wired while grading a shape
        /// nothing has any more. Read back from the source so a change on either side is loud.
        /// </remarks>
        public static IEnumerable<GateFinding> RowCountsMatchTheScreens(UnityAssetIndex index)
        {
            var findings = new List<GateFinding>();

            int browser = ReadConstant(
                index, "Scripts/Net/Client/Menu/MenuRoomBrowserScreen.cs", "public const int Rows = ");

            if (browser != RoomBrowserRows)
                findings.Add(new GateFinding(
                    Row, "Scripts/Net/Client/Menu/MenuRoomBrowserScreen.cs", 0,
                    $"MenuRoomBrowserScreen.Rows is {browser}; this gate grades the browser's "
                    + $"row arrays against {RoomBrowserRows}. Update RoomBrowserRows in "
                    + "MenuScreenWiringDetectors.cs in the same commit, or the check passes a "
                    + "Canvas built to the new shape while asserting the old one (P16 3.7)."));

            int roster = ReadConstant(
                index, "Scripts/Net/Client/Menu/MenuRoomLobbyScreen.cs",
                "public const int RowsPerSide = ");

            if (roster != RosterRowsPerSide)
                findings.Add(new GateFinding(
                    Row, "Scripts/Net/Client/Menu/MenuRoomLobbyScreen.cs", 0,
                    $"MenuRoomLobbyScreen.RowsPerSide resolves to {roster}; this gate grades the "
                    + $"roster arrays against {RosterRowsPerSide}. Update RosterRowsPerSide in "
                    + "MenuScreenWiringDetectors.cs in the same commit (P16 3.7)."));

            return findings;
        }

        /// <summary>
        /// The integer a named <c>const</c> line declares, resolving the one expression used.
        /// </summary>
        /// <remarks>
        /// <c>RowsPerSide</c> is written <c>ProtocolConstants.MAX_PLAYERS / 2</c> so the roster
        /// cannot silently fall behind the protocol. Rather than teach this a C# evaluator, the
        /// one form that appears is resolved by name; anything else throws, because a constant
        /// this cannot read is one it must not guess at.
        /// </remarks>
        private static int ReadConstant(UnityAssetIndex index, string sourcePath, string declaration)
        {
            string file = Path.Combine(
                index.AssetsRoot, sourcePath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(file))
                throw new AssetGateUnknownException(
                    $"no '{sourcePath}' to read '{declaration.Trim()}' from. Has the screen "
                    + "moved? This gate's row counts cannot be checked against it.");

            foreach (string line in File.ReadLines(file))
            {
                int at = line.IndexOf(declaration, StringComparison.Ordinal);
                if (at < 0) continue;

                string value = line.Substring(at + declaration.Length).TrimEnd(';', ' ').Trim();

                if (int.TryParse(value, out int literal)) return literal;

                if (value == "ProtocolConstants.MAX_PLAYERS / 2")
                    return Ironfront.Net.Protocol.ProtocolConstants.MAX_PLAYERS / 2;

                throw new AssetGateUnknownException(
                    $"'{sourcePath}' declares '{declaration.Trim()}{value}', which this gate "
                    + "cannot evaluate. Add the form to ReadConstant rather than letting the "
                    + "row-count guard silently stop guarding.");
            }

            throw new AssetGateUnknownException(
                $"'{sourcePath}' no longer declares '{declaration.Trim()}', so the row counts "
                + "this gate transcribes cannot be checked against it.");
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
