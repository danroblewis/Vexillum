---
name: xna-migration
description: API mapping from XNA 4.0 / Windows-only libraries to MonoGame 3.8 DesktopGL and cross-platform .NET, specific to the Vexillum codebase. Use when replacing System.Drawing, Nuclex, SlimDX, Steamworks, WinForms, GamerServices or Storage usages, or when a MonoGame API behaves differently from XNA.
---

# XNA → MonoGame migration map for Vexillum

Consult `docs/PORTING.md` for which step you are in and the hazard inventory.
Preservation rule (CLAUDE.md): the historical `.cs` files stay untouched
whenever a shim can make them compile. So the tables below are primarily a
**specification for the shims** (what each namespace/type must provide,
with what semantics), and only secondarily a list of edits. An edit to a
historical file is the last resort and carries a `// PORT:` comment.

## Namespaces (all kept in the original files; the shim provides them)

| Original `using` | Provided by |
|---|---|
| `Microsoft.Xna.Framework.GamerServices`, `.Storage`, `.Net` | `Shims/XnaCompat`: empty namespaces (one internal placeholder type each so the compiler accepts the `using`) |
| `Microsoft.Xna.Framework.Media` | MonoGame itself |
| `Nuclex.Input`, `Nuclex.UserInterface*`, `Nuclex.Support` | `Shims/Nuclex`: source port |
| `Steamworks` | `Shims/Steamworks`: offline implementation with Steamworks.NET names |
| `System.Drawing`, `System.Drawing.Imaging` | `Shims/System.Drawing`: managed implementation (never the real Windows-only package) |
| `System.Windows.Forms` | `Shims/System.Windows.Forms`: `MessageBox`, `KeysConverter`, `IMessageFilter`, `Message`, `Application` |
| `MiscUtil.*` | vendored source, unchanged |
| `SevenZip.Compression.LZMA` | vendored `Lzma` project, unchanged |

## Shim specifications (members the game actually uses)

| Type | Members used by the game | Semantics the shim must reproduce |
|---|---|---|
| `System.Drawing.Bitmap` | `Bitmap(string path)`, `Bitmap(Stream)`, `Bitmap(int w, int h)`, `Width`, `Height`, `PixelFormat`, `GetPixel`, `SetPixel`, `MakeTransparent(Color)`, `Clone()`, `Save(Stream, ImageFormat)`, `LockBits(Rectangle, ImageLockMode, PixelFormat)`, `UnlockBits` | decode JPEG/PNG with `StbImageSharp`; store 32bpp; `LockBits` returns `BitmapData` with `Scan0` (pinned via `GCHandle`), `Stride = width*4`, `Height`, in **BGRA byte order** because `Level` reads `ptr+2` as red, `ptr+1` green, `ptr+3` alpha, `ptr` blue; `MakeTransparent(c)` sets alpha 0 on every pixel equal to `c` (GDI+ compares RGB only for opaque targets); `Save` PNG non-premultiplied so `Texture2D.FromStream` yields the same texels as before |
| `System.Drawing.Color` | `.R .G .B .A`, `Color.Red`, `Color.White`, `Color.Transparent`, `Color.FromArgb(r,g,b)`, equality | plain struct; `Transparent` = (0,0,0,0) is what `GraphicsUtil.transparent` relies on |
| `System.Drawing.Rectangle` | ctor(x,y,w,h), `X Y Width Height` | used by `LockBits` and `ScrollPanel.localRect` |
| `System.Drawing.Brush`, `SolidBrush`, `Brushes.White`, `Graphics.DrawString`, `Font`, `PointF` | referenced by the dead `TextRenderer` overload and `brushes` array | stubs are enough; `DrawString` may be a no-op, nothing calls it |
| `System.Drawing.Imaging.*` | `BitmapData`, `ImageLockMode`, `ImageFormat.Png/Jpeg`, `PixelFormat`, `ImageCodecInfo.GetImageEncoders`, `FormatID` | `GetImageEncoders` returns an empty array (`MapCreator.GetJpgEncoder` is never called) |
| `System.Windows.Forms.MessageBox` | `Show(string)`, `Show(string, string)` | log through `Util.Debug`, print to stderr, return |
| `System.Windows.Forms.KeysConverter` | ctor only | empty class |
| `System.Windows.Forms.IMessageFilter`, `Message` (`Msg`, `WParam`), `Application.AddMessageFilter` | `KeyboardMessageFilter` | `AddMessageFilter` is a no-op; `TranslateMessage` P/Invoke stays declared but is never reached |
| `Nuclex.Input.InputManager` | `InputManager(GameServiceContainer, IntPtr)`, `GetKeyboard()` (`KeyPressed`, `KeyReleased`, `CharacterEntered` events), `GetMouse()` (`MouseButtonPressed/Released`, `MouseMoved`, `MouseWheelRotated`) as a `GameComponent` | source port; keyboard from `Keyboard.GetState()` diffing, characters from `GameWindow.TextInput`; mouse events in window pixels; the `pressedKeys` `isNew` logic in `Vexillum.KeyPressed` depends on one `KeyPressed` per physical press |
| `Nuclex.Input.MouseButtons` | `Left, Middle, Right` | `Util.GetMouseButtonInt` maps 0/1/2 |
| `Nuclex.UserInterface.*` | `GuiManager` (`Screen`, `Visualizer`, `DrawOrder`), `Screen` (`Width`, `Height`, `Desktop`, `FocusedControl`, `IsInputCaptured`, `IsMouseOverGui`), `UniRectangle/UniVector/UniScalar`, controls: `WindowControl` (`Title`, `Close`, `BringToFront`, `Bounds`, `Children`), `ButtonControl` (`Text`, `Pressed`, `ShortcutButton`), `LabelControl`, `InputControl` (`Text`, `OnCharacterEntered`, `OnKeyPressed`, `HasFocus`), `ListControl`, `OptionControl`, `ChoiceControl`, `Control` (`Bounds`, `GetAbsoluteBounds`, `OnMouse*`), `IWritable`, `IFocusable`, `IOpeningLocator`, `FlatGuiVisualizer.FromFile`, `RendererRepository.AddAssembly`, `IFlatControlRenderer<T>`, `IFlatGuiGraphics`, `RectangleF` | source port keeps all of this; `CustomInputControl.cs` overrides `OnKeyPressed` as `override` in the author's original (the 2025 commit changed it to `new` because the stub had no virtual; revert once the port exists) |
| `Steamworks.*` | `CSteamID(ulong)` + `.m_SteamID`, `HAuthTicket` + `.Invalid`, `AppId_t` (explicit cast from int), `Callback<T>` + `.Create` + `DispatchDelegate`, `GameOverlayActivated_t.m_bActive`, `ValidateAuthTicketResponse_t` (`m_SteamID`, `m_eAuthSessionResponse`), `EAuthSessionResponse.k_EAuthSessionResponseOK`, `EBeginAuthSessionResult.k_EBeginAuthSessionResultOK`, `SteamAPI.Init/Shutdown/RunCallbacks/RestartAppIfNecessary`, `SteamUser.GetSteamID/GetAuthSessionTicket/CancelAuthTicket/BeginAuthSession`, `SteamFriends.GetPersonaName`, `SteamUtils.SetWarningMessageHook`, `SteamAPIWarningMessageHook_t`, `Packsize.Test()`, `DllCheck.Test()` | offline: `Init()` returns true so the client does not exit at startup, `GetPersonaName()` returns the OS user name, tickets are empty (`length 0`), `BeginAuthSession` returns OK |
| `Microsoft.Xna.Framework.Graphics.Effect` for `"Blur"` | `Parameters["d"].SetValue(Vector2)`, `CurrentTechnique.Passes[0].Apply()` | see `content-pipeline` skill; content name `Blur` must resolve |

## MonoGame behavioural differences to watch

* `Texture2D.FromStream` in MonoGame does not premultiply by default in 3.8
  (it did in some 3.x versions). We stop using it anyway.
* `RenderTargetUsage.PreserveContents` is honoured by DesktopGL; set it in
  `PreparingDeviceSettings` exactly as now, and also pass it when creating
  `RenderTarget2D`s (`GameView.CloneRenderTarget`, `ScrollPanel`) or the
  target content is discarded on `SetRenderTarget`.
* `GraphicsDevice.SetRenderTargets(null)` / `GetRenderTargets()` exist.
* `Effect` content must be MGFX; XNA `Blur.xnb` will throw on load. See the
  `content-pipeline` skill.
* `SoundEffectInstance.Apply3D` exists; `SoundEffect.DistanceScale` exists.
* `SpriteFont` `.xnb` from XNA 4.0 loads (MonoGame reads XNB version 5).
  Texture and SoundEffect XNBs also load. Only Effect does not.
* `Game.Window.Handle` exists but is an SDL handle; nothing should need it.
* `GamePad` and `Keys` enums are equivalent; `Keys.OemPeriod` exists.
* `Content.RootDirectory = "Content"` is relative to the current directory,
  which is why the runtime directory matters. Use `--root` to `chdir`.
* Fixed timestep: `TargetElapsedTime = 16 ms` gives 62.5 Hz, same as XNA did
  (the original code computes `(int)(1/60f*1000) = 16`). Keep 16, not
  `1/60`.

## Per-file notes

* `Vexillum.cs`: remove `InputManager`/`GuiManager` components; keep
  `Components` order irrelevant. `LoadContent` order stays (fonts before views).
* `Util.cs`: `HttpGet/HttpPost` → `HttpClient` with 10 s timeout, still
  synchronous (`.GetAwaiter().GetResult()`), same return conventions ("" on
  failure). `loadBitmap`/`loadTexture` move to the wrapper.
* `Level.cs`: keep `MainBitmap`/`BackgroundBitmap` fields but as the wrapper
  type; `ClientLevel.Destroy` and `ApplyTerrainState` call `GetPixel` on them.
* `LevelLoader.cs`: `new Bitmap(stream)` → `PixelImage.Load(stream)`;
  `MakeTransparent` stays.
* `ServerLevel.cs`/`MenuLevel.cs`: constructor parameter types only.
* `Program.cs` (client): parse `--root`, `--connect`; `Directory.SetCurrentDirectory(root)`.
* `Server/Program.cs`: parse `--root`, `--port`; write default `settings.txt`
  (text from `HostServerForm.defaultConfig`) when missing; keep
  `Console.CancelKeyPress` handling.
