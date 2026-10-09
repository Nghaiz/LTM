#nullable enable

using System.Text.RegularExpressions;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Overlay
{
    /// <summary>
    /// What the guide says: one tab per subject, each a set of cards. Words only; the page draws them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Keys are never written into the text.</b> A card names an action in braces --
    /// <c>{Use}</c>, <c>{Scoreboard}</c> -- and <see cref="Keys"/> swaps in the player's own key when
    /// the card is drawn, so a rebound key reads correctly everywhere the guide mentions it (owner's
    /// list of 2026-10-09: "update this guide with the player's key bindings").
    /// </para>
    /// <para>
    /// Every number here is the game's own: three seconds to respawn, five metres to a supply cache,
    /// three to twenty metres of fall, lead-by and first-to points from the room rules.
    /// </para>
    /// </remarks>
    public static class HowToPlayContent
    {
        public readonly struct Card
        {
            public Card(string icon, string title, string body)
            {
                Icon = icon;
                Title = title;
                Body = body;
            }

            public string Icon { get; }
            public string Title { get; }
            public string Body { get; }
        }

        public readonly struct Tab
        {
            public Tab(string id, string caption, string icon, string lead, Card[] cards)
            {
                Id = id;
                Caption = caption;
                Icon = icon;
                Lead = lead;
                Cards = cards;
            }

            public string Id { get; }
            public string Caption { get; }
            public string Icon { get; }
            public string Lead { get; }
            public Card[] Cards { get; }
        }

        /// <summary>The id of the controls tab, which the page draws from the live key bindings.</summary>
        public const string ControlsTab = "controls";

        public static readonly Tab[] Tabs =
        {
            new Tab("basics", "BASICS", "book",
                "Two armies, Blue and Red, fight for the flags of one map. Soldiers, bots, tanks, jeeps, boats and helicopters, up to sixteen players and a hundred bots in one battle.",
                new[]
                {
                    new Card("flag", "THE GOAL", "Hold flags and win fights. Every enemy who dies scores your side as many points as you hold flags, so a side that holds the map wins the war of numbers."),
                    new Card("trophy", "WINNING A ROUND", "A room plays to its own rule: <b>LEAD BY</b> a margin (200 points unless the host chose otherwise), or <b>FIRST TO</b> a total (500 by default). The round ends the moment a side gets there, and the next round starts on the same map."),
                    new Card("people", "YOUR FIRST MATCH", "MULTIPLAYER, then sign in or create an account, and the room list opens. JOIN a room, QUICK MATCH into the fullest one, or CREATE ROOM. In the waiting room press READY UP; a match starts when at least two players are ready."),
                    new Card("target", "PRACTICE FIRST", "PRACTICE OFFLINE plays the same battle on this machine, against bots, with no account and no network. A good place to learn the maps and the vehicles."),
                }),
            new Tab("objectives", "OBJECTIVES", "flag",
                "Flags are the heart of every map: they are where you can spawn, and how many you hold multiplies every point your side scores.",
                new[]
                {
                    new Card("flag", "CAPTURING A FLAG", "Stand inside the flag's zone. The flag turns to your side faster the more of your soldiers stand in it, and stops while an enemy contests it. Neutral flags are quicker than enemy ones."),
                    new Card("star", "HOW POINTS ARE SCORED", "When a soldier dies, the other side scores one point for each flag it holds. Holding four flags makes every kill worth four. Points are shown across the top of the screen."),
                    new Card("handshake", "FRIENDLY FIRE IS ON", "Your bullets hurt your team. Killing a teammate scores the ENEMY side, exactly as if they had made the kill: check your target."),
                    new Card("skull", "RESPAWNING", "After a death the deploy screen opens. Three seconds later you can deploy again, at any flag your side holds."),
                }),
            new Tab(ControlsTab, "CONTROLS", "keyboard",
                "Your keys as they are bound right now. Change any of them in SETTINGS, CONTROLS; this page follows.",
                new Card[0]),
            new Tab("combat", "COMBAT", "crosshair",
                "Every gun drops its bullets over distance and every sight is zeroed. Aim a little high at long range and lead a moving target.",
                new[]
                {
                    new Card("rifles", "YOUR LOADOUT", "A primary weapon, a sidearm and up to three pieces of gear (grenades, launchers, tools), chosen on the deploy screen. Switch with {Weapon1} to {Weapon5} or the mouse wheel."),
                    new Card("scope", "AIMING AND SHOOTING", "{Fire} fires, {Aim} looks down the sights. Crouching ({Crouch}) and leaning ({LeanLeft} / {LeanRight}) steady the shot and show less of you. You cannot shoot while sprinting ({Sprint})."),
                    new Card("headshot", "HEADSHOTS", "A hit to the head does far more damage than one to the body. The scoreboard counts your headshots."),
                    new Card("ammo", "AMMO AND HEALTH", "Stand within five metres of an ammo cache or crate to refill your magazines, and of a medical station to heal. A field ammo bag does the same for the soldiers around it."),
                    new Card("boot", "FALLS AND WATER", "A drop of three metres is safe; from three to twenty the landing hurts more the faster you hit, and beyond that it kills. You can swim, but watch the breath bar under water."),
                    new Card("explosion", "EXPLOSIONS", "Grenades, rockets and burning wrecks hurt everyone nearby, more the closer they are. A vehicle that explodes kills the soldiers inside it."),
                }),
            new Tab("vehicles", "VEHICLES", "tank",
                "Tanks, jeeps, quad bikes, boats and helicopters wait at the bases and flags, for either side.",
                new[]
                {
                    new Card("jeep", "GETTING IN", "Look at a vehicle and press {Use}: the prompt says which seat you will take. Press {Use} again to get out. The driver steers with the movement keys; a gunner aims with the mouse and fires with {Fire}."),
                    new Card("tank", "TANKS AND GUNS", "A tank's driver drives; the gunner turns the turret with the mouse. Run enemies over for a roadkill, but mind your own team: friendly fire is on for vehicles too."),
                    new Card("helicopter", "FLYING", "{MoveForward} and {MoveBackward} climb and descend, the mouse banks and pitches. SETTINGS, VEHICLES chooses the mouse-roll or mouse-yaw scheme, or a joystick."),
                    new Card("bolt", "WRECKS AND RESPAWNS", "A destroyed or abandoned vehicle comes back at its own pad after a while. A burning wreck explodes: get out and away."),
                }),
            new Tab("deploy", "DEPLOY & MAP", "map",
                "Where you start, and how you find your way.",
                new[]
                {
                    new Card("map", "THE DEPLOY SCREEN", "Choose your weapons, then click a flag your side holds on the map to spawn there. Without a choice you deploy at a random flag your side holds. {HowToPlay} opens this guide from the deploy screen."),
                    new Card("eye", "THE MAP AND RADAR", "Hold {Map} for the full map; the mouse wheel zooms it. The radar in the corner shows your surroundings, your team and the flags."),
                    new Card("moon", "NIGHT MODE", "On a Night Mode map the battlefield is dark. Your night vision ({NightVision}) runs on a battery the room's host sized: it drains while on and recharges while off."),
                    new Card("star", "THE SCOREBOARD", "{Scoreboard} opens the scoreboard: both sides, every player and bot, kills, deaths and more. The mouse wheel or Page Up / Page Down turns its pages."),
                }),
            new Tab("rooms", "ROOMS & MODES", "people",
                "Every online match is played in a room a player created.",
                new[]
                {
                    new Card("sliders", "CREATING A ROOM", "Pick the map, the number of players (even, two to sixteen), how many bots each side gets, the rule (LEAD BY or FIRST TO) and its points, and a password if the room is private."),
                    new Card("people", "THE WAITING ROOM", "Choose your side with SWITCH SIDE, talk in the room chat, and press READY UP. The match starts when at least two players are ready. LEAVE ROOM goes back to the room list."),
                    new Card("target", "QUICK MATCH", "Joins the fullest open room that still has a place for you, so you get into a battle at once."),
                    new Card("medal", "RANKING AND ACHIEVEMENTS", "Every online match counts toward your career: the global ranking holds the best hundred, and fifty achievements wait to be earned, some of them secret."),
                }),
            new Tab("tips", "TIPS", "star",
                "What veterans wish they had known.",
                new[]
                {
                    new Card("flag", "PLAY THE FLAGS", "A side with more flags scores more for every kill: one capture can be worth more than five kills."),
                    new Card("shield", "STAY IN COVER", "Lean around corners instead of stepping out, and crouch behind walls. A sprinting soldier cannot shoot back."),
                    new Card("heart", "RESUPPLY OFTEN", "Pass an ammo cache on the way to the fight and a medical station on the way back."),
                    new Card("people", "MOVE TOGETHER", "Soldiers in a group capture faster and survive longer. Bots follow their squad; follow yours."),
                    new Card("tank", "VEHICLES ARE TEAM TOOLS", "A tank with a gunner is twice as deadly. Wait a second for a teammate before driving off."),
                    new Card("eye", "WATCH THE RADAR", "Red marks on the radar are enemies a teammate has seen. Use it before turning a corner."),
                }),
        };

        private static readonly Regex Placeholder = new Regex(@"\{(\w+)\}");

        /// <summary>Swaps each <c>{Action}</c> in <paramref name="text"/> for the player's key, highlighted.</summary>
        public static string Keys(string text)
            => Placeholder.Replace(text, match =>
                System.Enum.TryParse(match.Groups[1].Value, out GameAction action)
                    ? "<color=#FFB23F><b>" + GameKeys.Cap(action) + "</b></color>"
                    : match.Value);
    }
}
