"""pytest fixtures for Vexillum end-to-end tests.

Everything is built on .claude/mcp/vexillum_dev.py imported as a module (the
same code the MCP tools run): proc_start/proc_stop for the processes, the
debug console (_debug_call) for in-process C# evaluation, and the pure-Python
PNG decoder for screenshot analysis.

Layout of a test::

    @pytest.mark.e2e
    def test_something(server, client):
        me = local_player_state(client)          # position/health/class/weapon on the client
        press(client, "D", hold_ms=300)          # walk right for 300 ms
        wait_until(lambda: local_player_state(client)["x"] > me["x"], timeout=5)
        assert server_player_state(server, me["name"])["class"] == me["class"]

Isolation: every test gets its own scratch copy of Test/ (never Test/ itself),
its own TCP port for the server, its own UDP port for the LAN beacon and its
own debug ports; VEXILLUM_MASTER=off is always set so nothing is published to
the public ntfy registry. The dev tool's process registry is redirected to a
per-session temp folder so interactive `proc_start` sessions are not touched.

Display: the client opens a real 840x630 window. `screenshot()` captures the
whole display (macOS `screencapture`), so the game window must be frontmost
and unobscured for pixel assertions; pass region=(x, y, w, h) to capture only
part of the screen (screen points; on Retina displays the PNG has 2x pixels).
"""
import os
import pathlib
import shutil
import socket
import subprocess
import sys
import tempfile
import time

import pytest

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / ".claude" / "mcp"))
import vexillum_dev as dev  # noqa: E402

# Redirect the dev tool's scratch/process registry to this session.
SESSION_DIR = pathlib.Path(tempfile.mkdtemp(prefix="vexillum-e2e-"))
dev.SCRATCH = SESSION_DIR
dev.PROC_DIR = SESSION_DIR / "proc"
dev.PROC_DIR.mkdir(parents=True, exist_ok=True)

KEEP = os.environ.get("VEXILLUM_E2E_KEEP") == "1"


# ---------------------------------------------------------------- ports / files
_used_ports = set()


def free_tcp_port() -> int:
    """A free TCP port (bind 0), never handed out twice in this session."""
    for _ in range(50):
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
            s.bind(("0.0.0.0", 0))
            port = s.getsockname()[1]
        if port not in _used_ports:
            _used_ports.add(port)
            return port
    raise RuntimeError("no free TCP port")


def free_udp_port() -> int:
    """A free UDP port for VEXILLUM_LAN_PORT."""
    for _ in range(50):
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
            s.bind(("0.0.0.0", 0))
            port = s.getsockname()[1]
        if port not in _used_ports:
            _used_ports.add(port)
            return port
    raise RuntimeError("no free UDP port")


_SKIP_SUFFIX = {".exe", ".log", ".dll", ".pdb"}
_SKIP_NAMES = {"lock", "debug_client.log", "debug_server.log"}


def copy_runtime(dest: pathlib.Path, source: pathlib.Path = dev.RUNTIME_DIR) -> pathlib.Path:
    """Copy Test/ (Content, Maps, Server config, settings.xml, controls.xml) without exes and logs."""
    def ignore(d, names):
        return [n for n in names if n in _SKIP_NAMES or pathlib.Path(n).suffix.lower() in _SKIP_SUFFIX or n in ("bin", "obj")]
    shutil.copytree(source, dest, ignore=ignore)
    return dest


def set_server_setting(runtime: pathlib.Path, key: str, value: str) -> None:
    """Rewrite one key of Server/settings.txt (append when missing), keeping comments."""
    p = runtime / "Server" / "settings.txt"
    lines = p.read_text().split("\n") if p.exists() else []
    found = False
    for i, line in enumerate(lines):
        if line.startswith("#") or not line.strip():
            continue
        if line.split(" ")[0] == key:
            lines[i] = f"{key} {value}"
            found = True
    if not found:
        while lines and not lines[-1].strip():
            lines.pop()
        lines.append(f"{key} {value}")
    p.write_text("\n".join(lines))


def get_server_setting(runtime: pathlib.Path, key: str):
    p = runtime / "Server" / "settings.txt"
    for line in p.read_text().split("\n"):
        if line.startswith("#"):
            continue
        parts = line.split(" ", 1)
        if parts[0] == key:
            return parts[1] if len(parts) > 1 else ""
    return None


# ---------------------------------------------------------------- processes
class EvalError(RuntimeError):
    """The in-process script failed to compile or threw."""

    def __init__(self, target, code, error, log):
        super().__init__(f"eval in {target} failed: {error}\ncode: {code}")
        self.target, self.code, self.error, self.log = target, code, error, log


def ev(target: str, code: str, timeout: int = 15) -> str:
    """Evaluate C# in a running process ('server', 'client', 'client1', ...) and return the result text."""
    r = dev._debug_call(target, code, timeout)
    if not r.get("ok"):
        raise EvalError(target, code, r.get("error", "?"), r.get("log", ""))
    return r.get("result", "")


def wait_until(fn, timeout: float = 10.0, interval: float = 0.1, message: str = ""):
    """Poll fn() until it returns a truthy value (returned) or the timeout passes (TimeoutError)."""
    deadline = time.time() + timeout
    last = None
    while True:
        last = fn()
        if last:
            return last
        if time.time() >= deadline:
            raise TimeoutError(message or f"condition not met within {timeout} s (last value: {last!r})")
        time.sleep(interval)


class Proc:
    """A process started with dev.proc_start; `name` is the proc_start registry name."""

    def __init__(self, name: str, target: str, debug_port: int, runtime: pathlib.Path):
        self.name, self.target, self.debug_port, self.runtime = name, target, debug_port, runtime

    @property
    def alive(self) -> bool:
        m = dev._proc_meta(self.name)
        return bool(m and m["alive"])

    def logs(self, grep: str = "", lines: int = 200) -> str:
        """Tail of the process stdout/stderr (VEXILLUM_LOG_STDOUT=1), optionally filtered by regex."""
        return dev.proc_logs(self.name, lines=lines, grep=grep)

    def wait_log(self, pattern: str, timeout: float = 10.0) -> str:
        """Wait until a log line matches the regex; returns the matching lines."""
        return wait_until(lambda: self.logs(grep=pattern), timeout, message=f"{self.name}: no log line matching {pattern!r} within {timeout} s")

    def ev(self, code: str, timeout: int = 15) -> str:
        return ev(self.name, code, timeout)

    def stop(self) -> str:
        return dev.proc_stop(self.name)


class Server(Proc):
    def __init__(self, port: int, debug_port: int, lan_port: int, runtime: pathlib.Path):
        super().__init__("server", "server", debug_port, runtime)
        self.port, self.lan_port = port, lan_port

    def frame(self) -> int:
        return int(self.ev("Sync(() => Server.level.frame)"))

    def players(self) -> list:
        """Every player as the server sees it: list of dicts (name, class, score, bot, health, x, y, entity_id)."""
        text = self.ev(_SERVER_PLAYERS)
        return [_parse_player(line) for line in text.split("\n") if line.strip()]


class Client(Proc):
    def __init__(self, instance: int, debug_port: int, runtime: pathlib.Path, connect: str):
        super().__init__(dev._client_name(instance), "client", debug_port, runtime)
        self.instance, self.connect = instance, connect


@pytest.fixture
def scratch_runtime():
    """A private copy of Test/ for this test (deleted afterwards unless VEXILLUM_E2E_KEEP=1)."""
    scratch_runtime.counter = getattr(scratch_runtime, "counter", 0) + 1
    rt = copy_runtime(SESSION_DIR / f"rt-{scratch_runtime.counter}")
    yield rt
    if not KEEP:
        shutil.rmtree(rt, ignore_errors=True)


@pytest.fixture
def server(scratch_runtime):
    """The real server on a free port in the scratch runtime, stopped at teardown.

    VEXILLUM_MASTER=off (no public registry), VEXILLUM_LAN_PORT=<free udp>,
    VEXILLUM_DEBUG_PORT=<free tcp>. Edit Server/settings.txt in
    scratch_runtime before requesting this fixture (e.g. via an autouse
    fixture ordered earlier) to change maps/maxbots; keep maxbots at the
    default unless you know docs/PORTING.md's UpdateBots bug.
    """
    port, dbg, lan = free_tcp_port(), free_tcp_port(), free_udp_port()
    saved = {k: os.environ.get(k) for k in ("VEXILLUM_MASTER", "VEXILLUM_LAN_PORT", "VEXILLUM_LAN")}
    os.environ["VEXILLUM_MASTER"] = "off"
    os.environ["VEXILLUM_LAN_PORT"] = str(lan)
    os.environ.setdefault("VEXILLUM_LAN", "on")
    try:
        result = dev.proc_start(target="server", port=port, runtime_dir=str(scratch_runtime), debug_port=dbg, wait_seconds=30)
    finally:
        for k, v in saved.items():
            if v is None:
                os.environ.pop(k, None)
            else:
                os.environ[k] = v
    assert "READY" in result, result
    s = Server(port, dbg, lan, scratch_runtime)
    yield s
    s.stop()


@pytest.fixture
def make_client(server, scratch_runtime):
    """Factory: make_client(instance=0) starts a game window that joins `server` and waits for 'Set terrain state'."""
    started = []

    def factory(instance: int = None, wait_seconds: int = 60) -> Client:
        if instance is None:
            instance = len(started)
        dbg = free_tcp_port()
        os.environ["VEXILLUM_MASTER"] = "off"
        os.environ["VEXILLUM_LAN"] = "off"
        connect = f"127.0.0.1:{server.port}"
        result = dev.proc_start(target="client", connect=connect, runtime_dir=str(scratch_runtime),
                                debug_port=dbg, instance=instance, wait_seconds=wait_seconds)
        c = Client(instance, dbg, scratch_runtime, connect)
        started.append(c)
        assert "READY" in result, result
        # the GameView exists once packet 9 has been processed on the update thread
        wait_until(lambda: c.ev("Game.View == null ? \"none\" : Game.View.GetType().Name") == "GameView", 20,
                   message="client never reached the GameView")
        return c

    yield factory
    for c in started:
        c.stop()


@pytest.fixture
def client(make_client):
    """One game window joined to `server`."""
    return make_client(0)


# ---------------------------------------------------------------- input
def key_down(client: Client, key: str) -> None:
    """Press a key on the client (Microsoft.Xna.Framework.Input.Keys name, e.g. 'D', 'Space', 'R')."""
    client.ev(f'Sync(() => {{ ((GameView)Game.View).KeyPressed(Microsoft.Xna.Framework.Input.Keys.{key}, true); return "ok"; }})')


def key_up(client: Client, key: str) -> None:
    client.ev(f'Sync(() => {{ ((GameView)Game.View).KeyReleased(Microsoft.Xna.Framework.Input.Keys.{key}); return "ok"; }})')


def press(client: Client, key: str, hold_ms: int = 50) -> None:
    """Press, hold for hold_ms (wall clock, the game runs at 60 Hz), release."""
    key_down(client, key)
    time.sleep(hold_ms / 1000.0)
    key_up(client, key)


def mouse_move(client: Client, x: int, y: int) -> None:
    """Move the mouse to window coordinates (840x630 game space)."""
    client.ev(f'Sync(() => {{ ((GameView)Game.View).MouseMove({x}, {y}); return "ok"; }})')


def mouse_down(client: Client, button: str, x: int, y: int) -> None:
    """Move to (x, y) then press button ('Left', 'Middle', 'Right') through GameView.MouseDown."""
    client.ev(f'Sync(() => {{ var v = (GameView)Game.View; v.MouseMove({x}, {y}); v.MouseDown(Nuclex.Input.MouseButtons.{button}); return "ok"; }})')


def mouse_up(client: Client, button: str, x: int, y: int) -> None:
    client.ev(f'Sync(() => {{ var v = (GameView)Game.View; v.MouseMove({x}, {y}); v.MouseUp(Nuclex.Input.MouseButtons.{button}); return "ok"; }})')


def click(client: Client, button: str, x: int, y: int, hold_ms: int = 50) -> None:
    mouse_down(client, button, x, y)
    time.sleep(hold_ms / 1000.0)
    mouse_up(client, button, x, y)


# ---------------------------------------------------------------- state
_INV = "System.Globalization.CultureInfo.InvariantCulture"
_PLAYER_FIELDS = ("name", "class", "weapon", "entity_id", "x", "y", "health", "score", "bot")
_PLAYER_EXPR = (f'p.name + "|" + p.CurrentClass + "|" + p.WeaponIndex + "|" + p.GetID() + "|" + '
                f'(p.Entity != null ? p.Entity.Position.X.ToString({_INV}) : "nan") + "|" + '
                f'(p.Entity != null ? p.Entity.Position.Y.ToString({_INV}) : "nan") + "|" + '
                f'(p.Entity != null ? p.Entity.Health.ToString({_INV}) : "nan") + "|" + p.Score + "|" + p.isBot')
_LOCAL_PLAYER = ('Sync(() => { var v = Game.View as GameView; if (v == null) return "none"; '
                 'var p = (LocalPlayer)Get(v, "player"); if (p == null) return "none"; return ' + _PLAYER_EXPR + '; })')
_SERVER_PLAYERS = ('Sync(() => string.Join("\\n", ((System.Collections.IEnumerable)Server.players).Cast<Player>()'
                   '.Select(p => ' + _PLAYER_EXPR + ')))')


def _parse_player(line: str) -> dict:
    f = line.split("|")
    d = dict(zip(_PLAYER_FIELDS, f))
    d["weapon"] = int(d["weapon"]); d["entity_id"] = int(d["entity_id"]); d["score"] = int(d["score"])
    d["x"], d["y"], d["health"] = float(d["x"]), float(d["y"]), float(d["health"])
    d["bot"] = d["bot"] == "True"
    return d


def local_player_state(client: Client) -> dict:
    """The client's LocalPlayer: name, class, weapon (index), entity_id, x, y, health, score; None before the game view exists."""
    r = client.ev(_LOCAL_PLAYER)
    return None if r == "none" else _parse_player(r)


def server_player_state(server: Server, name: str) -> dict:
    """The server's view of the player called `name` (same fields as local_player_state), or None."""
    for p in server.players():
        if p["name"] == name:
            return p
    return None


# ---------------------------------------------------------------- screenshots
def screenshot(name: str, region=None) -> str:
    """Capture the display to a PNG and return its path (macOS only).

    Without region the whole display is captured (the dev tool's screenshot);
    with region=(x, y, w, h) in screen points only that rectangle is, which is
    also much faster to analyse. The game window must be frontmost.
    """
    if region is None:
        path = dev.screenshot(name)
        assert path.endswith(".png"), path
        return path
    x, y, w, h = region
    path = SESSION_DIR / f"{name}-{int(time.time())}.png"
    subprocess.run(["screencapture", "-x", "-R", f"{x},{y},{w},{h}", str(path)], check=True, timeout=20)
    return str(path)


def png_size(path: str):
    w, h, _ = dev._png_decode_rgba(pathlib.Path(path).read_bytes())
    return w, h


def region_stats(path: str, x: int = 0, y: int = 0, w: int = None, h: int = None) -> dict:
    """Mean colour and non-uniformity of a pixel rectangle of a PNG.

    Returns {"mean": (r, g, b), "stddev": float, "nonuniform": float, "pixels": n}.
    nonuniform is the fraction of pixels whose colour differs from the mean by
    more than 16 on any channel (0.0 = flat colour, e.g. a black window or an
    uncovered background; anything drawn gives a clearly positive value).
    """
    pw, ph, rgba = dev._png_decode_rgba(pathlib.Path(path).read_bytes())
    w = pw - x if w is None else min(w, pw - x)
    h = ph - y if h is None else min(h, ph - y)
    n = w * h
    if n <= 0:
        raise ValueError("empty region")
    sr = sg = sb = 0
    for yy in range(y, y + h):
        base = (yy * pw + x) * 4
        row = rgba[base:base + w * 4]
        sr += sum(row[0::4]); sg += sum(row[1::4]); sb += sum(row[2::4])
    mr, mg, mb = sr / n, sg / n, sb / n
    var = 0.0
    off = 0
    for yy in range(y, y + h):
        base = (yy * pw + x) * 4
        row = rgba[base:base + w * 4]
        for i in range(0, w * 4, 4):
            dr, dg, db = row[i] - mr, row[i + 1] - mg, row[i + 2] - mb
            var += dr * dr + dg * dg + db * db
            if abs(dr) > 16 or abs(dg) > 16 or abs(db) > 16:
                off += 1
    return {"mean": (round(mr, 1), round(mg, 1), round(mb, 1)), "stddev": round((var / (3 * n)) ** 0.5, 2),
            "nonuniform": round(off / n, 4), "pixels": n}


# ---------------------------------------------------------------- session
def pytest_sessionfinish(session, exitstatus):
    try:
        dev.proc_stop("all")
    except Exception as ex:  # never mask the test result with a cleanup error
        print(f"\n[conftest] proc_stop at session end failed: {ex!r}")
    if not KEEP:
        shutil.rmtree(SESSION_DIR, ignore_errors=True)
