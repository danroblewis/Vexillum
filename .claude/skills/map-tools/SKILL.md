---
name: map-tools
description: Inspect, extract, validate and build Vexillum .map level files without the .NET tools, using the vexillum-dev MCP server. Use when asked about maps, levels, collision images, spawn regions, or when a map fails to load.
---

# Map tools

Format reference: `docs/ARCHITECTURE.md` "Map file format" and "Level and
terrain". The MCP server implements the container in Python (`lzma`
`FORMAT_ALONE`), so nothing needs to be compiled.

* `map_info(path)` — magic check, compressed/uncompressed sizes, long name,
  the embedded file list with sizes and image dimensions, and the parsed
  `data.txt` regions. Use it first when a map "does not load".
* `extract_map(path, out_dir)` — writes `<name>_<file>` entries the way
  `MapExtractor` does, so the output folder is directly re-packable.
* `create_map(folder, long_name, out_path)` — packs a folder produced by
  `extract_map` (or authored by hand) into a `.map`, in the shipped file
  order. Produces a file the original `LevelLoader` accepts (verified against
  the C# decoder's expectations: props + 8-byte size + stream).

## Authoring rules a map must satisfy

* `main`, `background`, `collision`, `left`, `right`, `bottom` share the
  map's width/height; `sky` is a horizontally tiling strip; `left`/`right`
  are drawn beyond the map edges, `bottom` below it.
* `collision` must be 32-bit ARGB. Colour codes are in ARCHITECTURE.md.
  Any pixel that is not one of the special colours is destructible terrain
  with hardness from the red channel.
* `data.txt` needs `spawn_green`, `spawn_blue`, `flag_green`, `flag_blue`
  regions (Y-down bitmap coordinates). The server places flags at
  `(x1, height - y1 - 14)`.
* Keep maps under a few MB: the whole file is sent to clients that lack it,
  in 1020-byte chunks.

## When a map fails in the game

1. `map_info` on it. Wrong magic → not a map. LZMA error → truncated file.
2. Missing entry (`main`, `background`, `collision` are mandatory on the
   server; the client also needs `sky`, `left`, `right`, `bottom`) → the
   `LevelLoader` will throw `KeyNotFoundException` on `d.bitmaps[...]`.
3. Region parse errors log `Parse error on line N of data.txt`.
