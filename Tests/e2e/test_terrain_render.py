"""Terrain rendering on the client: Destroy repaints the destroyed pixel of the main
texture and a late joiner repaints the craters it receives in packet 3 (PHYS-43, PHYS-44)."""

import pytest

from conftest import wait_until
from clientlib import lone_client, lone_server, mod_runtime, start_client, terrain_bits, terrain_hash  # noqa: F401

# terrain_reference for Test/Maps/bases.map (docs/TESTING.md "Reference hashes")
PRISTINE_BASES_SHA256 = "059ee417b1b64d17f7ebe439dd8bf5b05f01177866873db6dbcd4947716987e3"

XNA = "Microsoft.Xna.Framework"
CS_LEVEL = "var v = (GameView)Game.View; var L = (ClientLevel)v.Level; var t = L.terrain; int W = (int)L.Size.X, H = (int)L.Size.Y; "
# one texel of the main texture as r:g:b:a (texture row = H - 1 - world y)
CS_TEXEL = ("Func<int, int, string> texel = (x, y) => { var one = new %s.Color[1]; "
            "L.MainTexture.GetData<%s.Color>(0, new %s.Rectangle(x, H - y - 1, 1, 1), one, 0, 1); "
            "return one[0].R + \":\" + one[0].G + \":\" + one[0].B + \":\" + one[0].A; }; " % (XNA, XNA, XNA))


def _rgba(text: str):
    return tuple(int(v) for v in text.split(":"))


# PHYS-43
@pytest.mark.e2e
def test_client_destroy_repaints_the_pixel_with_the_background_or_transparent_where_green_was_128(lone_server, lone_client):
    c = lone_client
    # pick, destroy and read back inside one game-thread call so nothing else touches the terrain in between:
    # A = a destructible pixel whose collision green channel was 128 (GetTransparent), B = one that was not
    # and whose main colour differs from the background colour underneath
    r = c.ev('Sync(() => { ' + CS_LEVEL + CS_TEXEL +
             'int ax = -1, ay = -1, bx = -1, by = -1; '
             'for (int x = 0; x < W && (ax < 0 || bx < 0); x++) for (int y = 0; y < H && (ax < 0 || bx < 0); y++) { '
             '  if (!t.GetTerrain(x, y) || t.GetCollisionData(x, y) == 1) continue; '
             '  var mn = L.MainBitmap.GetPixel(x, H - y - 1); if (mn.A != 255) continue; '
             '  if (t.GetTransparent(x, y)) { if (ax < 0) { ax = x; ay = y; } } '
             '  else if (bx < 0) { var bg = L.BackgroundBitmap.GetPixel(x, H - y - 1); if (mn.R != bg.R || mn.G != bg.G || mn.B != bg.B) { bx = x; by = y; } } } '
             'if (ax < 0 || bx < 0) return "none"; '
             'var bgB = L.BackgroundBitmap.GetPixel(bx, H - by - 1); var bgA = L.BackgroundBitmap.GetPixel(ax, H - ay - 1); '
             'string beforeA = texel(ax, ay), beforeB = texel(bx, by); '
             'bool ra = L.Destroy(ax, ay), rb = L.Destroy(bx, by); bool again = L.Destroy(bx, by); '
             'string afterA = texel(ax, ay), afterB = texel(bx, by); '
             'return ax + "," + ay + "," + bx + "," + by + "|" + beforeA + "|" + afterA + "|" + beforeB + "|" + afterB '
             '  + "|" + bgB.R + ":" + bgB.G + ":" + bgB.B + "|" + bgA.R + ":" + bgA.G + ":" + bgA.B + "|" + ra + "," + rb + "," + again '
             '  + "|" + t.GetTerrain(ax, ay) + "," + t.GetTerrain(bx, by) + "," + t.GetCollisionData(ax, ay) + "," + t.GetCollisionData(bx, by); })', 60)
    assert r != "none", "bases has no destructible pixel with green == 128 (48876 expected) or none with a main colour unlike the background"
    coords, before_a, after_a, before_b, after_b, bg_b, bg_a, results, bits = r.split("|")
    ax, ay, bx, by = (int(v) for v in coords.split(","))
    assert results == "True,True,False"                       # Destroy flips a solid pixel once, then refuses
    solid_a, solid_b, nib_a, nib_b = bits.split(",")
    assert (solid_a, solid_b) == ("False", "False")
    assert int(nib_a) not in (0, 1) and int(nib_b) not in (0, 1)   # both were destructible terrain

    # the main texture held the (opaque) terrain pixel before
    assert _rgba(before_a)[3] == 255 and _rgba(before_b)[3] == 255
    # A: green == 128 -> Color.Transparent
    assert _rgba(after_a) == (0, 0, 0, 0)
    assert _rgba(after_a) != _rgba(before_a)
    # B: the background image colour at that coordinate, opaque
    assert _rgba(after_b) == tuple(int(v) for v in bg_b.split(":")) + (255,)
    assert _rgba(after_b) != _rgba(before_b)
    # A was not painted with its background colour, so the two branches are distinguishable
    assert _rgba(after_a)[:3] != tuple(int(v) for v in bg_a.split(":")) or _rgba(after_a)[3] == 0

    # the client-side Destroy touched only this client: the server still has both pixels
    assert terrain_bits(lone_server, ax, ay, 1, 1) == "1"
    assert terrain_bits(lone_server, bx, by, 1, 1) == "1"


# PHYS-44
@pytest.mark.e2e
def test_client_joining_after_an_explosion_renders_the_crater_from_the_terrain_state(lone_server, lone_client, mod_runtime):
    c1 = lone_client
    # open destructible ground on the server: the top solid pixel of a column is not Solid and the
    # 60x60 box around it holds plenty of destructible terrain for a radius-26 crater
    spot = lone_server.ev('Sync(() => { var t = ((global::Server.Server)Server).level.terrain; var lv = ((global::Server.Server)Server).level; int W = (int)lv.Size.X, H = (int)lv.Size.Y; '
                          'for (int x = 400; x < W - 400; x += 16) { int top = -1; for (int y = H - 1; y >= 0; y--) if (t.GetTerrain(x, y)) { top = y; break; } '
                          'if (top < 40 || top > H - 80 || t.GetCollisionData(x, top) == 1) continue; int n = 0; '
                          'for (int dx = -30; dx <= 30; dx++) for (int dy = -30; dy <= 30; dy++) if (t.GetTerrain(x + dx, top + dy) && t.GetCollisionData(x + dx, top + dy) != 1) n++; '
                          'if (n > 800) return x + "," + top; } return "none"; })', 30)
    assert spot != "none", "no open destructible ground on this map"
    sx, sy = (int(v) for v in spot.split(","))
    strip = (sx - 40, sy - 40, 80, 80)
    strip_before = terrain_bits(c1, *strip)
    assert strip_before == terrain_bits(lone_server, *strip)
    h0 = terrain_hash(lone_server)

    # the same path a rocket takes: ServerLevel.Explode schedules the crater two frames ahead and
    # broadcasts packet 15, so the first client keeps in step with the server
    lone_server.ev(f'Sync(() => {{ Server.level.Explode({sx}, {sy}, 26, 4242, false, (Player)null, (Weapon)null); return "ok"; }})')
    h_server = wait_until(lambda: h if (h := terrain_hash(lone_server)) != h0 else None, 5, message="the server terrain did not change")
    assert h_server != PRISTINE_BASES_SHA256
    strip_server = terrain_bits(lone_server, *strip)
    assert strip_server.count("1") < strip_before.count("1")   # the crater is inside the strip
    # (the first client's whole-map hash can differ from the server's: the Destroy test above cratered
    # two of its pixels locally, so it is compared on the strip only)
    wait_until(lambda: terrain_bits(c1, *strip) == strip_server, 5, message="the first client did not apply the explosion")

    # a second human on a `maxbots 1` server would make UpdateBots hang (docs/TESTING.md): raise the
    # cap to the number of connections first so it stays a no-op
    lone_server.ev('Sync(() => { Server.maxBots = 2; return "ok"; })')
    c2 = start_client(mod_runtime, lone_server)
    try:
        assert c2.wait_log(r"Set terrain state \(\d+ bytes\)", 30)
        # the terrain state of packet 3 is the server's current one, craters included
        wait_until(lambda: terrain_hash(c2) == terrain_hash(lone_server), 20, message="the late joiner's terrain never matched the server's")
        assert terrain_hash(c2) != PRISTINE_BASES_SHA256
        assert terrain_bits(c2, *strip) == strip_server
        assert terrain_bits(c2, *strip) != strip_before

        # ApplyTerrainState (run from packet 9) repainted every destroyed pixel of the strip: transparent where
        # the collision green channel was 128, the background colour elsewhere; untouched solid pixels still
        # show the main image
        x0, y0, w, h = strip
        r = c2.ev('Sync(() => { ' + CS_LEVEL +
                  f'int x0 = {x0}, y0 = {y0}, w = {w}, h = {h}; var px = new {XNA}.Color[w * h]; '
                  f'L.MainTexture.GetData<{XNA}.Color>(0, new {XNA}.Rectangle(x0, H - (y0 + h), w, h), px, 0, px.Length); '
                  'int destroyed = 0, wrong = 0, solid = 0, solidWrong = 0; '
                  'for (int x = x0; x < x0 + w; x++) for (int y = y0; y < y0 + h; y++) { '
                  '  var c = px[(H - 1 - y - (H - (y0 + h))) * w + (x - x0)]; '
                  '  if (!t.GetTerrain(x, y) && t.GetCollisionData(x, y) != 0) { destroyed++; '
                  '    if (t.GetTransparent(x, y)) { if (c.A != 0) wrong++; } '
                  '    else { var bg = L.BackgroundBitmap.GetPixel(x, H - y - 1); if (c.R != bg.R || c.G != bg.G || c.B != bg.B || c.A != 255) wrong++; } } '
                  '  else if (t.GetTerrain(x, y)) { var mn = L.MainBitmap.GetPixel(x, H - y - 1); if (mn.A == 255) { solid++; if (c.R != mn.R || c.G != mn.G || c.B != mn.B || c.A != 255) solidWrong++; } } } '
                  'return destroyed + "," + wrong + "," + solid + "," + solidWrong; })', 60)
        destroyed, wrong, solid, solid_wrong = (int(v) for v in r.split(","))
        assert destroyed > 0
        assert wrong == 0, f"{wrong} of {destroyed} destroyed pixels were not repainted"
        assert solid > 0
        assert solid_wrong == 0, f"{solid_wrong} of {solid} untouched pixels no longer show the main image"
    finally:
        c2.stop()
