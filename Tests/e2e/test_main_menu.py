"""Main menu and its dialogs: layout, Server List (LAN), Options, Report Bug, Direct IP Join.

Every test starts its own menu client (no --connect) in its own scratch runtime;
the Nuclex GUI is driven through the debug console (menu hit tests through
Game.View.MouseMove/MouseDown, dialog buttons through their Pressed handlers).
"""
import re
import time

import pytest

import conftest as ct
from conftest import wait_until, region_stats
from clientlib import (KEYS, UNREACHABLE_HOST, buttons, connect, cs_window, desktop, dialog_texts, entity_state,
                       menu_click, menu_visible, new_runtime, press_desktop_button, press_window_button, server_humans,
                       shot, start_client, start_server, username, wait_view, window_open, window_region)


@pytest.fixture
def menu_client(scratch_runtime):
    c = start_client(scratch_runtime)
    yield c
    c.stop()


# E2E-03
@pytest.mark.e2e
def test_startup_reaches_the_main_menu_with_the_fixed_window_and_layout(menu_client, scratch_runtime):
    c = menu_client
    info = c.ev('Sync(() => Game.View.GetType().Name + "|" + Game.View.Menu.visible + "|" + Game.Window.ClientBounds.Width + "x" + Game.Window.ClientBounds.Height'
                ' + "|" + Game.IsFixedTimeStep + "|" + Game.TargetElapsedTime.TotalMilliseconds + "|" + Game.IsMouseVisible)')
    view, visible, size, fixed, step, mouse = info.split("|")
    assert view == "MainMenuView"
    assert visible == "True"
    assert size == "840x630"                      # CLAUDE.md invariant 8
    assert fixed == "True" and float(step) == 16  # invariant 7
    assert mouse == "True"
    # window title: the author never sets Window.Title (Vexillum.cs), so the MonoGame SdlGameWindow default
    # (null) is what the game shows; pinned so a launcher that starts naming the window is a visible change
    assert c.ev('Sync(() => Game.Window.GetType().Name + "|" + (Game.Window.Title == null ? "<null>" : "[" + Game.Window.Title + "]"))') == "SdlGameWindow|<null>"

    # offline identity (SetSteamIdentity): "<random double> <persona name>", so no ErrorDialog was raised
    name = username(c)
    assert name and re.match(r"^0\.\d+ \S", name), name
    kinds = [k for k, _ in desktop(c)]
    assert "ErrorDialog" not in kinds

    # the two bottom buttons: btnX starts at windowWidth-90 and decreases by 90, y = windowHeight-37
    b = buttons(c)
    assert b["Exit"] == (750.0, 593.0, 80.0, 24.0), b
    assert b["Report Bug"] == (660.0, 593.0, 80.0, 24.0), b
    assert kinds.count("ButtonControl") == 2

    # something is drawn: the title image is not a flat colour (screen capture of the window region)
    region = window_region(c)
    assert region[2:] == (840, 630), region
    path = shot("main-menu", region)
    stats = region_stats(path)
    assert stats["nonuniform"] > 0.05, stats


# E2E-04
@pytest.mark.e2e
def test_start_playing_lists_the_lan_server_and_join_connects(scratch_runtime):
    ct.set_server_setting(scratch_runtime, "maps", "bases")      # the server starts on a random map of the list
    server = start_server(scratch_runtime, lan="on")
    c = start_client(scratch_runtime, lan="on", lan_port=server.lan_port)
    try:
        # item 0 ("Start Playing") hit rect is (0, 225, 391, 50)
        first = c.ev(f'Sync(() => {{ Game.View.MouseMove(200, 240); Game.View.MouseDown(Nuclex.Input.MouseButtons.Left); '
                     f'var d = (Nuclex.UserInterface.Controls.Desktop.WindowControl){cs_window("ServerDialog")}; '
                     f'return d.Title + "|" + d.GetHashCode() + "|" + string.Join("/", ((Nuclex.UserInterface.Controls.Desktop.ListControl)Get(d, "serverList")).Items); }})')
        title, hash1, items = first.split("|")
        assert title == "Server List"
        assert items in ("", "Loading..."), items      # the refresh thread has just started

        # opening the dialog again replaces the first one (OpenWindow closes same-type windows)
        second = c.ev(f'Sync(() => {{ Game.View.MouseMove(200, 240); Game.View.MouseDown(Nuclex.Input.MouseButtons.Left); '
                      f'return {cs_window("ServerDialog")}.GetHashCode().ToString(); }})')
        assert second != hash1
        assert [k for k, _ in desktop(c)].count("ServerDialog") == 1

        def items_and_selection():
            r = c.ev(f'Sync(() => {{ var l = (Nuclex.UserInterface.Controls.Desktop.ListControl)Get({cs_window("ServerDialog")}, "serverList"); '
                     f'return string.Join("/", l.Items) + "|" + string.Join(",", l.SelectedItems); }})')
            return r if "[LAN]" in r else None

        listed = wait_until(items_and_selection, 8, message="the LAN server never appeared in the list")
        items, selected = listed.split("|")
        assert items == "[LAN] Vexillum Server        Map: bases", items   # name + 8 spaces + map
        assert selected == "0"                                              # array.Length == 1 selects it

        press_window_button(c, "ServerDialog", "connectButton")             # Vexillum.Connect(ip, port)
        assert not menu_visible(c)
        wait_view(c, "GameView", 20)
        me = wait_until(lambda: ct.local_player_state(c), 15)
        assert wait_until(lambda: ct.server_player_state(server, me["name"]), 10)["entity_id"] == me["entity_id"]
    finally:
        c.stop()
        server.stop()


# E2E-04 (VEXILLUM_LAN=off branch)
@pytest.mark.e2e
def test_server_list_stays_empty_without_lan_and_join_does_not_crash(scratch_runtime):
    server = start_server(scratch_runtime, lan="on")
    c = start_client(scratch_runtime, lan="off", lan_port=server.lan_port)
    try:
        menu_click(c, 200, 240)
        wait_until(lambda: window_open(c, "ServerDialog"), 5)
        time.sleep(2.5)     # duration: longer than the LAN listen window the dialog would otherwise use
        items = c.ev(f'Sync(() => string.Join("/", ((Nuclex.UserInterface.Controls.Desktop.ListControl)Get({cs_window("ServerDialog")}, "serverList")).Items))')
        assert items == ""
        # Join Server with nothing selected throws (SelectedItems[0]); in the real GUI Vexillum.Update swallows it
        with pytest.raises(ct.EvalError) as ex:
            press_window_button(c, "ServerDialog", "connectButton")
        assert "ArgumentOutOfRange" in ex.value.error
        assert c.alive
        assert menu_visible(c)
    finally:
        c.stop()
        server.stop()


# E2E-05
@pytest.mark.e2e
def test_options_dialog_rebinds_keys_rejects_duplicates_and_persists_controls_xml(menu_client, scratch_runtime):
    c = menu_client
    controls_xml = scratch_runtime / "controls.xml"
    before = controls_xml.read_text()
    mtime0 = controls_xml.stat().st_mtime

    # item 1 ("Options") hit rect is (0, 275, 391, 50)
    menu_click(c, 200, 290)
    wait_until(lambda: window_open(c, "OptionsDialog"), 5)
    layout = c.ev(f'Sync(() => {{ var w = (Nuclex.UserInterface.Controls.Desktop.WindowControl){cs_window("OptionsDialog")}; '
                  'string s = w.Title + "|" + w.Bounds.Left.Offset + "," + w.Bounds.Top.Offset + "," + w.Bounds.Size.X.Offset + "," + w.Bounds.Size.Y.Offset; '
                  'foreach (var ch in w.Children) { var l = ch as Nuclex.UserInterface.Controls.LabelControl; var i = ch as Nuclex.UserInterface.Controls.Desktop.InputControl; '
                  's += "\\n" + ch.GetType().Name + "|" + (l != null ? l.Text : i != null ? i.Text : "") + "|" + ch.Bounds.Left.Offset + "|" + ch.Bounds.Top.Offset; } return s; })')
    lines = layout.split("\n")
    title, bounds = lines[0].split("|")
    assert title == "Options" and bounds == "420,20,275,380"
    labels = [l.split("|") for l in lines[1:] if l.startswith("LabelControl|") and l.split("|")[1]]
    selectors = [l.split("|") for l in lines[1:] if l.startswith("KeySelectorControl|")]
    assert [l[1] for l in labels] == ["Move Left", "Move Right", "Move Down", "Jump", "Reload", "Chat", "SendChat", "Pause", "GrapplingHook", "Show Scoreboard"]
    assert [s[1] for s in selectors] == ["A", "D", "S", "W", "R", "OemPeriod", "Enter", "Escape", "F", "Tab"]
    for i, s in enumerate(selectors):
        assert (float(s[2]), float(s[3])) == (210.0, 40.0 + 30 * i), s

    def set_move_left(key_name):
        # KeySelectorControl.OnKeyPressed only reacts with focus; index 0 is Move_Left
        return c.ev(f'Sync(() => {{ var w = (Nuclex.UserInterface.Controls.Desktop.WindowControl){cs_window("OptionsDialog")}; '
                    f'var sel = w.Children.OfType<Nuclex.UserInterface.Controls.Desktop.InputControl>().First(); Game.View.GetScreen().FocusedControl = sel; '
                    f'Call(sel, "OnKeyPressed", {KEYS}.{key_name}); Game.View.GetScreen().FocusedControl = null; return sel.Text; }})')

    # duplicate: D is already Move_Right
    assert set_move_left("D") == "D"
    press_window_button(c, "OptionsDialog", "okButton")
    assert dialog_texts(c, "ErrorDialog") == ["The same key may not be assigned to multiple controls."]
    assert window_open(c, "OptionsDialog")
    assert controls_xml.read_text() == before

    # Shift display names
    assert set_move_left("LeftShift") == "Shift"
    assert set_move_left("RightShift") == "Shift"
    assert set_move_left("LeftControl") == "Shift"

    # K: saved, ControlSystem updated, controls.xml rewritten with K in A's place
    assert set_move_left("K") == "K"
    press_window_button(c, "OptionsDialog", "okButton")
    assert not window_open(c, "OptionsDialog")
    actions = c.ev(f'Sync(() => {{ var d = (System.Collections.IDictionary)Static("Vexillum.ControlSystem", "controls"); '
                   f'return (d.Contains({KEYS}.K) ? d[{KEYS}.K].ToString() : "None") + "|" + (d.Contains({KEYS}.A) ? d[{KEYS}.A].ToString() : "None"); }})')
    assert actions == "Move_Left|None"
    after = wait_until(lambda: controls_xml.read_text() if "<Keys>K</Keys>" in controls_xml.read_text() else None, 5)
    assert "<Keys>A</Keys>" not in after
    assert after.index("<string>Move_Left</string>") < after.index("<string>Move_Right</string>")
    keys_in_order = re.findall(r"<Keys>(\w+)</Keys>", after)
    assert keys_in_order == ["K", "D", "S", "W", "R", "OemPeriod", "Enter", "Escape", "F", "Tab"], keys_in_order
    mtime1 = controls_xml.stat().st_mtime
    assert mtime1 >= mtime0

    # Cancel leaves the file alone
    menu_click(c, 200, 290)
    wait_until(lambda: window_open(c, "OptionsDialog"), 5)
    assert set_move_left("J") == "J"
    press_window_button(c, "OptionsDialog", "cancelButton")
    assert not window_open(c, "OptionsDialog")
    assert controls_xml.stat().st_mtime == mtime1
    assert controls_xml.read_text() == after


# E2E-06
@pytest.mark.e2e
def test_report_bug_saves_a_local_report_and_confirms_with_its_id(menu_client, scratch_runtime):
    c = menu_client
    press_desktop_button(c, "Report Bug")
    wait_until(lambda: window_open(c, "ReportBugDialog"), 5)
    assert c.ev(f'Sync(() => ((Nuclex.UserInterface.Controls.Desktop.WindowControl){cs_window("ReportBugDialog")}).Title)') == "Report Bug"
    c.ev(f'Sync(() => {{ ((Nuclex.UserInterface.Controls.Desktop.InputControl)Get({cs_window("ReportBugDialog")}, "info")).Text = "test"; return "ok"; }})')
    press_window_button(c, "ReportBugDialog", "ok")

    def sent():
        texts = dialog_texts(c, "ErrorDialog")
        return texts if any(t.startswith("Report sent! ID: local-") for t in texts) else None

    texts = wait_until(sent, 10, message="no confirmation dialog")
    report_id = [t for t in texts if t.startswith("Report sent!")][0][len("Report sent! ID: "):]
    assert re.match(r"^local-\d{8}-\d{6}$", report_id), report_id
    assert not window_open(c, "ReportBugDialog")
    assert "StatusDialog" not in [k for k, _ in desktop(c)]          # 'Sending report...' closed again

    report = scratch_runtime / "bugreports" / f"{report_id}.txt"
    assert report.exists()
    text = report.read_text()
    head, _, body = text.partition("\n\n")
    assert head.splitlines()[0] == "version: 1"
    assert head.splitlines()[1].startswith("os: ")
    assert head.splitlines()[2] == "info: test"
    # the log was flushed (Util.WriteDebugLog) before it was read, so the report carries the startup lines
    assert "PortProgram: cwd=" in body
    assert (scratch_runtime / "debug_client.log").read_text().splitlines()[0] == body.splitlines()[0]
    c.wait_log(r"MasterServer: bug report saved to .*" + re.escape(report_id), 5)


# E2E-07
@pytest.mark.e2e
def test_direct_ip_join_dialog_defaults_port_fallback_and_join(scratch_runtime):
    server = start_server(scratch_runtime, lan="on")
    c = start_client(scratch_runtime)
    try:
        menu_click(c, 200, 240)
        wait_until(lambda: window_open(c, "ServerDialog"), 5)
        press_window_button(c, "ServerDialog", "ipButton")
        info = c.ev(f'Sync(() => {{ var w = (Nuclex.UserInterface.Controls.Desktop.WindowControl){cs_window("IPJoinDialog")}; '
                    'return w.Title + "|" + ((Nuclex.UserInterface.Controls.Desktop.InputControl)Get(w, "ipBox")).Text + "|" + ((Nuclex.UserInterface.Controls.Desktop.InputControl)Get(w, "portBox")).Text'
                    ' + "|" + w.Bounds.Size.X.Offset + "x" + w.Bounds.Size.Y.Offset + "|" + w.Bounds.Left.Fraction + "/" + w.Bounds.Left.Offset + "|" + w.Bounds.Top.Fraction + "/" + w.Bounds.Top.Offset; })')
        assert info == "Connect to Server|127.0.0.1|24224|400x65|0.5/-200|0.5/-65"

        # a non-numeric port falls back to DEFAULT_PORT (24224). The host is one that fails DNS at once,
        # so the run never touches a real server on 24224 (docs/TESTING.md: never hard-code that port).
        c.ev(f'Sync(() => {{ var w = {cs_window("IPJoinDialog")}; ((Nuclex.UserInterface.Controls.Desktop.InputControl)Get(w, "ipBox")).Text = "{UNREACHABLE_HOST}"; '
             f'((Nuclex.UserInterface.Controls.Desktop.InputControl)Get(w, "portBox")).Text = "notanumber"; return "ok"; }})')
        press_window_button(c, "IPJoinDialog", "connectButton")
        # SetMenuVisible(false) closed the dialogs; the StatusDialog the client thread adds survives it
        kinds = [k for k, _ in desktop(c)]
        assert "ServerDialog" not in kinds and "IPJoinDialog" not in kinds
        c.wait_log(r"Connecting to " + re.escape(UNREACHABLE_HOST) + ":24224", 10)
        c.wait_log(r"Disconnected: Could not connect to the server\.", 15)
        wait_until(lambda: dialog_texts(c, "ErrorDialog") == ["Could not connect to the server."], 5)
        assert menu_visible(c)

        # with the real port the client logs in
        menu_click(c, 200, 240)
        wait_until(lambda: window_open(c, "ServerDialog"), 5)
        press_window_button(c, "ServerDialog", "ipButton")
        c.ev(f'Sync(() => {{ var w = {cs_window("IPJoinDialog")}; ((Nuclex.UserInterface.Controls.Desktop.InputControl)Get(w, "ipBox")).Text = "127.0.0.1"; '
             f'((Nuclex.UserInterface.Controls.Desktop.InputControl)Get(w, "portBox")).Text = "{server.port}"; return "ok"; }})')
        press_window_button(c, "IPJoinDialog", "connectButton")
        assert not menu_visible(c)
        wait_view(c, "GameView", 20)
        name = username(c)
        assert wait_until(lambda: [p for p in server_humans(server) if p["name"] == name], 10)
        assert c.ev("Sync(() => Game.View.Menu == null)") == "True"
    finally:
        c.stop()
        server.stop()


# E2E-27 (Host Server button on a non-Windows host)
@pytest.mark.e2e
def test_host_server_button_fails_without_writing_ops_txt(menu_client, scratch_runtime):
    c = menu_client
    ops = scratch_runtime / "Server" / "ops.txt"
    before = ops.read_text()
    menu_click(c, 200, 240)
    wait_until(lambda: window_open(c, "ServerDialog"), 5)
    # Process.Start("./VexillumServerStart.exe") throws before the ops.txt block is reached
    with pytest.raises(ct.EvalError) as ex:
        press_window_button(c, "ServerDialog", "hostButton")
    assert "Win32Exception" in ex.value.error or "FileNotFound" in ex.value.error, ex.value.error
    assert c.alive
    assert ops.read_text() == before
    assert not (scratch_runtime / "ops.txt").exists()
    assert "ErrorDialog" not in [k for k, _ in desktop(c)]
