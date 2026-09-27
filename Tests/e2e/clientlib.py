"""Client-side e2e helpers on top of conftest.py (new file; conftest.py is untouched).

What it adds over conftest.py:

* ``start_server`` / ``start_client``: the fixtures' logic as plain functions so a
  test can start a *menu* client (no --connect), a client from a *different*
  runtime directory (map download), several clients, or module-scoped
  processes. Every process still gets its own free ports and
  VEXILLUM_MASTER=off.
* ``launch_raw``: the game executable with an arbitrary command line
  (``--help``, ``--bogus``, ...), keeping the Popen so the exit code is known.
* C# snippet builders for the debug console: the Nuclex desktop
  (``desktop``), open windows (``cs_window``), the local entity
  (``entity_state``), the terrain hash (``terrain_hash``), server entities.
* Module-scoped fixtures (``lone_server``/``lone_client``: ``maxbots 1``, so the
  only mover and shooter is the client) for the movement and weapon modules.

Screenshots taken through ``shot`` land in /tmp/vexillum-dev.
"""
import fcntl
import itertools
import json
import os
import pathlib
import re
import shutil
import socket
import subprocess
import time
import uuid

import pytest

import conftest as ct
from conftest import wait_until

dev = ct.dev
SHOT_DIR = pathlib.Path("/tmp/vexillum-dev")
KEYS = "Microsoft.Xna.Framework.Input.Keys"
MB = "Nuclex.Input.MouseButtons"
INV = "System.Globalization.CultureInfo.InvariantCulture"
UNREACHABLE_HOST = "vexillum-e2e-no-such-host.invalid"   # fails DNS in ~50 ms; never a real server
DEFAULT_PORT = 24224   # StreamHelper.DEFAULT_PORT, the game's fallback; tests only ever assert on it, never listen on it

_instances = itertools.count(0)


def next_instance() -> int:
    """A proc_start instance number nobody else in this session uses (client, client1, ...)."""
    return next(_instances)


# ---------------------------------------------------------------- environment
class _Env:
    """Temporarily set environment variables (proc_start copies os.environ)."""

    def __init__(self, **kv):
        self.kv, self.saved = kv, {}

    def __enter__(self):
        for k, v in self.kv.items():
            self.saved[k] = os.environ.get(k)
            if v is None:
                os.environ.pop(k, None)
            else:
                os.environ[k] = str(v)
        return self

    def __exit__(self, *a):
        for k, v in self.saved.items():
            if v is None:
                os.environ.pop(k, None)
            else:
                os.environ[k] = v


def new_runtime(tag: str = "rt") -> pathlib.Path:
    """A fresh private copy of Test/ inside the session folder."""
    return ct.copy_runtime(ct.SESSION_DIR / f"{tag}-{uuid.uuid4().hex[:8]}")


def set_player_list(runtime: pathlib.Path, name: str, players) -> None:
    """Write Server/<name>.txt (ops / banned), one player per line; read once at server start."""
    (runtime / "Server" / f"{name}.txt").write_text("".join(p + "\n" for p in players))


# ---------------------------------------------------------------- processes
class NamedServer(ct.Server):
    """A conftest.Server tracked under its own registry name (server, server1, ...), so a
    module-scoped server and a test's own server can run at the same time."""

    def __init__(self, name, port, debug_port, lan_port, runtime):
        super().__init__(port, debug_port, lan_port, runtime)
        self.name = name


def start_server(runtime: pathlib.Path, lan: str = "on", wait_seconds: int = 30) -> ct.Server:
    """VexillumServer on free ports in `runtime` (VEXILLUM_MASTER=off always). Registered in the
    dev tool's process registry like proc_start does, under the first free name server, server1, ..."""
    port, dbg, lanport = ct.free_tcp_port(), ct.free_tcp_port(), ct.free_udp_port()
    dev.PROC_DIR.mkdir(parents=True, exist_ok=True)
    name = "server"
    for n in itertools.count(1):
        m = dev._proc_meta(name)
        if not (m and m["alive"]):
            break
        name = f"server{n}"
    cmd = dev._find_binary("VexillumServer")
    assert cmd, "VexillumServer is not built"
    cmd = cmd + ["--port", str(port)]
    env = {"VEXILLUM_LOG_STDOUT": "1", "VEXILLUM_INSTANCE": "0", "VEXILLUM_DEBUG_PORT": str(dbg),
           "VEXILLUM_MASTER": "off", "VEXILLUM_LAN": lan, "VEXILLUM_LAN_PORT": str(lanport)}
    log = dev.PROC_DIR / f"{name}.log"
    p = dev._launch(cmd, runtime, log, env)
    meta = {"name": name, "target": "server", "pid": p.pid, "cmd": cmd, "cwd": str(runtime), "log": str(log),
            "debug_port": dbg, "port": port, "started": time.strftime("%Y-%m-%d %H:%M:%S")}
    (dev.PROC_DIR / f"{name}.json").write_text(json.dumps(meta, indent=1))
    ready = dev._wait_for(log, "Ready for connections", wait_seconds, p)
    assert ready and p.poll() is None, f"{name} did not become ready:\n" + dev.tail(log.read_text(errors="ignore"), 30)
    return NamedServer(name, port, dbg, lanport, runtime)


def start_client(runtime: pathlib.Path, server: ct.Server = None, connect: str = None, instance: int = None,
                 lan: str = "off", lan_port: int = None, wait_seconds: int = 60, wait_game: bool = True) -> ct.Client:
    """A game window. With `server` or `connect` it joins (and by default waits for the GameView);
    without either it is a menu client and the call waits for the visible main menu."""
    if instance is None:
        instance = next_instance()
    if connect is None and server is not None:
        connect = f"127.0.0.1:{server.port}"
    dbg = ct.free_tcp_port()
    env = {"VEXILLUM_MASTER": "off", "VEXILLUM_LAN": lan}
    if lan_port is not None:
        env["VEXILLUM_LAN_PORT"] = lan_port
    elif server is not None:
        env["VEXILLUM_LAN_PORT"] = server.lan_port
    with _Env(**env):
        result = dev.proc_start(target="client", connect=connect or "", runtime_dir=str(runtime), debug_port=dbg,
                                instance=instance, wait_seconds=wait_seconds if connect else 20)
    assert "pid" in result and "exited immediately" not in result, result
    c = ct.Client(instance, dbg, runtime, connect or "")
    if connect and wait_game:
        assert "READY" in result, result
        wait_view(c, "GameView", 30)
    elif not connect:
        wait_menu(c)
    return c


def debug_call(port: int, code: str, timeout: int = 15) -> str:
    """Debug-console eval on an explicit port (for processes not tracked by proc_start)."""
    with socket.create_connection(("127.0.0.1", port), timeout=timeout + 5) as sock:
        sock.sendall((json.dumps({"code": code, "timeout": timeout * 1000}) + "\n").encode("utf-8"))
        buf = b""
        while not buf.endswith(b"\n"):
            chunk = sock.recv(65536)
            if not chunk:
                break
            buf += chunk
    r = json.loads(buf.decode("utf-8"))
    if not r.get("ok"):
        raise ct.EvalError(f"port {port}", code, r.get("error", "?"), r.get("log", ""))
    return r.get("result", "")


class RawClient:
    """VexillumGame started with an arbitrary command line; the Popen is kept for the exit code."""

    def __init__(self, popen: subprocess.Popen, log_path: pathlib.Path, cwd: pathlib.Path, debug_port: int):
        self.popen, self.log_path, self.cwd, self.debug_port = popen, log_path, cwd, debug_port
        self.name = f"raw-{popen.pid}"

    @property
    def pid(self):
        return self.popen.pid

    @property
    def alive(self) -> bool:
        return self.popen.poll() is None

    def logs(self, grep: str = "", lines: int = 200) -> str:
        text = self.log_path.read_text(errors="ignore") if self.log_path.exists() else ""
        if grep:
            rx = re.compile(grep, re.I)
            text = "\n".join(l for l in text.splitlines() if rx.search(l))
        return "\n".join(text.splitlines()[-lines:])

    def wait_log(self, pattern: str, timeout: float = 10.0) -> str:
        return wait_until(lambda: self.logs(grep=pattern), timeout, message=f"{self.name}: no log line matching {pattern!r} within {timeout} s")

    def ev(self, code: str, timeout: int = 15) -> str:
        return debug_call(self.debug_port, code, timeout)

    def wait_exit(self, timeout: float = 10.0):
        """Returns the exit code, or None when the process is still running after timeout."""
        try:
            return self.popen.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            return None

    def stop(self) -> None:
        if self.popen.poll() is None:
            try:
                os.killpg(self.popen.pid, 15)
            except ProcessLookupError:
                pass
            try:
                self.popen.wait(timeout=5)
            except subprocess.TimeoutExpired:
                os.killpg(self.popen.pid, 9)
                self.popen.wait(timeout=5)


def launch_raw(args, cwd: pathlib.Path, env_extra: dict = None, debug_port: int = None, log_stdout: bool = True) -> RawClient:
    """Start VexillumGame with `args` (a list) in `cwd`; stdout+stderr go to a log file in the session folder."""
    cmd = dev._find_binary("VexillumGame")
    assert cmd, "VexillumGame is not built"
    env = os.environ.copy()
    env.pop("VEXILLUM_LOG_STDOUT", None)
    env.update({"VEXILLUM_MASTER": "off", "VEXILLUM_LAN": "off"})
    if log_stdout:
        env["VEXILLUM_LOG_STDOUT"] = "1"
    if debug_port is None:
        debug_port = ct.free_tcp_port()
    env["VEXILLUM_DEBUG_PORT"] = str(debug_port)
    if env_extra:
        env.update({k: str(v) for k, v in env_extra.items()})
    log = ct.SESSION_DIR / f"raw-{uuid.uuid4().hex[:8]}.log"
    f = open(log, "w")
    p = subprocess.Popen(cmd + list(args), cwd=str(cwd), stdout=f, stderr=subprocess.STDOUT, env=env, start_new_session=True)
    return RawClient(p, log, cwd, debug_port)


def lock_is_held(runtime: pathlib.Path) -> bool:
    """True while a game process holds `runtime`/lock (FileShare.None is flock() on Unix)."""
    p = runtime / "lock"
    if not p.exists():
        return False
    fd = os.open(str(p), os.O_RDONLY)
    try:
        fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        fcntl.flock(fd, fcntl.LOCK_UN)
        return False
    except OSError:
        return True
    finally:
        os.close(fd)


# ---------------------------------------------------------------- views / GUI
def view_name(client) -> str:
    try:
        return client.ev('Game.View == null ? "none" : Game.View.GetType().Name')
    except (ct.EvalError, RuntimeError, OSError):
        return "none"


def wait_view(client, name: str, timeout: float = 20.0) -> str:
    return wait_until(lambda: view_name(client) == name and name, timeout, message=f"{client.name}: view never became {name} (last: {view_name(client)})")


def menu_visible(client) -> bool:
    try:
        return client.ev("Sync(() => Game.View is MainMenuView && Game.View.Menu != null && Game.View.Menu.visible)") == "True"
    except (ct.EvalError, RuntimeError, OSError):
        return False


def wait_menu(client, timeout: float = 30.0) -> None:
    wait_until(lambda: menu_visible(client), timeout, message=f"{client.name}: main menu never became visible")


CS_CHILDREN = "((System.Collections.IEnumerable)Game.View.GetScreen().Desktop.Children).Cast<Nuclex.UserInterface.Controls.Control>()"
CS_WINDOWS = '((System.Collections.IDictionary)Get(Game.View, "openWindows"))'
_CS_DESC = ('c.GetType().Name + "|" + (c is Nuclex.UserInterface.Controls.Desktop.ButtonControl ? ((Nuclex.UserInterface.Controls.Desktop.ButtonControl)c).Text '
            ': (c.GetType().Name == "ErrorDialog" || c.GetType().Name == "StatusDialog") ? ((Nuclex.UserInterface.Controls.LabelControl)Get(c, "errorLabel")).Text '
            ': c is Nuclex.UserInterface.Controls.Desktop.WindowControl ? ((Nuclex.UserInterface.Controls.Desktop.WindowControl)c).Title : "")')


def cs_window(type_name: str) -> str:
    """C# expression for the open window of that Vexillum.ui type (openWindows[typeof(T)])."""
    return f'{CS_WINDOWS}[TypeOf("Vexillum.ui.{type_name}")]'


def desktop(client) -> list:
    """The Nuclex desktop children as [(type name, text)], text = button text / dialog label / window title."""
    r = client.ev(f'Sync(() => string.Join("\\n", {CS_CHILDREN}.Select(c => {_CS_DESC})))')
    return [tuple(line.split("|", 1)) for line in r.split("\n") if line]


def dialog_texts(client, type_name: str) -> list:
    return [t for (k, t) in desktop(client) if k == type_name]


def buttons(client) -> dict:
    """Bottom/menu ButtonControls on the desktop: text -> (x, y, w, h)."""
    r = client.ev(f'Sync(() => string.Join("\\n", {CS_CHILDREN}.OfType<Nuclex.UserInterface.Controls.Desktop.ButtonControl>()'
                  f'.Select(b => b.Text + "|" + b.Bounds.Left.Offset + "|" + b.Bounds.Top.Offset + "|" + b.Bounds.Size.X.Offset + "|" + b.Bounds.Size.Y.Offset)))')
    out = {}
    for line in r.split("\n"):
        if line:
            t, x, y, w, h = line.split("|")
            out[t] = (float(x), float(y), float(w), float(h))
    return out


def press_desktop_button(client, text: str) -> None:
    """Invoke the Pressed handler of the desktop ButtonControl with that text (Menu bottom buttons)."""
    client.ev(f'Sync(() => {{ var b = {CS_CHILDREN}.OfType<Nuclex.UserInterface.Controls.Desktop.ButtonControl>().First(x => x.Text == "{text}"); Call(b, "OnPressed"); return "ok"; }})')


def press_window_button(client, window_type: str, field: str) -> None:
    """Invoke the Pressed handler of a private ButtonControl field of an open dialog."""
    client.ev(f'Sync(() => {{ Call(Get({cs_window(window_type)}, "{field}"), "OnPressed"); return "ok"; }})')


def window_open(client, window_type: str) -> bool:
    return client.ev(f'Sync(() => {CS_WINDOWS}.Contains(TypeOf("Vexillum.ui.{window_type}")) && ((Nuclex.UserInterface.Controls.Desktop.WindowControl){cs_window(window_type)}).IsOpen)') == "True"


def menu_click(client, x: int, y: int) -> None:
    """Left click at window coordinates through the view (menu item hit test), not the Nuclex GUI."""
    client.ev(f'Sync(() => {{ Game.View.MouseMove({x}, {y}); Game.View.MouseDown({MB}.Left); return "ok"; }})')


def set_username(client, name: str) -> None:
    """Rename the local identity before connecting (Identity.username is what the login packet carries)."""
    client.ev(f'Sync(() => {{ Vexillum.game.Identity.username = "{name}"; return Vexillum.game.Identity.username; }})')


def username(client) -> str:
    return client.ev("Vexillum.game.Identity.username")


def connect(client, host: str, port: int) -> None:
    """Vexillum.Connect: what the Join/Connect buttons call."""
    client.ev(f'Sync(() => {{ Game.Connect("{host}", {port}); return "ok"; }})')


def chat(client, msg: str) -> None:
    client.ev(f'Sync(() => {{ ((LocalPlayer)Get(Game.View, "player")).SendChat("{msg}"); return "ok"; }})')


def local_name(client) -> str:
    return client.ev('Sync(() => ((LocalPlayer)Get(Game.View, "player")).name)')


# ---------------------------------------------------------------- entity state
CS_PLAYER = 'var v = (GameView)Game.View; var p = (LocalPlayer)Get(v, "player"); var e = p.Entity;'
_CS_STATE = (
    'Func<float, string> F = f => f.ToString(' + INV + '); '
    'return "name=" + p.name + "|class=" + p.CurrentClass + "|weapon=" + (e.Weapon == null ? "" : e.Weapon.GetType().Name)'
    ' + "|weaponIndex=" + p.WeaponIndex + "|x=" + F(e.Position.X) + "|y=" + F(e.Position.Y) + "|vx=" + F(e.Velocity.X) + "|vy=" + F(e.Velocity.Y)'
    ' + "|xVelocity=" + F(e.xVelocity) + "|speed=" + F(e.Speed) + "|moving=" + e.moving + "|direction=" + e.direction + "|jumping=" + e.jumping'
    ' + "|movementChanged=" + e.movementChanged + "|ladder=" + e.ladder + "|ladderDirection=" + e.ladderDirection'
    ' + "|hook=" + (e.hook == null ? "" : e.hook.GetType().Name + "#" + e.hook.ID) + "|canGrapple=" + p.canGrapple + "|armAngle=" + F(p.ArmAngle)'
    ' + "|clip=" + e.GetClipAmmo() + "|maxClip=" + e.GetMaxClipAmmo() + "|total=" + e.GetTotalAmmo() + "|health=" + F(e.Health)'
    ' + "|frame=" + v.Level.frame + "|camX=" + F(v.CamPosition.X) + "|camY=" + F(v.CamPosition.Y) + "|hurtFrame=" + p.hurtFrame'
    ' + "|alive=" + p.IsAlive() + "|entityId=" + e.ID + "|paused=" + v.IsPaused() + "|showChatBar=" + Get(v, "showChatBar")'
    ' + "|showScoreboard=" + Get(v, "showScoreboard") + "|chat=" + Get(v, "chat") + "|map=" + v.Level.ShortName;'
)


def _parse_kv(text: str) -> dict:
    out = {}
    for part in text.split("|"):
        k, _, val = part.partition("=")
        if val in ("True", "False"):
            out[k] = val == "True"
        else:
            try:
                out[k] = int(val)
            except ValueError:
                try:
                    out[k] = float(val)
                except ValueError:
                    out[k] = val
    return out


def entity_state(client, pre: str = "") -> dict:
    """The local player's entity as a dict (x, y, vx, vy, xVelocity, jumping, hook, clip, ...), read on the update thread.
    `pre` is C# run before the read in the same update tick (e.g. a key press), so the read is atomic with it."""
    return _parse_kv(client.ev(f"Sync(() => {{ {CS_PLAYER} {pre} {_CS_STATE} }})"))


def act(client, code: str) -> dict:
    """Run C# on the update thread with v/p/e in scope, then return entity_state (same tick)."""
    return entity_state(client, code)


def key(name: str) -> str:
    return f"{KEYS}.{name}"


def cs_press(name: str) -> str:
    return f"v.KeyPressed({key(name)}, true);"


def cs_release(name: str) -> str:
    return f"v.KeyReleased({key(name)});"


def terrain_hash(proc) -> str:
    """SHA-256 of Level.GetTerrainState() (TerrainArray.ToBytes) on a client or the server."""
    if isinstance(proc, ct.Server):
        return proc.ev('Sync(() => { var s = (global::Server.Server)Server; byte[] b = s.level.GetTerrainState(); return BitConverter.ToString(System.Security.Cryptography.SHA256.HashData(b)).Replace("-", "").ToLowerInvariant(); })')
    return proc.ev('Sync(() => { var v = (GameView)Game.View; byte[] b = v.Level.GetTerrainState(); return BitConverter.ToString(System.Security.Cryptography.SHA256.HashData(b)).Replace("-", "").ToLowerInvariant(); })')


def terrain_bits(proc, x: int, y: int, w: int, h: int) -> str:
    """The terrain bits of a rectangle as a '01' string (row-major), identical logic on both sides."""
    body = ('var sb = new System.Text.StringBuilder(); for (int yy = %d; yy < %d; yy++) for (int xx = %d; xx < %d; xx++) sb.Append(t.GetTerrain(xx, yy) ? "1" : "0"); return sb.ToString();'
            % (y, y + h, x, x + w))
    if isinstance(proc, ct.Server):
        return proc.ev('Sync(() => { var t = ((global::Server.Server)Server).level.terrain; ' + body + ' })')
    return proc.ev('Sync(() => { var t = ((GameView)Game.View).Level.terrain; ' + body + ' })')


def client_entities(client) -> list:
    r = client.ev('Sync(() => string.Join("\\n", ((GameView)Game.View).Level.getEntities().Select(e => e.GetType().Name + "|" + e.ID + "|" + e.Position.X.ToString(' + INV + ') + "|" + e.Position.Y.ToString(' + INV + ') + "|" + (e.player != null ? e.player.name : ""))))')
    return _parse_entities(r)


def server_entities(server) -> list:
    r = server.ev('Sync(() => string.Join("\\n", ((global::Server.Server)Server).level.getEntities().Select(e => e.GetType().Name + "|" + e.ID + "|" + e.Position.X.ToString(' + INV + ') + "|" + e.Position.Y.ToString(' + INV + ') + "|" + (e.player != null ? e.player.name : ""))))')
    return _parse_entities(r)


def _parse_entities(text: str) -> list:
    out = []
    for line in text.split("\n"):
        if not line:
            continue
        t, i, x, y, owner = line.split("|")
        out.append({"type": t, "id": int(i), "x": float(x), "y": float(y), "player": owner})
    return out


_CS_SERVER_PLAYER = (
    'Sync(() => { var p = ((System.Collections.IEnumerable)Server.players).Cast<Player>().FirstOrDefault(x => x.name == "{NAME}"); if (p == null) return "none"; var e = p.Entity; '
    'return "class=" + p.CurrentClass + "|weaponIndex=" + p.WeaponIndex + "|weapon=" + (e.Weapon == null ? "" : e.Weapon.GetType().Name) + "|armAngle=" + p.ArmAngle.ToString(' + INV + ')'
    ' + "|x=" + e.Position.X.ToString(' + INV + ') + "|y=" + e.Position.Y.ToString(' + INV + ') + "|hook=" + (e.hook == null ? "" : e.hook.GetType().Name + "#" + e.hook.ID)'
    ' + "|isOp=" + Get(p, "isOp") + "|health=" + e.Health.ToString(' + INV + ') + "|score=" + p.Score + "|entityId=" + e.ID; })')


def server_player(server, name: str) -> dict:
    """The server-side player: class, weaponIndex, weapon, armAngle, x, y, hook, isOp, health."""
    r = server.ev(_CS_SERVER_PLAYER.replace("{NAME}", name))
    return None if r == "none" else _parse_kv(r)


def server_humans(server) -> list:
    return [p for p in server.players() if not p["bot"]]


# ---------------------------------------------------------------- pixels
def backbuffer_pixels(client, x: int, y: int, w: int, h: int) -> list:
    """RGBA tuples of a back-buffer rectangle read on the game thread (no screen capture needed)."""
    r = client.ev(f'Sync(() => {{ var gd = (Microsoft.Xna.Framework.Graphics.GraphicsDevice)Game.GraphicsDevice; var d = new Microsoft.Xna.Framework.Color[{w * h}]; '
                  f'gd.GetBackBufferData<Microsoft.Xna.Framework.Color>(new Microsoft.Xna.Framework.Rectangle({x}, {y}, {w}, {h}), d, 0, d.Length); '
                  f'return string.Join(",", d.Select(c => c.R + ":" + c.G + ":" + c.B + ":" + c.A)); }})')
    return [tuple(int(v) for v in px.split(":")) for px in r.split(",") if px]


def window_region(client):
    """(x, y, w, h) of the game window in screen points, for screencapture -R."""
    r = client.ev('Sync(() => Game.Window.Position.X + "," + Game.Window.Position.Y + "," + Game.Window.ClientBounds.Width + "," + Game.Window.ClientBounds.Height)')
    return tuple(int(v) for v in r.split(","))


def shot(name: str, region) -> str:
    """screencapture of a screen region (points) into /tmp/vexillum-dev; returns the PNG path."""
    SHOT_DIR.mkdir(parents=True, exist_ok=True)
    x, y, w, h = region
    path = SHOT_DIR / f"e2e-{name}-{int(time.time())}.png"
    subprocess.run(["screencapture", "-x", "-R", f"{x},{y},{w},{h}", str(path)], check=True, timeout=20)
    return str(path)


# ---------------------------------------------------------------- fixtures
@pytest.fixture(scope="module")
def mod_runtime():
    rt = new_runtime("mrt")
    yield rt
    if not ct.KEEP:
        shutil.rmtree(rt, ignore_errors=True)


@pytest.fixture(scope="module")
def lone_server(mod_runtime):
    """A server with `maxbots 1` (no bot ever spawns for one human; see docs/TESTING.md), module-scoped."""
    ct.set_server_setting(mod_runtime, "maxbots", "1")
    ct.set_server_setting(mod_runtime, "maps", "bases")     # the server starts on a random map of the list
    s = start_server(mod_runtime)
    yield s
    s.stop()


@pytest.fixture(scope="module")
def lone_client(lone_server, mod_runtime):
    """The single game window joined to lone_server, module-scoped. Tests must leave it idle
    (keys released, unpaused, chat bar closed) — use `settle`."""
    c = start_client(mod_runtime, lone_server)
    wait_until(lambda: ct.local_player_state(c), 20, message="no local player")
    yield c
    c.stop()


def settle(client) -> dict:
    """Release the movement keys, close the chat bar, unpause, select weapon 0 and wait until the
    entity stands still; returns entity_state."""
    st = act(client, ('v.KeyReleased(%s.A); v.KeyReleased(%s.D); v.KeyReleased(%s.W); v.KeyReleased(%s.S); v.KeyReleased(%s.Tab); '
                      'if (v.IsPaused()) v.Unpause(); Set(v, "showChatBar", false); p.SelectWeapon(0); '
                      'if (e.hook != null) { e.SetGrapplingHook(null); } ') % ((KEYS,) * 5))
    wait_until(lambda: abs(entity_state(client)["vx"]) < 0.01 and entity_state(client)["vy"] > -0.4 and not entity_state(client)["jumping"], 10,
               message="entity never came to rest")
    return entity_state(client)
