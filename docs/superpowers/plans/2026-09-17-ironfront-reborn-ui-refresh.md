# Ironfront Reborn UI Refresh Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebrand the Unity client as Ironfront Reborn by Team 10 LTM and ship a polished, animated, keyboard-complete multiplayer menu.

**Architecture:** Keep `GameFlowState` and `MenuScreenController` as the only navigation state. Add one reusable keyboard navigator and one reusable panel transition, then generate and wire the complete menu through `BuildMenuCanvas`.

**Tech Stack:** Unity 6.3 (`6000.3.21f1`), C#, UnityEngine.UI, NUnit/Unity Test Framework, PowerShell, .NET 8.

**Spec:** `docs/superpowers/specs/2026-09-17-ironfront-reborn-ui-refresh-design.md`

## Global Constraints

- Product identity is exactly `Ironfront Reborn`; project credit is exactly `Team 10 LTM`.
- Player-facing inherited branding and links are removed; technical/history references remain where they explain compatibility.
- Do not add downloaded art, third-party fonts, tween packages, UI frameworks, protocol changes, or new flow states.
- Author the multiplayer scene subtree through `BuildMenuCanvas`, never by hand-written YAML.
- Keyboard behavior is Tab/Shift+Tab wrap, Enter primary action, Escape cancel/back, initial focus, and disabled-control skipping.
- Preserve password masking, password clearing, and duplicate-submit protection.

---

### Task 1: Pin and replace player-facing branding

**Files:**
- Create: `Ironfront_Reborn/Assets/Tests/EditMode/Client/BrandIdentityTests.cs`
- Create: `Ironfront_Reborn/Assets/Tests/EditMode/Client/BrandIdentityTests.cs.meta` (Unity-generated)
- Modify: `Ironfront_Reborn/ProjectSettings/ProjectSettings.asset:15-16,173-177`
- Modify: `Ironfront_Reborn/Assets/Scenes/Splash.unity:839,2357,2378`
- Modify: `Ironfront_Reborn/Assets/Scenes/Menu.unity:132810,135309,139499`
- Modify: `Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/MainMenu.cs:48-50,219`
- Modify: `docs/handing-over-a-build.md:106-116`

**Interfaces:**
- Consumes: committed Unity settings, splash/menu scenes, and legacy menu code.
- Produces: the `%USERPROFILE%/AppData/LocalLow/Team 10 LTM/Ironfront Reborn` identity and `BrandIdentityTests` regression contract.

- [ ] **Step 1: Write failing identity tests**

```csharp
#nullable enable
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Tests
{
    public sealed class BrandIdentityTests
    {
        [Test]
        public void PlayerSettingsUseNewIdentity()
        {
            Assert.AreEqual("Team 10 LTM", PlayerSettings.companyName);
            Assert.AreEqual("Ironfront Reborn", PlayerSettings.productName);
            Assert.AreEqual("com.team10ltm.ironfrontreborn",
                PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Standalone));
        }

        [TestCase("Assets/Scenes/Splash.unity", "IRONFRONT REBORN")]
        [TestCase("Assets/Scenes/Menu.unity", "IRONFRONT REBORN")]
        public void PlayerFacingScenesUseOnlyNewBrand(string path, string requiredTitle)
        {
            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            string text = string.Join("\n", scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Text>(true))
                .Select(label => label.text));
            StringAssert.Contains(requiredTitle, text);
            StringAssert.DoesNotContain("Ravenfield", text);
            StringAssert.DoesNotContain("SteelRaven7", text);
            StringAssert.DoesNotContain("Johan Hassel", text);
        }
    }
}
```

- [ ] **Step 2: Run focused tests and verify RED**

Run: `unity.exe -batchmode -nographics -quit -projectPath Ironfront_Reborn -runTests -testPlatform EditMode -testFilter Ironfront.Net.Unity.Client.Tests.BrandIdentityTests -testResults tmp/brand-red.xml -logFile tmp/brand-red.log`

Expected: failures name current Ravenfield/SteelRaven settings and scene copy.

- [ ] **Step 3: Apply exact identity values**

```yaml
companyName: Team 10 LTM
productName: Ironfront Reborn
Standalone: com.team10ltm.ironfrontreborn
```

Replace splash/menu text with `IRONFRONT REBORN`, `TEAM 10 LTM PRESENTS`, and `Ironfront Reborn — Team 10 LTM`. Remove obsolete vote/social URLs and update the build-handoff log path.

- [ ] **Step 4: Re-run focused tests and verify GREEN**

Run Step 2 with `brand-green.xml`/`brand-green.log`. Expected: all identity tests pass.

- [ ] **Step 5: Commit**

```powershell
git add Ironfront_Reborn/ProjectSettings/ProjectSettings.asset Ironfront_Reborn/Assets/Scenes/Splash.unity Ironfront_Reborn/Assets/Scenes/Menu.unity Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/MainMenu.cs Ironfront_Reborn/Assets/Tests/EditMode/Client/BrandIdentityTests.cs Ironfront_Reborn/Assets/Tests/EditMode/Client/BrandIdentityTests.cs.meta docs/handing-over-a-build.md
git commit -m "feat(client): rebrand game as Ironfront Reborn"
```

### Task 2: Add reusable keyboard navigation

**Files:**
- Create: `Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuKeyboardNavigator.cs`
- Create: `Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuKeyboardNavigator.cs.meta` (Unity-generated)
- Create: `Ironfront_Reborn/Assets/Tests/EditMode/Client/MenuKeyboardNavigatorTests.cs`
- Create: `Ironfront_Reborn/Assets/Tests/EditMode/Client/MenuKeyboardNavigatorTests.cs.meta` (Unity-generated)

**Interfaces:**
- Consumes: ordered `Selectable[]`, optional primary/cancel `Button`, and `EventSystem.current`.
- Produces: `Configure(Selectable[], Button?, Button?)`, `FocusFirst()`, `Move(bool)`, `Submit()`, and `Cancel()`.

- [ ] **Step 1: Write failing behavior tests**

Create real EventSystem/UI controls in setup. Pin forward wrapping with a disabled middle control, backward wrapping, initial focus, inactive-control skipping, disabled-primary no-op, multiline-input Enter no-op, and exactly-once submit/cancel:

```csharp
[Test]
public void MoveForwardWrapsAndSkipsDisabledControls()
{
    _second.interactable = false;
    _navigator.Configure(new Selectable[] { _first, _second, _third }, _submit, _cancel);
    EventSystem.current.SetSelectedGameObject(_third.gameObject);
    _navigator.Move(false);
    Assert.AreSame(_first.gameObject, EventSystem.current.currentSelectedGameObject);
}

[Test]
public void SubmitAndCancelInvokeLiveButtonsOnce()
{
    int submitCount = 0, cancelCount = 0;
    _submit.onClick.AddListener(() => submitCount++);
    _cancel.onClick.AddListener(() => cancelCount++);
    _navigator.Configure(new Selectable[] { _first }, _submit, _cancel);
    _navigator.Submit();
    _navigator.Cancel();
    Assert.AreEqual(1, submitCount);
    Assert.AreEqual(1, cancelCount);
}
```

- [ ] **Step 2: Run focused tests and verify RED**

Run: `unity.exe -batchmode -nographics -quit -projectPath Ironfront_Reborn -runTests -testPlatform EditMode -testFilter Ironfront.Net.Unity.Client.Tests.MenuKeyboardNavigatorTests -testResults tmp/nav-red.xml -logFile tmp/nav-red.log`

Expected: compile failure because `MenuKeyboardNavigator` does not exist.

- [ ] **Step 3: Implement minimal navigator**

```csharp
public sealed class MenuKeyboardNavigator : MonoBehaviour
{
    public void Configure(Selectable[] order, Button? primary, Button? cancel) { }
    public void FocusFirst() { }
    public void Move(bool backwards) { }
    public void Submit() { }
    public void Cancel() { }
}
```

Fill these methods with active/interactable filtering and wraparound. `OnEnable` focuses next frame; `Update` maps Tab/Shift+Tab, Return/KeypadEnter, and Escape. Enter leaves a selected multiline `InputField` alone.

- [ ] **Step 4: Re-run tests and verify GREEN**

Run Step 2 with `nav-green.xml`/`nav-green.log`. Expected: all navigator tests pass cleanly.

- [ ] **Step 5: Commit**

```powershell
git add Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuKeyboardNavigator.cs* Ironfront_Reborn/Assets/Tests/EditMode/Client/MenuKeyboardNavigatorTests.cs*
git commit -m "feat(client): add keyboard-complete menu navigation"
```

### Task 3: Add interruption-safe screen transitions

**Files:**
- Create: `Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuScreenTransition.cs`
- Create: `Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuScreenTransition.cs.meta` (Unity-generated)
- Create: `Ironfront_Reborn/Assets/Tests/EditMode/Client/MenuScreenTransitionTests.cs`
- Create: `Ironfront_Reborn/Assets/Tests/EditMode/Client/MenuScreenTransitionTests.cs.meta` (Unity-generated)
- Modify: `Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuScreenController.cs:688-695`

**Interfaces:**
- Consumes: panel `CanvasGroup`, `RectTransform`, and controller visibility decisions.
- Produces: `SetVisible(bool visible, bool immediate = false)` and `IsTargetVisible`.

- [ ] **Step 1: Write failing coroutine tests**

```csharp
[UnityTest]
public IEnumerator ShowingActivatesThenEnablesInput()
{
    _panel.SetActive(false);
    _transition.SetVisible(true);
    Assert.IsTrue(_panel.activeSelf);
    Assert.IsFalse(_group.interactable);
    yield return new WaitForSecondsRealtime(0.25f);
    Assert.AreEqual(1f, _group.alpha, 0.01f);
    Assert.IsTrue(_group.interactable);
}
```

Add hide safety and show→hide→show interruption tests; the latest target must win.

- [ ] **Step 2: Run focused tests and verify RED**

Run: `unity.exe -batchmode -nographics -quit -projectPath Ironfront_Reborn -runTests -testPlatform EditMode -testFilter Ironfront.Net.Unity.Client.Tests.MenuScreenTransitionTests -testResults tmp/transition-red.xml -logFile tmp/transition-red.log`

Expected: compile failure because `MenuScreenTransition` does not exist.

- [ ] **Step 3: Implement transition and controller seam**

Implement a 0.18-second unscaled fade plus 18-pixel settle. Stop the active coroutine before starting a new target. Hide disables input immediately and deactivates after alpha reaches zero; show activates immediately and enables input only after alpha reaches one.

```csharp
private static void SetActive(GameObject? screen, bool active)
{
    if (screen == null) return;
    MenuScreenTransition transition = screen.GetComponent<MenuScreenTransition>();
    if (transition != null) transition.SetVisible(active);
    else if (screen.activeSelf != active) screen.SetActive(active);
}
```

- [ ] **Step 4: Verify GREEN and flow regression**

Run Step 2 with green report names, then `dotnet test Ironfront.Client.Flow.Tests/Ironfront.Client.Flow.Tests.csproj -c Release`. Expected: all pass.

- [ ] **Step 5: Commit**

```powershell
git add Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuScreenTransition.cs* Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuScreenController.cs Ironfront_Reborn/Assets/Tests/EditMode/Client/MenuScreenTransitionTests.cs*
git commit -m "feat(client): animate menu screen transitions"
```

### Task 4: Rebuild the tactical menu and wire UX components

**Files:**
- Modify: `Ironfront_Reborn/Assets/Editor/NetVerification/BuildMenuCanvas.cs:54-877`
- Modify: `Ironfront_Reborn/Assets/Tests/EditMode/Client/BrandIdentityTests.cs`
- Modify (generated): `Ironfront_Reborn/Assets/Scenes/Menu.unity`

**Interfaces:**
- Consumes: navigator/transition components and existing `Menu*Screen` serialized fields.
- Produces: shared visual tokens/helpers and the regenerated `Multiplayer Menu` Canvas.

- [ ] **Step 1: Add failing scene authoring assertions**

```csharp
[Test]
public void BuilderAuthorsApprovedIdentityAndUxComponents()
{
    var scene = EditorSceneManager.OpenScene("Assets/Scenes/Menu.unity", OpenSceneMode.Single);
    GameObject root = scene.GetRootGameObjects().Single(go => go.name == "Multiplayer Menu");
    string text = string.Join("\n", root.GetComponentsInChildren<Text>(true).Select(label => label.text));
    StringAssert.Contains("IRONFRONT REBORN", text);
    StringAssert.Contains("TEAM 10 LTM", text);
    Assert.GreaterOrEqual(root.GetComponentsInChildren<MenuKeyboardNavigator>(true).Length, 8);
    Assert.GreaterOrEqual(root.GetComponentsInChildren<MenuScreenTransition>(true).Length, 8);
}
```

Add assertions over real scene objects: every button has distinct normal/highlighted/selected/disabled colours, every input has a visible sibling label, and the title/auth screens have a bounded raised card rather than controls parented directly to the full-screen panel.

- [ ] **Step 2: Run identity tests and verify RED**

Run Task 1 Step 2 using `menu-authoring-red.xml`. Expected: new builder assertions fail.

- [ ] **Step 3: Implement visual system and layouts**

Add named `CanvasInk`, `MutedInk`, `Surface`, `SurfaceRaised`, `Amber`, `Cyan`, and `Danger` colours. Add focused `BuildShell`, `BuildBrandRail`, `BuildCard`, `FieldWithLabel`, `ApplyButtonStyle`, and `WireNavigation` helpers. Use explicit field labels, consistent cards/spacing, and visible normal/highlighted/pressed/selected/disabled states.

Each screen adds `CanvasGroup` + `MenuScreenTransition` and wires its ordered controls, primary action, and local cancel:

```csharp
WireNavigation(panel, new Selectable[] { username, password, logIn, create }, logIn, null);
```

Keep room browsing wide and scan-friendly; use the same brand rail, card, status, and button language across title, auth, lobby, create-room, prompt, and room-lobby views.

- [ ] **Step 4: Regenerate the scene**

Run: `unity.exe -batchmode -nographics -quit -projectPath Ironfront_Reborn -executeMethod Ironfront.Net.Unity.EditorTools.BuildMenuCanvas.Run -logFile tmp/build-menu.log`

Expected: `build-menu-canvas.txt` ends with `saved: Assets/Scenes/Menu.unity` and Unity has no compile/serialization errors.

- [ ] **Step 5: Verify client EditMode GREEN**

Run: `unity.exe -batchmode -nographics -quit -projectPath Ironfront_Reborn -runTests -testPlatform EditMode -testFilter Ironfront.Net.Unity.Client.Tests -testResults tmp/menu-green.xml -logFile tmp/menu-green.log`

Expected: branding, navigation, transition, and existing client suites all pass.

- [ ] **Step 6: Commit**

```powershell
git add Ironfront_Reborn/Assets/Editor/NetVerification/BuildMenuCanvas.cs Ironfront_Reborn/Assets/Scenes/Menu.unity Ironfront_Reborn/Assets/Tests/EditMode/Client/BrandIdentityTests.cs
git commit -m "feat(client): redesign multiplayer menu UI"
```

### Task 5: Full verification and player build

**Files:**
- Verify: `Ironfront_Reborn/`
- Verify: `Ironfront.Client.Flow.Tests/`
- Verify: `Ironfront.sln`
- Modify only a previously listed file if a failing regression test proves a defect.

**Interfaces:**
- Consumes: tasks 1–4.
- Produces: XML/log evidence under ignored `tmp/` and a build at `build/windows/Ironfront.exe`.

- [ ] **Step 1: Check formatting and scoped branding**

```powershell
git diff --check
git grep -n -i -E "Ravenfield|SteelRaven7|Johan Hassel" -- Ironfront_Reborn/ProjectSettings Ironfront_Reborn/Assets/Scenes/Splash.unity Ironfront_Reborn/Assets/Scenes/Menu.unity Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/MainMenu.cs
```

Expected: both checks are silent.

- [ ] **Step 2: Run .NET regression suite**

Run: `dotnet test Ironfront.sln --no-restore -c Release`

Expected: all projects pass except any explicitly recorded clean-baseline environment failure.

- [ ] **Step 3: Run complete Unity EditMode suite**

Run: `unity.exe -batchmode -nographics -quit -projectPath Ironfront_Reborn -runTests -testPlatform EditMode -testResults tmp/ui-refresh-editmode.xml -logFile tmp/ui-refresh-editmode.log`

Expected: zero failed tests and no compiler errors.

- [ ] **Step 4: Build Windows player**

Run: `unity.exe -batchmode -nographics -quit -projectPath Ironfront_Reborn -executeMethod Ironfront.EditorBuildWindowsHarness.BuildWindowsPlayer -logFile tmp/ui-refresh-build.log`

Expected: successful `build/windows/Ironfront.exe` using new Player Settings.

- [ ] **Step 5: Walk through keyboard UX manually**

At 1920×1080, exercise title → login → register → login → lobby → rooms → create/private prompt → room lobby. Verify both Tab directions, selected-state visibility, Enter, Escape, busy-state duplicate prevention, and interrupted screen changes.

- [ ] **Step 6: Commit only proven corrections**

If verification exposes a defect, add a focused failing regression test, confirm RED, make the smallest correction, confirm GREEN, and commit that test with the correction.
