"""Spectator mode (`/spec` is one-way: PlayerClass stays, CurrentClass becomes Spectator) and the
camera: right-drag pans and is clamped, the camera converges on the player, firing is inert.
Own `maxbots 1` server and window; team switching is checked first while still alive."""
import time

import pytest

from conftest import wait_until
from clientlib import (MB, act, chat, client_entities, cs_press, cs_release, entity_state, lone_client, lone_server,  # noqa: F401
                       mod_runtime, server_entities, server_player, settle)


# E2E-10 / scope: /blue and /green switch the team shown in class
@pytest.mark.e2e
def test_slash_blue_and_slash_green_switch_the_team(lone_server, lone_client):
    c = lone_client
    st = settle(c)
    name, start = st["name"], st["class"]
    other = "Blue" if start == "Green" else "Green"
    # with no bots the other team is emptier, so the switch is allowed; the player is reset and respawns
    chat(c, "/" + other.lower())
    wait_until(lambda: server_player(lone_server, name)["class"] == other, 12, message=f"server never moved the player to {other}")
    wait_until(lambda: entity_state(c)["class"] == other, 5, message="packet 21 never changed the class on the client")
    chat(c, "/" + start.lower())
    wait_until(lambda: server_player(lone_server, name)["class"] == start, 12)
    wait_until(lambda: entity_state(c)["class"] == start, 5)
    # a no-op request (already on that team) changes nothing
    chat(c, "/" + start.lower())
    time.sleep(0.5)
    assert server_player(lone_server, name)["class"] == start


# E2E-24 (non-spectator part) + E2E-13 spectator firing + E2E-24 camera
@pytest.mark.e2e
def test_spectator_drag_pans_and_clamps_the_camera_and_firing_is_inert(lone_server, lone_client):
    c = lone_client
    st0 = settle(c)
    # alive: a right-drag does nothing to the entity
    st = act(c, f"v.MouseMove(400, 300); v.MouseDrag({MB}.Right, 100, 300);")
    assert abs(st["x"] - st0["x"]) < 1 and abs(st["y"] - st0["y"]) < 1

    chat(c, "/spec")
    spec = wait_until(lambda: s if (s := entity_state(c))["class"] == "Spectator" else None, 10, message="never became a spectator")
    assert spec["alive"] is False
    # the camera follows the player: after the class change the entity was moved, and CamPosition converges
    # by ~81.5 % of the remaining distance per frame (0.9^16), i.e. to under 1 px within 30 frames
    wait_until(lambda: abs((s := entity_state(c))["camX"] - s["x"]) < 1 and abs(s["camY"] - s["y"]) < 1, 5)

    st1 = act(c, f"v.MouseMove(400, 300); v.MouseDrag({MB}.Right, 100, 300);")
    assert abs(st1["x"] - (spec["x"] + 300)) < 1 and abs(st1["y"] - spec["y"]) < 1
    assert c.ev('Sync(() => Get(Game.View, "mouseX") + "," + Get(Game.View, "mouseY"))') == "100,300"
    # right after the jump the camera lags behind, then converges
    assert abs(st1["camX"] - st1["x"]) > 100
    wait_until(lambda: abs((s := entity_state(c))["camX"] - s["x"]) < 1, 5, message="camera did not converge")
    cam = c.ev('Sync(() => { var v = (GameView)Game.View; return (v.CamStart.X == v.CamPosition.X - 420 && v.CamStart.Y == v.CamPosition.Y + 315).ToString(); })')
    assert cam == "True"

    # a drag far past the level edge is clamped to the level size
    size = c.ev('Sync(() => { var l = ((GameView)Game.View).Level; return l.Size.X + "," + l.Size.Y; })')
    sx, sy = (float(t) for t in size.split(","))
    st2 = act(c, f"v.MouseDrag({MB}.Right, -100000, 100000);")
    assert (st2["x"], st2["y"]) == (sx, sy)
    st3 = act(c, f"v.MouseDrag({MB}.Right, 100000f + {sx}f, -100000f);")
    assert (st3["x"], st3["y"]) == (0.0, 0.0)

    # firing as a spectator sends nothing: no projectile on either side
    before = len(server_entities(lone_server))
    act(c, f"var piv = e.GetWeaponScreenPivot(v); v.MouseMove(400, 300); v.MouseDown({MB}.Left);")
    time.sleep(0.7)
    act(c, f"v.MouseUp({MB}.Left);")
    assert not [e for e in server_entities(lone_server) if e["type"] == "Rocket"]
    assert len(server_entities(lone_server)) == before
    assert not [e for e in client_entities(c) if e["type"] == "Rocket"]
