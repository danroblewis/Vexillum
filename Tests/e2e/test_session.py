"""Session transitions: pause menu Disconnect, disconnect by the server (packet 254), level change
(packet 253) with automatic reconnect, and map download for a client without the map."""
import hashlib
import re
import shutil
import time

import pytest

import conftest as ct
from conftest import wait_until
from clientlib import (act, chat, connect, cs_press, cs_release, desktop, dialog_texts, entity_state, menu_click,
                       menu_visible, new_runtime, server_humans, set_player_list, set_username, start_client,
                       start_server, terrain_hash, username, view_name, wait_view, window_open)

BASES_TERRAIN_SHA256 = "059ee417b1b64d17f7ebe439dd8bf5b05f01177866873db6dbcd4947716987e3"   # docs/TESTING.md


def _op_client(runtime, server, name="opuser"):
    """A menu client renamed to `name` (listed in Server/ops.txt before the server started) and connected."""
    c = start_client(runtime)
    set_username(c, name)
    connect(c, "127.0.0.1", server.port)
    wait_view(c, "GameView", 30)
    wait_until(lambda: [h for h in server_humans(server) if h["name"] == name], 10)
    return c


# E2E-17
@pytest.mark.e2e
def test_pause_menu_toggles_with_escape_and_disconnect_returns_to_the_main_menu(server, client):
    c = client
    name = username(c)
    st = act(c, cs_press("Escape") + cs_release("Escape"))
    assert st["paused"] is True
    menu = c.ev('Sync(() => { var v = (GameView)Game.View; return v.Menu.GetType().Name + "|" + v.Menu.visible + "|" + string.Join(",", ((System.Collections.IEnumerable)Get(v.Menu, "items")).Cast<object>().Select(i => (string)Get(i, "Text"))); })')
    assert menu == "PauseMenu|True|Disconnect,Options"
    assert [t for k, t in desktop(c) if k == "ButtonControl"] == ["Exit"]
    # the game keeps stepping while paused
    f0 = st["frame"]
    wait_until(lambda: entity_state(c)["frame"] > f0 + 5, 3, message="Level.frame stopped while paused")
    st = act(c, cs_press("Escape") + cs_release("Escape"))
    assert st["paused"] is False
    assert [t for k, t in desktop(c) if k == "ButtonControl"] == []

    # Options over the game: item 1 rect is (30, 100, 170, 50)
    act(c, cs_press("Escape") + cs_release("Escape"))
    menu_click(c, 100, 120)
    assert window_open(c, "OptionsDialog")
    c.ev('Sync(() => { ((Nuclex.UserInterface.Controls.Desktop.WindowControl)((System.Collections.IDictionary)Get(Game.View, "openWindows"))[TypeOf("Vexillum.ui.OptionsDialog")]).Close(); return "ok"; })')

    # Disconnect: item 0 rect is (30, 50, 170, 50)
    menu_click(c, 100, 60)
    c.wait_log(r"Disconnected: $", 5)
    wait_view(c, "MainMenuView", 10)
    assert menu_visible(c)
    assert "ErrorDialog" not in [k for k, _ in desktop(c)]
    wait_until(lambda: not [h for h in server_humans(server) if h["name"] == name], 5, message="server still lists the client")
    server.wait_log(re.escape(name) + " disconnected", 5)
    assert c.alive


# E2E-21
@pytest.mark.e2e
def test_disconnect_by_server_shows_the_reason_and_returns_to_the_menu(scratch_runtime):
    set_player_list(scratch_runtime, "ops", ["opuser"])
    server = start_server(scratch_runtime)
    c = _op_client(scratch_runtime, server)
    try:
        assert server.ev('Sync(() => Get(((System.Collections.IEnumerable)Server.players).Cast<Player>().First(q => q.name == "opuser"), "isOp"))') == "True"
        # the op kicks itself by name (with the correct code this also hits the issuer; the known
        # original bug makes /kick compare the issuer's name, docs/PORTING.md)
        chat(c, "/kick opuser")
        c.wait_log(r"Disconnected: Disconnected by server: You were kicked", 10)
        wait_view(c, "MainMenuView", 10)
        assert menu_visible(c)
        texts = dialog_texts(c, "ErrorDialog")
        assert texts == ["Disconnected by server: You were kicked"], texts
        wait_until(lambda: not [h for h in server_humans(server) if h["name"] == "opuser"], 5, message="server still lists the client")
        assert c.alive
        # and can reconnect
        connect(c, "127.0.0.1", server.port)
        wait_view(c, "GameView", 30)
        wait_until(lambda: [h for h in server_humans(server) if h["name"] == "opuser"], 10)
    finally:
        c.stop()
        server.stop()


# E2E-22
@pytest.mark.e2e
def test_level_change_shows_waiting_for_the_server_and_reconnects(scratch_runtime):
    set_player_list(scratch_runtime, "ops", ["opuser"])
    server = start_server(scratch_runtime)
    c = _op_client(scratch_runtime, server)
    try:
        first = entity_state(c)["map"]
        assert first in ("bases", "complex")                      # random pick from `maps bases complex`
        target = "complex" if first == "bases" else "bases"
        t0 = time.time()
        chat(c, f"/newgame {target}")
        seen = wait_until(lambda: t if "Waiting for the server..." in (t := dialog_texts(c, "StatusDialog")) else None, 10, interval=0.02,
                          message="no 'Waiting for the server...' dialog")
        assert seen == ["Waiting for the server..."]
        assert "ErrorDialog" not in [k for k, _ in desktop(c)]
        c.wait_log(r"Disconnected: $", 5)
        wait_until(lambda: view_name(c) == "GameView" and entity_state(c)["map"] == target, 15, message=f"never landed on {target}")
        assert time.time() - t0 < 15
        assert "StatusDialog" not in [k for k, _ in desktop(c)]
        assert "ErrorDialog" not in [k for k, _ in desktop(c)]
        assert c.ev("Game.waitingForServer") == "False"
        server.wait_log(r"opuser: /newgame " + target, 5)
        wait_until(lambda: [h for h in server_humans(server) if h["name"] == "opuser"], 10)
    finally:
        c.stop()
        server.stop()


# E2E-23
@pytest.mark.e2e
def test_missing_map_is_downloaded_and_saved_with_the_reference_terrain(scratch_runtime):
    ct.set_server_setting(scratch_runtime, "maxbots", "1")       # no explosions: the terrain stays pristine
    ct.set_server_setting(scratch_runtime, "maps", "bases")      # the start map is random within the list
    server = start_server(scratch_runtime)
    r1 = new_runtime("nomaps")
    for m in (r1 / "Maps").glob("*.map"):
        m.unlink()
    c = start_client(r1, server)
    try:
        saved = r1 / "Maps" / "bases.map"
        assert saved.exists()
        data = saved.read_bytes()
        assert int.from_bytes(data[:4], "little") == 0x004F876B
        assert hashlib.sha256(data).hexdigest() == hashlib.sha256((ct.ROOT / "Test" / "Maps" / "bases.map").read_bytes()).hexdigest()
        assert entity_state(c)["map"] == "bases"
        assert terrain_hash(c) == BASES_TERRAIN_SHA256
        assert terrain_hash(server) == BASES_TERRAIN_SHA256
        assert "Could not install map" not in c.logs()
    finally:
        c.stop()
        server.stop()
        shutil.rmtree(r1, ignore_errors=True)


# E2E-23 (no Maps/ directory)
@pytest.mark.e2e
def test_missing_maps_directory_fails_with_could_not_install_map(scratch_runtime):
    server = start_server(scratch_runtime)
    r2 = new_runtime("nomapsdir")
    shutil.rmtree(r2 / "Maps")
    c = start_client(r2, server, wait_seconds=2, wait_game=False)
    try:
        c.wait_log(r"Disconnected: Could not install map\.", 30)
        wait_view(c, "MainMenuView", 10)
        wait_until(lambda: dialog_texts(c, "ErrorDialog") == ["Could not install map."], 5)
        assert menu_visible(c)
        assert not (r2 / "Maps").exists()
        assert c.alive
    finally:
        c.stop()
        server.stop()
        shutil.rmtree(r2, ignore_errors=True)
