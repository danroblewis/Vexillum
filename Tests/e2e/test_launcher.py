"""The port's client launcher (ZombieSurvival/PortProgram.cs): --help, --root/--connect parsing,
--connect timing and timeout, Exit from the menus, two clients from one runtime directory."""
import os
import re
import socket
import threading
import time

import pytest

import conftest as ct
from conftest import wait_until
from clientlib import (DEFAULT_PORT, UNREACHABLE_HOST, desktop, dialog_texts, entity_state, launch_raw, lock_is_held, menu_visible,
                       new_runtime, press_desktop_button, server_humans, start_client, start_server, username,
                       view_name, wait_menu, wait_view)

HELP_LINES = [
    "VexillumGame [--root <dir>] [--connect <host>:<port>] [--help]",
    "  --root <dir>            run with <dir> as the working directory (Content/, Maps/, settings.xml)",
    "  --connect <host>:<port> join that server as soon as the main menu is loaded",
    "  VEXILLUM_LOG_STDOUT=1   echo the debug log to stdout (Debug builds)",
    "  VEXILLUM_DEBUG_PORT=<n>  start the in-process debug console on 127.0.0.1:<n>",
]
STAMP = r"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\] "


# E2E-01
@pytest.mark.e2e
@pytest.mark.parametrize("flag", ["--help", "-h", "/?"])
def test_help_prints_usage_and_exits_without_starting_the_game(flag, tmp_path):
    cwd = tmp_path / "cwd"
    cwd.mkdir()
    p = launch_raw([flag], cwd=cwd, log_stdout=False)
    code = p.wait_exit(20)
    assert code == 0, p.logs()
    assert p.logs().splitlines() == HELP_LINES
    assert sorted(os.listdir(cwd)) == []          # no debug_client.log, no lock
    assert not p.alive


# E2E-02
@pytest.mark.e2e
def test_root_and_connect_arguments_stdout_echo_and_log_location(tmp_path):
    rt = new_runtime("args")
    real_rt = str(rt.resolve())          # the process reports the real path (/private/var/... on macOS)
    other_cwd = tmp_path / "elsewhere"
    other_cwd.mkdir()
    for target, expected in ((UNREACHABLE_HOST, f"{UNREACHABLE_HOST}:{DEFAULT_PORT}"),          # no colon: DEFAULT_PORT
                             (UNREACHABLE_HOST + ":abc", f"{UNREACHABLE_HOST}:{DEFAULT_PORT}")):  # unparsable port: DEFAULT_PORT
        p = launch_raw(["--root", str(rt), "--connect", target, "--bogus"], cwd=other_cwd)
        try:
            line = p.wait_log(r"PortProgram: cwd=", 20).splitlines()[-1]
            assert line.endswith(f"PortProgram: cwd={real_rt} connect={expected}"), line
            assert re.match(STAMP, line), line
            # the process really changed directory (Util paths are relative to it)
            assert wait_until(lambda: _cwd(p), 15) == real_rt
            assert (rt / "lock").exists() and lock_is_held(rt)
            # the same line goes to R/debug_client.log: Util.Debug buffers until 1 KB or WriteDebugLog
            # (the startup lines are shorter than that, so flush the way OnExit does)
            logfile = rt / "debug_client.log"
            p.ev("Vexillum.Util.WriteDebugLog(); \"flushed\"")
            assert logfile.exists() and f"connect={expected}" in logfile.read_text(errors="ignore")
            file_line = [l for l in logfile.read_text(errors="ignore").splitlines() if "PortProgram: cwd=" in l][-1]
            assert file_line == line
            assert not (other_cwd / "debug_client.log").exists()
            assert not (other_cwd / "lock").exists()
        finally:
            p.stop()
    # without VEXILLUM_LOG_STDOUT the line is only in the file
    p = launch_raw(["--root", str(rt)], cwd=other_cwd, log_stdout=False)
    try:
        wait_until(lambda: _cwd(p), 20)
        assert "PortProgram: cwd=" not in p.logs()
        # the debug-console's own listening line is logged after PortProgram's, so the buffer holds both
        p.ev("Vexillum.Util.WriteDebugLog(); \"flushed\"")
        text = (rt / "debug_client.log").read_text(errors="ignore")
        assert re.search(STAMP.replace("^", r"(?m)^") + re.escape(f"PortProgram: cwd={real_rt}") + r"$", text), text[-500:]
        assert "PortProgram: cwd=" not in p.logs()
    finally:
        p.stop()


def _cwd(p):
    try:
        return p.ev("System.IO.Directory.GetCurrentDirectory()")
    except (ct.EvalError, OSError):
        return None


# E2E-08 (a)
@pytest.mark.e2e
def test_connect_joins_once_the_menu_is_ready(scratch_runtime):
    server = start_server(scratch_runtime)
    c = start_client(scratch_runtime, server)
    try:
        line = c.wait_log(r"PortProgram: main menu ready after", 5).splitlines()[-1]
        m = re.search(r"main menu ready after (\d+) ms, connecting to 127\.0\.0\.1:(\d+)$", line)
        assert m, line
        assert int(m.group(1)) % 250 == 0 and int(m.group(2)) == server.port
        assert view_name(c) == "GameView"
        assert c.ev("Sync(() => Game.View.Menu == null)") == "True"     # no pause menu
        name = username(c)
        assert wait_until(lambda: [p for p in server_humans(server) if p["name"] == name], 10)
        assert c.ev("Game.waitingForServer") == "False"
    finally:
        c.stop()
        server.stop()


# E2E-08 (b)
@pytest.mark.e2e
def test_connect_times_out_cleanly_when_no_server_answers(scratch_runtime):
    port = ct.free_tcp_port()      # nothing listens here: the probe is refused at once
    c = start_client(scratch_runtime, connect=f"127.0.0.1:{port}", wait_seconds=1, wait_game=False)
    try:
        c.wait_log(r"PortProgram: main menu ready after \d+ ms, connecting to 127\.0\.0\.1:" + str(port), 20)
        assert wait_until(lambda: c.ev("Game.waitingForServer") == "True", 5)
        assert not menu_visible(c)                          # SetMenuView(false) while polling
        t0 = time.time()
        texts = wait_until(lambda: dialog_texts(c, "ErrorDialog") or None, 40, interval=0.5,
                           message="no 'Connection timed out.' dialog")
        elapsed = time.time() - t0
        assert texts == ["Connection timed out."]
        assert elapsed > 10, elapsed                        # 40 polls x 500 ms minus what already passed
        assert c.ev("Game.waitingForServer") == "True"      # the flag is only cleared on success or Cancel
        assert menu_visible(c)
        assert c.alive
        assert "gave up waiting for the main menu" not in c.logs()
    finally:
        c.stop()


class _NotReadyServer:
    """A TCP listener that answers every status probe (byte 255, docs/PROTOCOL.md) with ready=false
    and records the time and first byte of each probe, so the client's poll loop can be counted."""

    def __init__(self):
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.sock.bind(("127.0.0.1", 0))
        self.sock.listen(8)
        self.sock.settimeout(0.2)
        self.port = self.sock.getsockname()[1]
        self.probes = []
        self.stop = threading.Event()
        self.thread = threading.Thread(target=self._serve, daemon=True)
        self.thread.start()

    def _serve(self):
        while not self.stop.is_set():
            try:
                conn, _ = self.sock.accept()
            except socket.timeout:
                continue
            with conn:
                conn.settimeout(2)
                try:
                    first = conn.recv(1)
                except OSError:
                    first = b""
                self.probes.append((time.time(), first))
                try:
                    conn.sendall(b"\x00")          # BinaryReader.ReadBoolean() -> false: keep polling
                except OSError:
                    pass

    def close(self):
        self.stop.set()
        self.thread.join(2)
        self.sock.close()


# E2E-08 (b): the exact poll schedule, observed from the server side of the probe
@pytest.mark.e2e
def test_connect_polls_the_server_status_40_times_every_500_ms_before_timing_out(scratch_runtime):
    fake = _NotReadyServer()
    c = start_client(scratch_runtime, connect=f"127.0.0.1:{fake.port}", wait_seconds=1, wait_game=False)
    try:
        c.wait_log(r"PortProgram: main menu ready after \d+ ms, connecting to 127\.0\.0\.1:" + str(fake.port), 20)
        texts = wait_until(lambda: dialog_texts(c, "ErrorDialog") or None, 40, interval=0.5,
                           message="no 'Connection timed out.' dialog")
        assert texts == ["Connection timed out."]
        time.sleep(1.0)                                    # a 41st probe would arrive within 500 ms
        probes = list(fake.probes)
        assert len(probes) == 40, len(probes)              # ConnectWhenServerReady(host, port, 40)
        assert all(first == b"\xff" for _, first in probes), [first for _, first in probes]
        gaps = [round(b[0] - a[0], 3) for a, b in zip(probes, probes[1:])]
        assert all(0.45 <= g <= 0.9 for g in gaps), gaps    # Thread.Sleep(500) between probes
        assert 19 <= probes[-1][0] - probes[0][0] <= 23, probes[-1][0] - probes[0][0]
        assert c.ev("Game.waitingForServer") == "True"      # only Cancel or success clears it
        assert menu_visible(c)
        assert c.alive
        assert "--connect failed" not in c.logs()            # ReadBoolean got its byte every time
    finally:
        c.stop()
        fake.close()


# E2E-25 (pause menu Exit)
@pytest.mark.e2e
def test_exit_from_the_pause_menu_disconnects_flushes_the_log_and_releases_the_lock(scratch_runtime):
    server_rt = new_runtime("srv")     # the server holds its own ./lock, so it must not share the client's directory
    server = start_server(server_rt)
    p = launch_raw(["--root", str(scratch_runtime), "--connect", f"127.0.0.1:{server.port}"], cwd=scratch_runtime.parent)
    try:
        wait_view(p, "GameView", 40)
        name = username(p)
        wait_until(lambda: [h for h in server_humans(server) if h["name"] == name], 10)
        assert lock_is_held(scratch_runtime)
        st = entity_state(p, "v.KeyPressed(Microsoft.Xna.Framework.Input.Keys.Escape, true); v.KeyReleased(Microsoft.Xna.Framework.Input.Keys.Escape);")
        assert st["paused"]
        assert "Exit" in [t for k, t in desktop(p) if k == "ButtonControl"]
        press_desktop_button(p, "Exit")                     # PauseMenu.quit: Disconnect then Exit
        code = p.wait_exit(10)
        assert code == 0, p.logs()
        wait_until(lambda: not [h for h in server_humans(server) if h["name"] == name], 5, message="server still lists the client")
        server.wait_log(re.escape(name) + " disconnected", 5)
        assert not lock_is_held(scratch_runtime)
        # OnExit wrote the buffered lines: the user disconnect (null message) is in the file
        lines = (scratch_runtime / "debug_client.log").read_text(errors="ignore").splitlines()
        assert any(l.endswith("] Disconnected: ") for l in lines[-10:]), lines[-10:]
        # the last line is a stamped message, or the continuation of a multi-line one (an exception's
        # "   at ..." trace logged by a network thread as the socket closes); never a torn fragment
        assert re.match(STAMP, lines[-1]) or lines[-1].startswith(" "), lines[-5:]
    finally:
        p.stop()
        server.stop()


# E2E-25 (main menu Exit, no client)
@pytest.mark.e2e
def test_exit_from_the_main_menu_exits_cleanly(scratch_runtime):
    p = launch_raw(["--root", str(scratch_runtime)], cwd=scratch_runtime.parent)
    try:
        wait_menu(p)
        assert lock_is_held(scratch_runtime)
        press_desktop_button(p, "Exit")
        assert p.wait_exit(10) == 0, p.logs()
        assert not lock_is_held(scratch_runtime)
        assert "Unexpected error" not in p.logs()
    finally:
        p.stop()


# E2E-26
@pytest.mark.e2e
def test_two_clients_from_the_same_runtime_dir_both_join_with_distinct_identities(scratch_runtime):
    server = start_server(scratch_runtime)
    a = start_client(scratch_runtime, server)
    b = start_client(scratch_runtime, server)
    try:
        for c in (a, b):
            assert "PortProgram: main menu ready" in c.logs(grep="main menu ready")
            assert view_name(c) == "GameView"
        ua, ub = username(a), username(b)
        assert ua != ub
        # the random prefix differs; the persona name (after the space) is the same account
        assert ua.split(" ", 1)[1] == ub.split(" ", 1)[1]
        uid_a, uid_b = a.ev("Vexillum.game.Identity.uid.m_SteamID"), b.ev("Vexillum.game.Identity.uid.m_SteamID")
        # Random.Next(1000) draws: equal with probability 1/1000, which would break Client.cs:331 local-player detection
        assert uid_a != uid_b, "uid collision (1/1000): rerun; see notes"
        humans = wait_until(lambda: h if len(h := server_humans(server)) == 2 else None, 10)
        assert {h["name"] for h in humans} == {ua, ub}
        # both processes append to the same debug_client.log (Util.WriteDebugLog opens it in Append mode)
        for c in (a, b):
            c.ev("Vexillum.Util.WriteDebugLog(); \"flushed\"")
        text = (scratch_runtime / "debug_client.log").read_text(errors="ignore")
        assert text.count("PortProgram: cwd=") == 2, text[-800:]
        assert a.alive and b.alive
    finally:
        a.stop()
        b.stop()
        server.stop()
