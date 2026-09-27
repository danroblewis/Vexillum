---
name: content-pipeline
description: Handle Vexillum game content for MonoGame without Wine: which .xnb files still load, why Blur.fx cannot be recompiled here and how the blur is reproduced instead, sprite fonts, the Nuclex GUI skin, and MGCB for new assets. Use when content fails to load, when touching ZombieSurvivalContent/, or when adding assets.
---

# Content pipeline

Platform policy (CLAUDE.md): no Wine, Proton or Rosetta. That rules out
MonoGame's `mgfxc` effect compiler on macOS/Linux, and it is the only piece
of the content toolchain that needs it.

## What exists

* `Test/Content/*.xnb` — XNA 4.0 (XNB version 5, platform `w`) files:
  13 `SoundEffectReader`, 4 `SpriteFontReader`, 1 `EffectReader` (`Blur`),
  plus `Content/ui/Darkness.xnb` (texture), `ui/DefaultFont.xnb`,
  `ui/TitleFont.xnb`. `xnb_info(path)` (MCP) prints platform, version,
  flags, size and the reader list.
* Loose `Content/*.png|jpg` and `Content/ui/*.png` are loaded by the game's
  own `System.Drawing.Bitmap` path (shimmed), not by the content manager.
* Sources in `ZombieSurvivalContent/`: `.wav`, `.spritefont` (font
  "Righteous" and others, may not be installed here), `*-exenfont*` files
  (an "ExEn" font shim the author used for Mono experiments; ignore),
  `Blur.fx`, `ui/DarknessUI.xml` + `ui/Darkness.png` (Nuclex skin).

## What loads in MonoGame 3.8 without rebuilding

SoundEffect, SpriteFont and Texture2D XNBs from XNA 4.0 load (MonoGame reads
XNB version 5). **Effect does not**: XNA compiled DirectX 9 bytecode;
MonoGame needs MGFX. Everything except `Blur.xnb` stays as is (invariant A6).

## The blur without a shader

`Blur.fx` is:

```
out.rgb = (tex(uv) + tex(uv + d)) / 2 ;  out.a = tex(uv).a
```

with `d` = `Level.cameraShake` in texture units. `GameView.DrawStuff` draws
the full-screen render target once through this effect. The same pixels
come from two ordinary sprite draws of the render target:

1. draw at (0,0) with colour `White * 0.5`
2. draw at `(-d.X * width, -d.Y * height)` with colour `White * 0.5`,
   `BlendState.Additive`

The level render target is fully opaque (sky + main + borders cover the
window), so alpha is 1 in both formulations.

**What is implemented (step 8, 2026-09-27):** the real shader, no edit to
`GameView.cs`. On DesktopGL an MGFX effect stores GLSL *source*, so
`Shims/XnaCompat/Content/XnaEffectContent.cs` assembles an MGFX v10 blob in
code (`BlurMgfx`) with `Blur.fx` transcribed to GLSL, and registers a
content reader for the XNA `EffectReader` type string
(`ContentTypeReaderManager.AddTypeCreator`, which is consulted before type
resolution) that skips the DX9 bytes of the shipped `Blur.xnb` and returns
that effect. `PortProgram` calls `XnaEffectContent.Register()` before the
author's `Main`. The GLSL must keep the names of MonoGame's SpriteEffect
vertex shader (`vTexCoord0`, `vFrontColor`, `ps_s0`, `ps_uniforms_vec4[]`)
because `DrawStuff` applies the pass inside an Immediate-mode batch.

**Fallback** (only if a driver rejects the GLSL): the two draws above in a
`// PORT:`-marked spot in `DrawStuff`, but note that on MonoGame
`Color.White * 0.5f` also halves alpha and `BlendState.Additive` multiplies
by source alpha (0.25x total). Correct form inside the existing
NonPremultiplied batch: draw at (0,0) with `Color.White`, then at the offset
with `new Color(255, 255, 255, 128)`. Keep the content name `Blur`. Record
changes in `docs/PORTING.md` step 8.

Do **not** install `dotnet-mgfxc`, do not run `mgfxc_wine_setup.sh`, and do
not commit an `.xnb` compiled on a Windows machine unless the owner asks:
a checked-in artifact nobody can rebuild here is against the platform policy.

## MGCB (when adding new content)

`dotnet tool install -g dotnet-mgcb` works natively on arm64 for textures,
sounds and fonts (no Wine involved; only effects need it). Create
`Content/Content.mgcb` listing new wavs/spritefonts. Font sources reference
fonts by name; if "Righteous" is missing, keep using the prebuilt `.xnb`
(the glyph metrics define the UI layout, invariant A6) rather than rebuilding
with a substitute font.

## GUI skin

`ui/DarknessUI.xml` is a Nuclex `FlatGuiVisualizer` skin referencing
`Darkness.xnb` texture regions and `DefaultFont`/`TitleFont` under
`Content/ui/`. With the Nuclex source port (PORTING.md step 6) it loads
unchanged; `FlatGuiVisualizer.FromFile` reads the XML with `System.Xml`,
which is cross-platform.
