using UnityEngine;
using UnityEngine.EventSystems;

namespace Ironfront.Net.Unity.Client.Hud
{
    /// <summary>
    /// Keeps the match's keys out of its menus while none is on screen: the EventSystem then
    /// neither navigates nor presses, and holds no selection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner's report of 2026-10-09: practice "crashed" or threw the player back to the menu,
    /// over and over.</b> Nothing crashed. The Esc menu and the deploy screen hide by switching their
    /// canvas off, the EventSystem kept DEPLOY selected after the click that closed the deploy
    /// screen, and its <c>StandaloneInputModule</c> turns the Horizontal/Vertical axes (WASD and the
    /// arrows) into navigation and Submit (Return, keypad Enter, Space) into a press, whether or not
    /// anything is drawn. Measured with Unity's own navigation in a practice match: from DEPLOY, A
    /// selects the hidden EXIT GAME and A, W the hidden QUIT TO MENU, so strafing left and jumping
    /// quit the game, and the player's log ends on <c>[AppQuit] quit requested</c> mid-match.
    /// </para>
    /// <para>
    /// <b>Before the EventSystem.</b> The execution order puts this ahead of the EventSystem's own
    /// <c>Update</c>, where the input module reads the keys, so the frame after a menu closes is
    /// already guarded.
    /// </para>
    /// <para>
    /// <b>A text field keeps its selection.</b> While the player is typing in the chat box
    /// (<see cref="LocalTextEntry.OwnsKeyboard"/>) the field stays selected: its typing arrives as
    /// update events, which the input module sends whatever <see cref="EventSystem.sendNavigationEvents"/>
    /// says, and the chat box clears the selection itself when it closes.
    /// </para>
    /// <para>
    /// The menus themselves make a hidden menu non-interactable (<c>MenuCanvas</c>), which covers
    /// the other half: the keys wandering from a menu on screen onto a hidden one.
    /// </para>
    /// </remarks>
    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(EventSystem))]
    public sealed class MatchMenuKeyGuard : MonoBehaviour
    {
        private EventSystem _events;

        private void Awake() => _events = GetComponent<EventSystem>();

        private void Update()
            => Apply(_events, NetClientBindings.IsMatchMenuShowing, LocalTextEntry.OwnsKeyboard);

        /// <summary>
        /// Lets <paramref name="events"/> steer menus only while <paramref name="menuShowing"/>, and
        /// drops its selection otherwise, unless a text field the player is typing into holds it.
        /// </summary>
        public static void Apply(EventSystem events, bool menuShowing, bool typing)
        {
            events.sendNavigationEvents = menuShowing;
            if (menuShowing || typing || events.currentSelectedGameObject == null) return;
            events.SetSelectedGameObject(null);
        }
    }
}
