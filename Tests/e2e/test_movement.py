"""Movement input on the local entity and its arrival at the server: walking, jumping,
ladders, and the key routing in Vexillum.KeyPressed. One server with `maxbots 1`
(never a bot) and one window for the module, so the only thing that moves is the client."""
import time

import pytest

import conftest as ct
from conftest import wait_until
from clientlib import (KEYS, MB, act, cs_press, cs_release, entity_state, lone_client, lone_server, mod_runtime,   # noqa: F401 (fixtures)
                       new_runtime, server_player, settle, start_client, start_server)


# E2E-10
@pytest.mark.e2e
def test_movement_keys_drive_the_local_entity_and_reach_the_server(lone_server, lone_client):
    c = lone_client
    st0 = settle(c)
    assert st0["xVelocity"] == 0 and not st0["moving"]

    st1 = act(c, cs_press("D"))
    assert st1["xVelocity"] == st1["speed"] > 0
    assert st1["direction"] is True and st1["moving"] is True and st1["movementChanged"] is True
    # the entity moves right frame by frame (2 px/frame at 60 Hz)
    wait_until(lambda: entity_state(c)["x"] > st0["x"] + 8, 5, message="x did not increase while D was held")
    time.sleep(0.4)                                     # duration: keep walking
    st2 = entity_state(c)
    assert st2["x"] > st1["x"] + 20

    # the server follows through the position packets (16/17 every 100 ms): at most ~12 px behind at 2 px/frame
    wait_until(lambda: abs(server_player(lone_server, st0["name"])["x"] - entity_state(c)["x"]) < 30, 5,
               message="server position did not converge to the client's")
    assert server_player(lone_server, st0["name"])["x"] > st0["x"] + 20

    # A while D is still held, then release D: the opposite key wins
    st3 = act(c, cs_press("A") + cs_release("D"))
    assert st3["xVelocity"] == -st3["speed"]
    assert st3["direction"] is False
    st4 = act(c, cs_release("A"))
    assert st4["xVelocity"] == 0 and st4["moving"] is False and st4["movementChanged"] is True

    x_client = wait_until(lambda: entity_state(c) if abs(entity_state(c)["vx"]) < 0.01 else None, 5)["x"]
    wait_until(lambda: abs(server_player(lone_server, st0["name"])["x"] - x_client) < 3, 5,
               message="server x did not settle within 3 px of the client")


# E2E-10 (remapping: the keys go through ControlSystem)
@pytest.mark.e2e
def test_movement_keys_are_resolved_through_the_control_system(lone_client):
    c = lone_client
    settle(c)
    # an unbound key is KeyAction.None and moves nothing
    st = act(c, cs_press("K"))
    assert st["xVelocity"] == 0 and st["moving"] is False
    act(c, cs_release("K"))
    # rebind K to Move_Right in memory (ControlSystem.SetControl) and it walks; restore afterwards
    st = act(c, f'((System.Collections.IDictionary)Static("Vexillum.ControlSystem", "controls"))[{KEYS}.K] = KeyAction.Move_Right; ' + cs_press("K"))
    try:
        assert st["xVelocity"] == st["speed"] and st["moving"] is True
    finally:
        act(c, cs_release("K") + f' ((System.Collections.IDictionary)Static("Vexillum.ControlSystem", "controls")).Remove({KEYS}.K);')
    settle(c)


# E2E-11 (jump)
@pytest.mark.e2e
def test_jump_gives_one_impulse_and_lands(lone_client):
    c = lone_client
    st0 = settle(c)
    for _ in range(4):
        if not st0["ladder"]:
            break
        # standing in a ladder column W would climb instead (bases has ladders near the green base): step aside
        act(c, cs_press("A"))
        time.sleep(0.4)
        act(c, cs_release("A"))
        st0 = settle(c)
    assert not st0["ladder"], st0
    st1 = act(c, cs_press("W"))
    assert st1["jumping"] is True, st1
    assert st1["vy"] == 6.0                                                     # the jump constant
    assert st1["movementChanged"] is True
    assert c.ev('Sync(() => Get(Get(Game.View, "player"), "jumping"))') == "True"  # LocalPlayer.jumping

    # a second W while airborne is ignored: no second impulse, velocity keeps decaying under gravity
    st2 = act(c, cs_release("W") + cs_press("W"))
    assert st2["jumping"] is True
    assert st2["vy"] < 6.0
    st3 = act(c, cs_release("W") + cs_press("W"))
    assert st3["vy"] < st2["vy"]
    act(c, cs_release("W"))

    # y rises above the ground level then the entity lands (jumping false, back on the ground)
    top = 0.0
    deadline = time.time() + 1.5
    while time.time() < deadline:
        s = entity_state(c)
        top = max(top, s["y"])
        if not s["jumping"] and top > st0["y"] + 5:
            break
        time.sleep(0.02)
    assert top > st0["y"] + 20, top
    landed = wait_until(lambda: s if not (s := entity_state(c))["jumping"] else None, 3)
    assert abs(landed["y"] - st0["y"]) < 2
    # LocalPlayer.jumping clears on landing and Step flags movementChanged once
    assert c.ev('Sync(() => Get(Get(Game.View, "player"), "jumping"))') == "False"


# E2E-11 (ladder)
@pytest.mark.e2e
def test_ladder_climbing_with_w_and_s(scratch_runtime):
    ct.set_server_setting(scratch_runtime, "maxbots", "1")
    ct.set_server_setting(scratch_runtime, "maps", "complex")     # bases has no ladder columns
    server = start_server(scratch_runtime)
    c = start_client(scratch_runtime, server)
    try:
        st = settle(c)
        # find a long ladder column in the level the client actually loaded
        column = c.ev('Sync(() => { var l = ((GameView)Game.View).Level; var t = l.terrain; int bestX = -1, bestLen = 0, bestY0 = 0; '
                      'for (int x = 0; x < (int)l.Size.X; x++) { int run = 0, y0 = 0; for (int y = 0; y < (int)l.Size.Y; y++) { '
                      'if (t.GetLadder(x, y)) { if (run == 0) y0 = y; run++; if (run > bestLen) { bestLen = run; bestX = x; bestY0 = y0; } } else run = 0; } } '
                      'return bestX + "," + bestY0 + "," + bestLen; })', 30)
        x, y0, length = map(int, column.split(","))
        assert length > 60, column
        # server-side teleport (packet 18) onto the middle of the ladder; the client entity falls until the
        # ladder check in Level physics catches it (entity.ladder true, hanging still)
        target_y = y0 + length // 2
        server.ev(f'Sync(() => {{ var p = ((System.Collections.IEnumerable)Server.players).Cast<Player>().First(q => q.name == "{st["name"]}"); '
                  f'((global::Server.ServerPlayer)p).Teleport(new Vec2({x}, {target_y})); return "ok"; }})')
        on_ladder = wait_until(lambda: s if (s := entity_state(c))["ladder"] and abs(s["vy"]) < 0.01 else None, 10,
                               message="entity never hung on the ladder")
        assert abs(on_ladder["x"] - x) < 2

        st_w = act(c, cs_press("W"))
        assert st_w["ladderDirection"] == 1 and st_w["ladder"] is True
        assert st_w["jumping"] is False          # the ladder branch, not Jump()
        time.sleep(0.2)                          # duration: climb
        climbed = entity_state(c)
        assert climbed["y"] > on_ladder["y"] + 2
        st_rel = act(c, cs_release("W"))
        assert st_rel["ladderDirection"] == 0 and st_rel["jumping"] is False
        assert c.ev('Sync(() => ((LocalPlayer)Get(Game.View, "player")).Entity.FixedVelocity.Y)') == "0"

        st_s = act(c, cs_press("S"))
        assert st_s["ladderDirection"] == -1
        time.sleep(0.2)
        descended = entity_state(c)
        assert descended["y"] < climbed["y"] - 2
        st_rel2 = act(c, cs_release("S"))
        assert st_rel2["ladderDirection"] == 0
    finally:
        c.stop()
        server.stop()


# E2E-09
@pytest.mark.e2e
def test_key_routing_shift_tab_repeats_gui_focus_and_click_clearing_focus(lone_client):
    c = lone_client
    settle(c)
    K = KEYS
    # Shift+Tab is swallowed before the view (Steam overlay chord) and Tab is not recorded in pressedKeys
    r = c.ev(f'Sync(() => {{ var v = (GameView)Game.View; Call(Game, "KeyPressed", {K}.LeftShift); Call(Game, "KeyPressed", {K}.Tab); '
             f'var pk = (System.Collections.IDictionary)Get(Game, "pressedKeys"); string s = Get(v, "showScoreboard") + "|" + string.Join(",", pk.Keys.Cast<object>()); '
             f'Call(Game, "KeyReleased", {K}.Tab); Call(Game, "KeyReleased", {K}.LeftShift); return s + "|" + pk.Count; }})')
    assert r == "False|LeftShift|0"
    # Tab alone reaches the view
    r = c.ev(f'Sync(() => {{ var v = (GameView)Game.View; Call(Game, "KeyPressed", {K}.Tab); string s = "" + Get(v, "showScoreboard"); Call(Game, "KeyReleased", {K}.Tab); return s + "|" + Get(v, "showScoreboard"); }})')
    assert r == "True|False"

    # a repeated key delivers isNew=false: movementChanged is not set again
    r = c.ev(f'Sync(() => {{ var v = (GameView)Game.View; var e = ((LocalPlayer)Get(v, "player")).Entity; e.movementChanged = false; Call(Game, "KeyPressed", {K}.D); '
             f'string s = "" + e.movementChanged; e.movementChanged = false; Call(Game, "KeyPressed", {K}.D); s += "|" + e.movementChanged + "|" + e.xVelocity; '
             f'Call(Game, "KeyReleased", {K}.D); return s + "|" + e.xVelocity + "|" + ((System.Collections.IDictionary)Get(Game, "pressedKeys")).Count; }})')
    assert r == "True|False|2|0|0"

    # a focused Nuclex control eats the keys; a click outside the GUI clears the focus and keys flow again
    r = c.ev(f'Sync(() => {{ var v = (GameView)Game.View; var e = ((LocalPlayer)Get(v, "player")).Entity; '
             f'var d = (Nuclex.UserInterface.Controls.Desktop.WindowControl)Activator.CreateInstance(TypeOf("Vexillum.ui.OptionsDialog"), true); Game.View.OpenWindow(d); '
             f'Game.View.GetScreen().FocusedControl = d.Children.OfType<Nuclex.UserInterface.Controls.Desktop.InputControl>().First(); '
             f'Call(Game, "KeyPressed", {K}.W); string s = (Game.View.GetScreen().FocusedControl == null) + "|" + e.jumping; Call(Game, "KeyReleased", {K}.W); '
             f'Call(Game, "MouseMove", 700f, 500f); Call(Game, "MouseDown", {MB}.Right); s += "|" + (Game.View.GetScreen().FocusedControl == null); Call(Game, "MouseUp", {MB}.Right); '
             f'Call(Game, "KeyPressed", {K}.W); s += "|" + e.jumping; Call(Game, "KeyReleased", {K}.W); d.Close(); return s; }})')
    assert r == "False|False|True|True"
    settle(c)
