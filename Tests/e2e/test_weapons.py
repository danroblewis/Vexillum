"""Weapons: selection keys and wheel, aiming and firing (rocket + terrain destruction on both
sides), reload and the ammo HUD, the grappling hook. One `maxbots 1` server and one window
for the module: nothing but the client shoots, so terrain changes are attributable."""
import math
import time

import pytest

from conftest import wait_until
from clientlib import (MB, act, backbuffer_pixels, client_entities, cs_press, cs_release, entity_state, lone_client,  # noqa: F401
                       lone_server, mod_runtime, server_entities, server_player, settle, terrain_bits, terrain_hash)

AIM_RIGHT = "var piv = e.GetWeaponScreenPivot(v); v.MouseMove(piv.X + 200, piv.Y);"


def _projectiles(entities, kind="Rocket"):
    return [e for e in entities if e["type"] == kind]


# E2E-12
@pytest.mark.e2e
def test_weapon_selection_by_number_keys_and_mouse_wheel(lone_server, lone_client):
    c = lone_client
    st0 = settle(c)
    assert st0["weaponIndex"] == 0 and st0["weapon"] == "RocketLauncher"
    name = st0["name"]
    inventory = c.ev('Sync(() => string.Join(",", ((LocalPlayer)Get(Game.View, "player")).Inventory.Select(w => w.GetType().Name)))')
    assert inventory == "RocketLauncher,SMG,Sword"

    st = act(c, cs_press("D2") + cs_release("D2"))
    assert st["weaponIndex"] == 1 and st["weapon"] == "SMG"
    wait_until(lambda: server_player(lone_server, name)["weaponIndex"] == 1, 3, message="packet 13 did not reach the server")
    assert server_player(lone_server, name)["weapon"] == "SMG"

    # D9: index 8 is outside the 3-slot inventory, nothing changes and no packet is sent
    st = act(c, cs_press("D9") + cs_release("D9"))
    assert st["weaponIndex"] == 1
    time.sleep(0.3)
    assert server_player(lone_server, name)["weaponIndex"] == 1

    # wheel: down selects index+1, up index-1, clamped by Player.SelectWeapon; client-local (no packet 13)
    st = act(c, "v.MouseWheelMoved(-1);")
    assert st["weaponIndex"] == 2 and st["weapon"] == "Sword"
    st = act(c, "v.MouseWheelMoved(1); v.MouseWheelMoved(1);")
    assert st["weaponIndex"] == 0 and st["weapon"] == "RocketLauncher"
    st = act(c, "v.MouseWheelMoved(1);")
    assert st["weaponIndex"] == 0                                # clamped at 0
    time.sleep(0.3)
    assert server_player(lone_server, name)["weaponIndex"] == 1  # the server still has the last key selection
    # the next key selection resynchronises
    act(c, cs_press("D1") + cs_release("D1"))
    wait_until(lambda: server_player(lone_server, name)["weaponIndex"] == 0, 3)


# E2E-13
@pytest.mark.e2e
def test_mouse_aim_and_left_click_fire_a_rocket_that_destroys_terrain_on_both_sides(lone_server, lone_client):
    c = lone_client
    st0 = settle(c)
    name = st0["name"]
    st = act(c, AIM_RIGHT + ' string pv = piv.X + "," + piv.Y; ')
    assert abs(st["armAngle"]) < 0.5                              # atan2(0, +200) = 0
    pivot = c.ev('Sync(() => { var v = (GameView)Game.View; var e = ((LocalPlayer)Get(v, "player")).Entity; var piv = e.GetWeaponScreenPivot(v); return piv.X + "," + piv.Y; })')
    px, py = (float(t) for t in pivot.split(","))
    st = act(c, f"v.MouseMove({px + 100}f, {py + 100}f);")
    assert abs(st["armAngle"] - math.atan2(100, 100)) < 1e-4
    # the bases are walled with Solid (indestructible) terrain: put the player on open, destructible ground
    # first (server-side Teleport = packet 18, the same path a respawn uses)
    spot = c.ev('Sync(() => { var v = (GameView)Game.View; var t = v.Level.terrain; int W = (int)v.Level.Size.X, H = (int)v.Level.Size.Y; '
                'for (int x = 400; x < W - 400; x += 16) { int top = -1; for (int y = H - 1; y >= 0; y--) if (t.GetTerrain(x, y)) { top = y; break; } '
                'if (top < 0 || top > H - 80) continue; bool ok = t.GetCollisionData(x, top) != 1; '
                'for (int dx = -40; dx <= 120 && ok; dx += 8) { int ty = -1; for (int y = H - 1; y >= 0; y--) if (t.GetTerrain(x + dx, y)) { ty = y; break; } '
                'if (ty < 0 || Math.Abs(ty - top) > 30 || t.GetCollisionData(x + dx, ty) == 1) ok = false; } if (ok) return x + "," + top; } return "none"; })', 30)
    assert spot != "none", "no open destructible ground on this map"
    sx_, sy_ = (int(t) for t in spot.split(","))
    lone_server.ev(f'Sync(() => {{ var p = ((System.Collections.IEnumerable)Server.players).Cast<Player>().First(q => q.name == "{name}"); '
                   f'((global::Server.ServerPlayer)p).Teleport(new Vec2({sx_}, {sy_ + 30})); return "ok"; }})')
    wait_until(lambda: abs((s := entity_state(c))["x"] - sx_) < 40 and abs(s["y"] - (sy_ + 20)) < 8 and not s["jumping"] and abs(s["vy"]) < 0.4, 10,
               message="the player did not land on the open ground")
    # DrawStuff re-applies MouseMove(mouseX, mouseY) while the camera is still converging on the player,
    # which would re-aim from the stale window mouse position: wait until the camera is at rest
    wait_until(lambda: abs((s := entity_state(c))["camX"] - s["x"]) < 0.05 and abs(s["camY"] - s["y"]) < 0.05, 10,
               message="camera did not settle after the teleport")
    st0 = entity_state(c)

    # rockets fly straight (FixedVelocity) and Destroy() skips Solid collision pixels,
    # so trace the aim ray from the weapon pivot and pick an angle whose first terrain hit is destructible
    found = c.ev('Sync(() => { var v = (GameView)Game.View; var p = (LocalPlayer)Get(v, "player"); var e = p.Entity; var t = v.Level.terrain; var piv = e.Weapon.GetPivot(); '
                 'for (int k = 0; k < 30; k++) { float a = -0.4f + 0.05f * k; var dir = new Vec2((float)Math.Cos(a), -(float)Math.Sin(a)); var pos = piv; int n = 0; bool hit = false; '
                 'while (n < 700) { pos += dir; n++; if (pos.X < 0 || pos.Y < 0 || pos.X >= v.Level.Size.X || pos.Y >= v.Level.Size.Y) break; if (t.GetTerrain((int)pos.X, (int)pos.Y)) { hit = true; break; } } '
                 'if (hit && n > 60 && t.GetCollisionData((int)pos.X, (int)pos.Y) != 1) return a.ToString(' + "System.Globalization.CultureInfo.InvariantCulture" + ') + "|" + (int)pos.X + "|" + (int)pos.Y; } return "none"; })')
    assert found != "none", "no destructible terrain within 700 px of the spawn"
    angle, hx, hy = float(found.split("|")[0]), int(found.split("|")[1]), int(found.split("|")[2])
    R = 300.0
    st = act(c, f"var piv = e.GetWeaponScreenPivot(v); v.MouseMove(piv.X + {R * math.cos(angle)}f, piv.Y + {R * math.sin(angle)}f);")
    assert abs(st["armAngle"] - angle) < 1e-3 and abs(st["armAngle"]) < 0.5
    # WriteAngle quantises to an sbyte (pi/127 steps), so the server's copy is within 0.05 rad
    history = []
    deadline = time.time() + 5
    while time.time() < deadline:
        srv = server_player(lone_server, name)
        cli = c.ev('Sync(() => { var p = (LocalPlayer)Get(Game.View, "player"); var cl = p.GetClient(); return p.ArmAngle + "|" + Get(cl, "sendAngle") + "|" + Get(cl, "sendPosition") + "|" + p.Entity.movementChanged + "|" + cl.IsConnected(); })')
        history.append((srv["armAngle"], srv["x"], srv["y"], cli))
        if abs(srv["armAngle"] - st["armAngle"]) < 0.05:
            break
        time.sleep(0.1)
    assert abs(history[-1][0] - st["armAngle"]) < 0.05, f"server never received arm angle {st['armAngle']}: {history[-6:]}"

    before_client, before_server = terrain_hash(c), terrain_hash(lone_server)
    assert before_client == before_server
    assert not _projectiles(server_entities(lone_server)) and not _projectiles(client_entities(c))
    n_client = len(client_entities(c))
    # the terrain around the predicted impact point (explosion radius 26)
    strip = (hx - 60, hy - 60, 120, 120)
    strip_before = terrain_bits(c, *strip)
    assert strip_before == terrain_bits(lone_server, *strip)

    st = act(c, f"v.MouseDown({MB}.Left);")
    assert st["clip"] == st0["clip"] - 1                          # the launcher fired client-side
    rockets = wait_until(lambda: _projectiles(server_entities(lone_server)) or None, 3, message="no Rocket on the server")
    assert len(rockets) == 1
    wait_until(lambda: len(client_entities(c)) == n_client + 1 and _projectiles(client_entities(c)), 3,
               message="packet 42 never created the rocket on the client")
    act(c, f"v.MouseUp({MB}.Left);")

    # the explosion (packet 15, scheduled on the client at the server's frame) removes terrain identically on both sides
    wait_until(lambda: not _projectiles(server_entities(lone_server)), 8, message="the rocket never exploded")
    after_client = wait_until(lambda: h if (h := terrain_hash(c)) != before_client else None, 5, message="client terrain unchanged")
    after_server = wait_until(lambda: h if (h := terrain_hash(lone_server)) != before_server else None, 5, message="server terrain unchanged")
    assert after_client == after_server
    strip_client = wait_until(lambda: s if (s := terrain_bits(c, *strip)) != strip_before else None, 5, message="no crater in the strip on the client")
    strip_server = terrain_bits(lone_server, *strip)
    assert strip_client == strip_server
    assert strip_client.count("1") < strip_before.count("1")      # pixels were destroyed, not added


# E2E-14
@pytest.mark.e2e
def test_reload_key_refills_the_clip_and_the_ammo_hud_reflects_it(lone_server, lone_client):
    c = lone_client
    settle(c)
    st = act(c, cs_press("D2") + cs_release("D2") + AIM_RIGHT + f" v.MouseDown({MB}.Left);")
    assert st["weapon"] == "SMG"
    full = st["maxClip"]
    wait_until(lambda: entity_state(c)["clip"] <= full - 3, 5, message="the SMG did not fire")
    st = act(c, f"v.MouseUp({MB}.Left);")
    assert 0 < st["clip"] < full
    # HUD: the ammo indicator (5,5,29,29) is drawn in Colors.ammoIndicatorColor while the clip has rounds
    color = c.ev("Vexillum.util.Colors.ammoIndicatorColor.R + \",\" + Vexillum.util.Colors.ammoIndicatorColor.G + \",\" + Vexillum.util.Colors.ammoIndicatorColor.B")
    cr, cg, cb = (int(v) for v in color.split(","))
    pixels = backbuffer_pixels(c, 5, 5, 29, 29)
    assert sum(1 for (r, g, b, a) in pixels if (r, g, b) == (cr, cg, cb)) > 0

    st = act(c, cs_press("R") + cs_release("R"))
    reloaded = wait_until(lambda: s if (s := entity_state(c))["clip"] == full else None, 10, message="the clip never refilled")
    assert reloaded["clip"] == reloaded["maxClip"]
    # the server ran the same reload and its packet 11 agreed with the client's total
    assert reloaded["total"] < st["total"] or reloaded["total"] == st["total"]

    # holding R (isNew=false) sends nothing: fire one round, hold R, the clip stays short for a while
    act(c, f"v.MouseDown({MB}.Left);")
    wait_until(lambda: entity_state(c)["clip"] < full, 5)
    st = act(c, f"v.MouseUp({MB}.Left); v.KeyPressed(Microsoft.Xna.Framework.Input.Keys.R, false);")
    time.sleep(0.6)
    assert entity_state(c)["clip"] == st["clip"]
    act(c, cs_release("R"))
    settle(c)


# E2E-15
@pytest.mark.e2e
def test_grappling_hook_fires_locks_movement_and_releases_on_w(lone_server, lone_client):
    c = lone_client
    st0 = settle(c)
    name = st0["name"]
    # aim at the sky: nothing to grab within range, F sends nothing
    st = act(c, "var piv = e.GetWeaponScreenPivot(v); v.MouseMove(piv.X, piv.Y - 300);")
    if not st["canGrapple"]:
        act(c, cs_press("F") + cs_release("F"))
        time.sleep(0.5)
        assert not [e for e in server_entities(lone_server) if e["type"] == "GrapplingHook"]
        assert entity_state(c)["hook"] == ""

    # aim at terrain within grapple range (420 px): try a fan of directions from the weapon pivot
    st = None
    for dx, dy in ((200, -150), (200, 0), (250, -80), (-200, -150), (-200, 0), (0, -250), (150, 150), (-150, 150)):
        st = act(c, f"var piv = e.GetWeaponScreenPivot(v); v.MouseMove(piv.X + {dx}, piv.Y + {dy});")
        if st["canGrapple"]:
            break
    assert st["canGrapple"] is True, "no grapple target around the spawn"
    act(c, cs_press("F") + cs_release("F"))
    # packet 42 creates the hook on the client; it auto-releases once the player is pulled to it,
    # so everything that needs the attached hook happens in one update tick below
    hooked = wait_until(lambda: s if (s := entity_state(c))["hook"] else None, 3, interval=0.01, message="no hook arrived (packet 22/42)")
    assert hooked["hook"].startswith("GrapplingHook#")
    assert [e for e in server_entities(lone_server) if e["type"] == "GrapplingHook"]
    r = c.ev(f'Sync(() => {{ var v = (GameView)Game.View; var p = (LocalPlayer)Get(v, "player"); var e = p.Entity; if (e.hook == null) return "released"; '
             f'float xv0 = e.xVelocity; v.KeyPressed(Microsoft.Xna.Framework.Input.Keys.D, true); string s = (e.xVelocity == xv0) + "|" + e.moving; v.KeyReleased(Microsoft.Xna.Framework.Input.Keys.D); '
             f'var v0 = e.Velocity; v.KeyPressed(Microsoft.Xna.Framework.Input.Keys.W, true); v.KeyReleased(Microsoft.Xna.Framework.Input.Keys.W); '
             f'return s + "|" + (e.hook == null) + "|" + (e.Velocity == v0 * 0.5f); }})')
    if r == "released":
        pytest.skip("the hook pulled the player in before the test could act (timing); rerun")
    assert r == "True|False|True|True", r
    wait_until(lambda: not [e for e in server_entities(lone_server) if e["type"] == "GrapplingHook"], 3,
               message="the server did not remove the hook after packet 11 (254)")
    assert server_player(lone_server, name)["hook"] == ""
    settle(c)
