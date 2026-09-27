"""Two windows on one default server (bots on): join message (packet 61), hurt overlay and the
render pipeline, chat bar and history, Steam overlay pause, scoreboard. Client A is module-scoped;
client B is started inside the join-message test and reused afterwards."""
import re
import time

import pytest

import conftest as ct
from conftest import wait_until
from clientlib import (KEYS, act, backbuffer_pixels, chat, cs_press, cs_release, entity_state, mod_runtime,  # noqa: F401
                       server_player, settle, start_client, start_server, username, wait_view)

_state = {}


@pytest.fixture(scope="module")
def duo_server(mod_runtime):
    ct.set_server_setting(mod_runtime, "maps", "bases")
    s = start_server(mod_runtime)          # default maxbots 6: safe for two humans (docs/TESTING.md)
    yield s
    s.stop()


@pytest.fixture(scope="module")
def client_a(duo_server, mod_runtime):
    c = start_client(mod_runtime, duo_server)
    wait_until(lambda: entity_state(c), 20)
    yield c
    c.stop()


@pytest.fixture(scope="module")
def client_b(duo_server, mod_runtime, client_a):
    """Started by the join-message test when it runs first; otherwise here."""
    if "b" not in _state:
        _state["b"] = start_client(mod_runtime, duo_server)
    yield _state["b"]


@pytest.fixture(scope="module", autouse=True)
def _stop_b():
    yield
    if "b" in _state:
        _state["b"].stop()


def _message(c):
    r = c.ev('Sync(() => { var v = (GameView)Game.View; var m = Get(v, "message"); var pos = (Vec2)Get(v, "messagePos"); '
             'return (m == null ? "" : (string)m) + "|" + Get(v, "messageAlpha") + "|" + Get(v, "fadeMessage") + "|" + pos.X + "|" + pos.Y; })')
    m, alpha, fade, x, y = r.split("|")
    return {"message": m, "alpha": float(alpha), "fade": fade == "True", "x": float(x), "y": float(y)}


# E2E-20 (server message)
@pytest.mark.e2e
def test_server_join_message_fades_in_at_the_top(duo_server, mod_runtime, client_a):
    a = client_a
    assert "b" not in _state
    _state["b"] = start_client(mod_runtime, duo_server, wait_game=False)
    b = _state["b"]
    name_b = username(b)
    seen = wait_until(lambda: m if name_b in (m := _message(a))["message"] else None, 30, interval=0.02,
                      message="client A never showed the join message")
    # Messages.Parse(PLAYER_JOIN, [display name]): "<colour><name>§1 joined the game"
    assert re.fullmatch("§[0-9]" + re.escape(name_b) + "§1 joined the game", seen["message"]), seen
    assert seen["y"] == 100
    width = a.ev(f'Sync(() => Vexillum.util.TextRenderer.MeasureString(Vexillum.util.TextRenderer.FancyFont, Vexillum.util.TextRenderer.RemoveColors("{seen["message"]}")).X)')
    assert abs(seen["x"] - (420 - float(width) / 2)) < 0.01
    # alpha climbs by 0.05 per Step to 1.0, holds for the packet's duration (2000 ms), then fades to null
    samples = [seen["alpha"]]
    deadline = time.time() + 2
    while time.time() < deadline and samples[-1] < 1.0:
        samples.append(_message(a)["alpha"])
        time.sleep(0.02)
    assert samples[-1] == 1.0 and samples == sorted(samples), samples
    wait_until(lambda: _message(a)["fade"], 5, message="the message never started fading")
    wait_until(lambda: _message(a)["message"] == "", 5, message="the message never cleared")
    wait_view(b, "GameView", 30)


# E2E-20 (hurt overlay + pipeline sanity)
@pytest.mark.e2e
def test_damage_sets_hurt_frame_and_flashes_the_red_border(duo_server, client_a):
    a = client_a
    st = settle(a)
    name = st["name"]
    centre = backbuffer_pixels(a, 420, 315, 1, 1)[0]
    assert centre[:3] != (0, 0, 0)                                   # the level is drawn through the blur pass
    base_r = backbuffer_pixels(a, 5, 315, 1, 1)[0][0]
    before = entity_state(a)
    duo_server.ev(f'Sync(() => {{ var p = ((System.Collections.IEnumerable)Server.players).Cast<Player>().First(q => q.name == "{name}"); '
                  f'p.Entity.Health -= 20; Server.gameMode.PlayerHealthChanged(p, null); return p.Entity.Health.ToString(); }})')
    hurt = wait_until(lambda: s if (s := entity_state(a))["hurtFrame"] > before["hurtFrame"] else None, 5, interval=0.01,
                      message="packet 110 never set hurtFrame")
    assert hurt["health"] == before["health"] - 20
    assert 0 <= hurt["frame"] - hurt["hurtFrame"] < 20
    # border_red.png is blended over border.png for 20 frames: the border pixel's red channel rises then decays
    peak = base_r
    deadline = time.time() + 0.4
    while time.time() < deadline:
        peak = max(peak, backbuffer_pixels(a, 5, 315, 1, 1)[0][0])
        time.sleep(0.01)
    assert peak > base_r + 20, (base_r, peak)
    wait_until(lambda: abs(backbuffer_pixels(a, 5, 315, 1, 1)[0][0] - base_r) <= 6, 5, message="the red border did not decay")


# E2E-16
@pytest.mark.e2e
def test_chat_bar_open_type_cancel_send_and_history_limits(duo_server, client_a, client_b):
    a, b = client_a, client_b
    settle(a)
    name = username(a)
    # opening is a queued task: not yet visible in the tick of the key press, visible on the next Step
    st = act(a, cs_press("OemPeriod") + cs_release("OemPeriod"))
    assert st["showChatBar"] is False
    st = wait_until(lambda: s if (s := entity_state(a))["showChatBar"] else None, 2)
    assert st["chat"] == ""
    st = act(a, "v.CharacterEntered('h'); v.CharacterEntered('i');")
    assert st["chat"] == "hi"
    st = act(a, cs_press("Back") + cs_release("Back"))
    assert st["chat"] == "h"
    st = act(a, cs_press("Escape") + cs_release("Escape"))
    assert st["showChatBar"] is False and st["paused"] is False      # closed without sending, no pause menu
    time.sleep(0.3)
    assert not duo_server.logs(grep=re.escape(name) + ": h$")

    act(a, cs_press("OemPeriod") + cs_release("OemPeriod"))
    wait_until(lambda: entity_state(a)["showChatBar"], 2)
    st = act(a, "".join(f"v.CharacterEntered('{ch}');" for ch in "hello"))
    assert st["chat"] == "hello"
    st = act(a, cs_press("W"))                                         # W types nothing and does not jump
    assert st["jumping"] is False and st["chat"] == "hello"
    act(a, cs_release("W"))
    st = act(a, cs_press("Escape") + cs_release("Escape") + cs_press("OemPeriod") + cs_release("OemPeriod"))
    wait_until(lambda: entity_state(a)["showChatBar"], 2)
    st = act(a, "".join(f"v.CharacterEntered('{ch}');" for ch in "hello") + cs_press("Enter") + cs_release("Enter"))
    assert st["showChatBar"] is False
    duo_server.wait_log(re.escape(name) + ": hello$", 5)

    def chats(c):
        r = c.ev('Sync(() => string.Join("\\n", ((System.Collections.IEnumerable)Get(Game.View, "chats")).Cast<object>().Select(m => Get(m, "old") + "|" + (string)Get(m, "message"))))')
        return [line.split("|", 1) for line in r.split("\n") if line]

    for c in (a, b):
        wait_until(lambda: [m for _, m in chats(c) if m.endswith("> hello") and name in m], 5, message="chat line missing on a client")
    # Enter with an empty bar sends nothing
    n_lines = duo_server.logs(grep=re.escape(name) + ": ").count("\n") + 1
    act(a, cs_press("OemPeriod") + cs_release("OemPeriod"))
    wait_until(lambda: entity_state(a)["showChatBar"], 2)
    st = act(a, cs_press("Enter") + cs_release("Enter"))
    assert st["showChatBar"] is False
    time.sleep(0.3)
    assert duo_server.logs(grep=re.escape(name) + ": ").count("\n") + 1 == n_lines

    # history: at most 17 lines are kept (oldest dropped)
    for i in range(18):
        chat(a, f"line{i}")
    wait_until(lambda: any(m.endswith("> line17") for _, m in chats(a)), 5)
    lines = chats(a)
    assert len(lines) == 17
    assert lines[-1][1].endswith("> line17")
    assert not any(m.endswith("> hello") for _, m in lines)
    # after 20 s a line is old (CleanChats) and DrawChats stops at the first old one unless the bar is open
    assert not lines[-1][0] == "True"
    wait_until(lambda: all(old == "True" for old, _ in chats(a)), 30, interval=1, message="chat lines never aged")


# E2E-18
@pytest.mark.e2e
def test_steam_overlay_callback_pauses_and_unpauses_the_game_view(client_a):
    a = client_a
    settle(a)
    assert a.ev("Vexillum.steam.SteamManager.Initialized") == "True"
    a.ev("Steamworks.CallbackDispatcher.Post(new Steamworks.GameOverlayActivated_t { m_bActive = 1 }); \"posted\"")
    wait_until(lambda: entity_state(a)["paused"], 2, message="overlay on did not pause")
    a.ev("Steamworks.CallbackDispatcher.Post(new Steamworks.GameOverlayActivated_t { m_bActive = 0 }); \"posted\"")
    wait_until(lambda: not entity_state(a)["paused"], 2, message="overlay off did not unpause")


# E2E-19
@pytest.mark.e2e
def test_scoreboard_on_tab_sorted_by_score_and_excluding_spectators(duo_server, client_a, client_b):
    a, b = client_a, client_b
    settle(a)
    name_b = username(b)
    chat(b, "/spec")
    wait_until(lambda: entity_state(b)["class"] == "Spectator", 10)
    wait_until(lambda: server_player(duo_server, name_b)["class"] == "Spectator", 5)
    st = act(a, cs_press("Tab"))
    assert st["showScoreboard"] is True
    r = a.ev('Sync(() => { var sb = Get(Game.View, "scoreboard"); Func<string, string> team = t => string.Join("\\n", ((System.Collections.IEnumerable)Get(sb, t)).Cast<Player>().Select(p => p.name + "|" + p.CurrentClass + "|" + p.Score + "|" + p.pingString)); return team("green") + "\\n--\\n" + team("blue"); })')
    green, blue = r.split("\n--\n")
    rows = {"Green": [l.split("|") for l in green.split("\n") if l], "Blue": [l.split("|") for l in blue.split("\n") if l]}
    for team, entries in rows.items():
        assert all(e[1] == team for e in entries), entries
        scores = [int(e[2]) for e in entries]
        assert scores == sorted(scores, reverse=True), scores
        assert all(re.fullmatch(r"\d+", e[3]) for e in entries), entries       # pingString from packet 130
    assert not any(e[0] == name_b for e in rows["Green"] + rows["Blue"])
    assert any(e[0] == st["name"] for e in rows["Green"] + rows["Blue"])
    st = act(a, cs_release("Tab"))
    assert st["showScoreboard"] is False
