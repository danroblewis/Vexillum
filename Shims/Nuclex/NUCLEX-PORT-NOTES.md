# Nuclex Framework source port for the Vexillum MonoGame build

`Shims/Nuclex` is a source port of the three Nuclex Framework assemblies the
game links against (`Nuclex.Support`, `Nuclex.Input`, `Nuclex.UserInterface`),
compiled as one assembly (`Vexillum.Shims.Nuclex`) against MonoGame 3.8.4
DesktopGL. License: IBM Common Public License 1.0, see `LICENSE-CPL.txt`.

## Provenance

* Sources: Nuclex Framework **r1404** (the last public SVN revision, XNA 4.0
  era), from the GitHub mirror `remiomosowon/NuclexFramework`
  (`nuclex-framework-r1404-sources/`). Every copied file keeps its CPL header.
* The shipped `dlls/Nuclex.*.dll` were decompiled (ilspycmd) only to compare
  the public API and behaviour with r1404 (they match, with one addition
  noted below) and to extract the embedded "Suave" default skin, which the
  mirror does not contain in built form.
* Original layout `Source/...` is kept under `UserInterface/`, `Input/` and
  `Support/`. Unit tests (`*.Test.cs`, NUnit/NMock) are not included.

## What is included

* **UserInterface**: everything (`GuiManager`, `Screen`, all controls,
  `UniScalar/UniVector/UniRectangle`, `RectangleF`, `DefaultInputCapturer`,
  `FlatGuiVisualizer`, `FlatGuiGraphics` with XSD skin validation, all Flat
  renderers, `OpeningLocator`) except `Source/Input/MouseButtons.cs`, which
  the original project did not compile either (it duplicates
  `Nuclex.Input.MouseButtons`).
* **Input**: `InputManager`, `IInputService`, `MouseButtons`,
  `ExtendedPlayerIndex`, the device interfaces, `BufferedKeyboard/Mouse`,
  `XnaKeyboard` (chat pads), `XnaGamePad`/`GamePad`/`ExtendedGamePadState`,
  the `No*` placeholder devices, and the `Mocked*` devices plus
  `MockInputManager` (used by `Tests/Vexillum.Tests/NuclexShimTests.cs`).
* **Support**: only what the above needs: `Plugins/` (`PluginRepository`,
  `PluginHost`, `Employer`, `PluginHelper`, `IAssemblyLoader`,
  `NoPluginAttribute`, `AssemblyLoadEventArgs`), `Collections/`
  (`ObservableCollection`, `ItemEventArgs`, `IObservableCollection`),
  `WeakReference`, `FloatHelper`, `EnumHelper`, `XmlHelper`.

## What is dropped

* DirectInput/SlimDX: `DirectInputManager`, `DirectInputConverter.*`,
  `DirectInputGamePad`, `ControllerDetector`, `ControllerEventArgs`.
* Win32 window-message input: `WindowMessageFilter`, `UnsafeNativeMethods`,
  `WindowMessageKeyboard`, `WindowMessageMouse`, `IKeyboardMessageSource`,
  `IMouseMessageSource`.
* Xbox/Phone touch specifics: `XnaTouchPanel`, `MockedTouchPanel`,
  `TouchCollectionHelper` (reflection into XNA's `TouchCollection`).
* Everything else in `Nuclex.Support` (threading, scheduling, licensing,
  parsing, the other collections, ...).

## Changes to the library (complete list)

New files (not from Nuclex; CPL like the rest):

* `Input/Devices/MonoGameKeyboard.cs` - PC keyboard. `Keyboard.GetState()` is
  diffed once per `Update()`: one `KeyPressed` per physical press and one
  `KeyReleased` per release, no auto-repeat. Characters come from
  `GameWindow.TextInput` (typed text plus Backspace/Tab/Return control
  characters, as `WM_CHAR` delivered them). Without a window, characters are
  synthesized from key presses with the chat pad character map.
* `Input/Devices/MonoGameMouse.cs` - PC mouse. `Mouse.GetState()` is diffed
  once per `Update()`: `MouseMoved(x, y)` in window pixels, one
  `MouseButtonPressed/Released` per button change, `MouseWheelRotated` in
  `ScrollWheelValue delta / 120` (one notch = 1.0, as before). A cursor
  leaving the client area is reported once as `MouseMoved(-1, -1)`, matching
  the old `WM_MOUSELEAVE` handling that `Vexillum.MouseMove` filters on.
* `Input/GameWindowLocator.cs` - finds the `GameWindow` for the window handle
  `InputManager` receives (service container first, then MonoGame's internal
  `Game.Instance` by reflection).
* `UserInterface/Resources/EmbeddedByteResourceManager.cs` - serves the
  embedded Suave skin files as `byte[]` resources so
  `FlatGuiVisualizer.FromResource` / `ResourceContentManager` work without the
  Windows ResX tooling.
* `UserInterface/Resources/Skins/Suave/*` - the default skin (skin XML and
  the three XNA 4.0 `.xnb` files), extracted from the shipped
  `Nuclex.UserInterface.dll` resources. `UserInterface/Resources/skin.xsd` is
  the schema from r1404, embedded under its original resource name.

Edited files:

* `Input/InputManager.cs` - rewritten constructor/setup: no DirectInput, no
  `WindowMessageFilter`; the PC keyboard and mouse are the MonoGame devices
  above. Public API, device indices (`GetKeyboard()` = index 4,
  `GetMouse()` = index 0), snapshot system and `IGameComponent` plumbing
  unchanged; the `InputManager(GameServiceContainer, IntPtr)` overload
  Vexillum calls still exists.
* `Input/Devices/KeyboardStateHelper.cs` - rewritten on the public
  `KeyboardState` API (XNA's private `AddPressedKey/RemovePressedKey` do not
  exist in MonoGame). Same two delegates, same semantics.
* `Input/Devices/BufferedKeyboard.cs`, `BufferedMouse.cs` - `Update()` made
  `virtual` so the MonoGame devices can poll before draining their queues.
* `Input/Devices/XnaKeyboard.CharacterMap.cs` - `characterMap` made
  `internal` (shared with `MonoGameKeyboard`'s no-window fallback).
* `Input/MockInputManager.cs` - touch panels are `NoTouchPanel`
  (`ReadOnlyCollection<ITouchPanel>` / `ITouchPanel GetTouchPanel()`) instead
  of `MockedTouchPanel`.
* `UserInterface/Screen.cs` - `WeakReference<Control>` qualified as
  `Nuclex.Support.WeakReference<Control>` (ambiguous with
  `System.WeakReference<T>` since .NET 4.5).
* `UserInterface/GuiManager.cs` - `Initialize()` wraps the default-skin
  `FromResource` call in try/catch (logged to `Trace`, visualizer stays null,
  `Draw()` already tolerates that). Only matters if MonoGame ever refuses the
  embedded XNA `.xnb` files; Vexillum assigns its own visualizer in
  `LoadContent()` either way.
* `UserInterface/Resources/SuaveSkinResources.Designer.cs` - creates an
  `EmbeddedByteResourceManager` instead of a `.resources`-backed
  `ResourceManager`.
* `Support/Plugins/PluginHost.cs` - `employAssemblyTypes` catches
  `ReflectionTypeLoadException` and employs the types that did load (the
  game assembly is scanned for renderers at `LoadContent`).
* `Nuclex.csproj` defines `TRACE;WINDOWS;NO_DIRECTINPUT`: `WINDOWS` is the
  XNA "PC" platform constant the original x86 projects defined (it selects the
  desktop code paths, it is not an OS switch), `NO_DIRECTINPUT` is the
  library's own switch that removes the SlimDX constructor from
  `ExtendedGamePadState.Builders.cs` without editing it.

Everything else is byte-for-byte r1404 apart from BOM/line-ending
normalization by the copy.

## Differences from the shipped DLLs worth knowing

* The shipped `Nuclex.UserInterface.dll` and r1404 have the same public API
  and the same control behaviour (verified by diffing decompiled bodies of
  `Screen`, `Control`, `InputControl`, `ListControl`, `WindowControl`,
  `PressableControl`, `FlatGuiGraphics`, `GuiManager`, `FlatGuiVisualizer`,
  `DefaultInputCapturer`).
* Keyboard auto-repeat: Windows sent repeated `WM_KEYDOWN` for a held key, so
  the original fired repeated `KeyPressed` events (the game's `pressedKeys`
  / `isNew` logic and `Screen`'s `repetition` flag exist for that). The
  MonoGame keyboard fires one `KeyPressed` per physical press. Consequence:
  holding Backspace in a text field deletes one character, holding a letter
  still repeats (SDL repeats `TextInput`). TODO in `docs/PORTING.md` if the
  original feel is wanted back (synthesize repeats after ~500 ms at ~30 Hz).
* The mouse is polled, so events are generated at `Update()` time (60 Hz
  fixed step) rather than per window message; multiple moves inside one
  frame collapse into one `MouseMoved`.

## Skin loading path (unchanged)

`FlatGuiVisualizer.FromFile(services, "Content/ui/DarknessUI.xml")` creates a
`ContentManager` rooted at `Content/ui`, validates the XML against the
embedded `skin.xsd`, then loads `ui/Darkness` (`Content.Load<Texture2D>`)
and `ui/DefaultFont` / `ui/TitleFont` (`Content.Load<SpriteFont>`) exactly as
the XNA build did. `FlatGuiGraphics` uses `SpriteBatch.Begin(Deferred,
AlphaBlend, null, null, rasterizerState{ScissorTestEnable})` and
`GraphicsDevice.ScissorRectangle` for clip regions; both exist unchanged in
MonoGame, so no drawing code was touched.
