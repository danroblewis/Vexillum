#!/usr/bin/env python3
"""vexillum-dev: build, audit, content and runtime tooling for the Vexillum port.

Two ways to use it:

  MCP server (stdio):   uv run --with mcp python .claude/mcp/vexillum_dev.py
  Command line:         python3 .claude/mcp/vexillum_dev.py <tool> [key=value ...]
                        python3 .claude/mcp/vexillum_dev.py help

The CLI mode needs only the standard library, so agents can call every tool
through Bash even when the MCP server is not connected in their session.

Tools: build, port_audit, invariant_check, preservation_check, runtime_status,
run_server, run_client, smoke_test, read_log, map_info, extract_map,
create_map, terrain_reference, xnb_info, decompile,
proc_start, proc_stop, proc_status, proc_logs, screenshot, eval, probe
(the last group manages long-running processes and the in-process C# debug console).
"""
import glob
import io
import json
import lzma
import os
import re
import shutil
import signal
import struct
import subprocess
import sys
import time
import zlib
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ORIGINAL_COMMIT = "370aa81"          # the author's initial commit
RUNTIME_DIR = ROOT / "Test"
SCRATCH = Path(os.environ.get("VEXILLUM_SCRATCH", "/tmp/vexillum-dev"))

HISTORICAL_DIRS = ["Game", "Server", "ZombieSurvival", "MapTool", "CreateMap.cs",
                   "ExtractMap", "ServerStart", "Platform", "ZombieSurvivalContent"]
SKIP_DIRS = {"bin", "obj", ".git", ".claude", "docs", "Test", "dlls", "lib", "Lzma", "misc", "Shims"}

# (name, why it matters, regex).  Matched against comment-stripped C# source.
HAZARDS = [
    ("System.Drawing", "GDI+ imaging, Windows-only on .NET 6+; must resolve to the shim", r"System\.Drawing"),
    ("System.Windows.Forms", "WinForms; must resolve to the shim", r"System\.Windows\.Forms"),
    ("Nuclex", "Nuclex Input/UserInterface; must resolve to the source port", r"\bNuclex\b"),
    ("Steamworks", "Steamworks.NET; must resolve to the offline shim", r"\bSteamworks\b|\bCSteamID\b|\bSteamAPI\b|\bSteamUser\b|\bSteamFriends\b|\bSteamUtils\b"),
    ("SlimDX", "DirectInput, Windows-only native; must not be referenced", r"\bSlimDX\b"),
    ("Awesomium", "dead embedded browser; all uses are commented out", r"\bAwesomium\b"),
    ("DllImport", "P/Invoke into Windows DLLs", r"\bDllImport\b"),
    ("XNA GamerServices", "namespace absent from MonoGame; shim namespace needed", r"Xna\.Framework\.GamerServices"),
    ("XNA Storage", "namespace absent from MonoGame; shim namespace needed", r"Xna\.Framework\.Storage"),
    ("XNA Net", "namespace absent from MonoGame; shim namespace needed", r"Xna\.Framework\.Net\b"),
    ("#if WINDOWS", "platform ifdef (author's WINDOWS||XBOX in Program.cs is expected)", r"#if\s+WINDOWS"),
    ("Process.Start", "launches sibling .exe files by name", r"Process\.Start"),
]
CSPROJ_HAZARDS = [
    ("legacy csproj", "not SDK-style; cannot build with dotnet on this machine", r"ToolsVersion=\"(4|12)\.0\""),
    ("dlls/ reference", "prebuilt XNA-bound binaries", r"HintPath>[^<]*dlls[\\/]"),
    ("lib/MonoGame reference", "3.7.1 loose binary from the 2025 attempt", r"HintPath>[^<]*lib[\\/]MonoGame"),
    ("XNA GameStudio targets", "XNA build targets", r"Microsoft\.Xna\.GameStudio"),
    ("XNA 4.0 reference", "strong-named XNA assembly", r"Microsoft\.Xna\.Framework[^\"]*Version=4\.0"),
    ("absolute Windows path", "author's machine path", r"[A-Z]:\\\\"),
]

# ---- expected invariants (docs/PROTOCOL.md) --------------------------------
EXPECTED_ENTITY_TYPES = [
    "Vexillum.Entities.BlueFlagEntity", "Vexillum.Entities.CrateEntity",
    "Vexillum.Entities.GrapplingHook", "Vexillum.Entities.GreenFlagEntity",
    "Vexillum.Entities.HumanoidEntity", "Vexillum.Entities.Rocket",
    "Vexillum.Entities.Weapons.RocketLauncher", "Vexillum.Entities.NullEntity",
    "Vexillum.Entities.Weapons.ClusterBombLauncher", "Vexillum.Entities.Weapons.SMG",
    "Vexillum.Entities.Weapons.Sword", "Vexillum.Entities.PixelEntity",
    "Vexillum.Entities.ClusterBomb", "Vexillum.Entities.Bomblet",
]
EXPECTED_ENUMS = {
    "PlayerClass": ["Green", "Blue", "Spectator", "None"],
    "KeyAction": ["None", "Move_Left", "Move_Right", "Move_Down", "Jump", "Reload",
                  "Chat", "SendChat", "Pause", "GrapplingHook", "Show_Scoreboard"],
    "Sounds": ["WALK1", "WALK2", "WALK3", "WALK4", "WALK5", "ROCKET", "EXPLOSION",
               "SMG", "CLICK", "CLICK2", "SWORD1", "SWORD2", "SWORD3"],
}
EXPECTED_CONSTS = {  # (file glob, regex) -> expected text
    ("Constants.cs", r"FRAME_RATE\s*=\s*(\d+)"): "60",
    ("Constants.cs", r"PROTOCOL_VERSION\s*=\s*(\d+)"): "3",
    ("Constants.cs", r"DEFAULT_PORT\s*=\s*(\d+)"): "24224",
    ("Constants.cs", r"MAX_PING\s*=\s*(\d+)"): "5000",
    ("LevelLoader.cs", r"MagicNumber\s*=\s*(0x[0-9A-Fa-f]+)"): "0x004F876B",
    ("MapUtil.cs", r"magic\s*=\s*(0x[0-9A-Fa-f]+)"): "0x004F876B",
    ("Vexillum.cs", r"GameWidth\s*=\s*(\d+)"): "840",
    ("Vexillum.cs", r"GameHeight\s*=\s*(\d+)"): "630",
    ("Vexillum.cs", r"const int Scale\s*=\s*(\d+)"): "1",
    ("Level.cs", r"Gravity\s*=\s*([0-9.]+)f"): "0.3",
    ("Level.cs", r"friction\s*=\s*([0-9.]+)f"): "0.5",
    ("TerrainArray.cs", r"SetShort0\(x, y, (t \| \(c << 4\) \| \(l << 3\))\)"): "t | (c << 4) | (l << 3)",
}
MAP_MAGIC = 0x004F876B
MAP_FILE_ORDER = ["main.jpg", "background.jpg", "collision.png", "data.txt",
                  "sky.jpg", "left.png", "right.png", "bottom.jpg"]


# ---- helpers ----------------------------------------------------------------
def rel(p: Path | str) -> str:
    try:
        return str(Path(p).resolve().relative_to(ROOT))
    except ValueError:
        return str(p)


def strip_comments(src: str) -> str:
    """Remove // and /* */ comments but keep line structure."""
    out, i, n = [], 0, len(src)
    while i < n:
        c = src[i]
        if c == '"':
            j = i + 1
            while j < n and src[j] != '"':
                j += 2 if src[j] == '\\' else 1
            out.append(src[i:j + 1]); i = j + 1
        elif src.startswith("//", i):
            j = src.find("\n", i)
            i = n if j == -1 else j
        elif src.startswith("/*", i):
            j = src.find("*/", i + 2)
            j = n if j == -1 else j + 2
            out.append("\n" * src[i:j].count("\n")); i = j
        else:
            out.append(c); i += 1
    return "".join(out)


def source_files(paths: list[str] | None = None, exts=(".cs",)) -> list[Path]:
    bases = [ROOT / p for p in paths] if paths else [ROOT]
    files: list[Path] = []
    for base in bases:
        if base.is_file():
            files.append(base); continue
        for dp, dns, fns in os.walk(base):
            dns[:] = [d for d in dns if d not in SKIP_DIRS]
            for fn in fns:
                if fn.endswith(exts):
                    files.append(Path(dp) / fn)
    return sorted(files)


def find_file(name: str) -> Path | None:
    hits = [p for p in ROOT.rglob(name)
            if not any(part in {"bin", "obj", "Test", ".git"} for part in p.parts)]
    return hits[0] if hits else None


def run(cmd: list[str], cwd: Path | None = None, timeout: int = 600, env: dict | None = None) -> tuple[int, str]:
    e = os.environ.copy()
    if env:
        e.update(env)
    try:
        p = subprocess.run(cmd, cwd=str(cwd or ROOT), capture_output=True, text=True,
                           timeout=timeout, env=e)
        return p.returncode, (p.stdout or "") + (p.stderr or "")
    except FileNotFoundError as ex:
        return 127, f"not found: {ex}"
    except subprocess.TimeoutExpired:
        return 124, f"timeout after {timeout}s: {' '.join(cmd)}"


def tail(text: str, n: int) -> str:
    lines = text.splitlines()
    return "\n".join(lines[-n:]) if len(lines) > n else text


def read_7bit(r: io.BytesIO) -> int:
    n = shift = 0
    while True:
        b = r.read(1)
        if not b:
            raise EOFError("unexpected end of stream")
        n |= (b[0] & 0x7F) << shift
        shift += 7
        if not b[0] & 0x80:
            return n


def write_7bit(w: io.BytesIO, n: int) -> None:
    while n >= 0x80:
        w.write(bytes([(n & 0x7F) | 0x80])); n >>= 7
    w.write(bytes([n]))


def read_bstring(r: io.BytesIO) -> str:
    return r.read(read_7bit(r)).decode("utf-8")


def write_bstring(w: io.BytesIO, s: str) -> None:
    b = s.encode("utf-8"); write_7bit(w, len(b)); w.write(b)


def image_size(data: bytes) -> str:
    if data[:8] == b"\x89PNG\r\n\x1a\n":
        w, h = struct.unpack(">II", data[16:24]); bit, ctype = data[24], data[25]
        return f"PNG {w}x{h} depth={bit} colortype={ctype}"
    if data[:2] == b"\xff\xd8":
        i = 2
        while i + 9 < len(data):
            if data[i] != 0xFF:
                i += 1; continue
            m = data[i + 1]
            if m in (0xC0, 0xC1, 0xC2, 0xC3, 0xC5, 0xC6, 0xC7, 0xC9, 0xCA, 0xCB, 0xCD, 0xCE, 0xCF):
                h, w = struct.unpack(">HH", data[i + 5:i + 9]); return f"JPEG {w}x{h}"
            if m in (0xD8, 0x01) or 0xD0 <= m <= 0xD7:
                i += 2; continue
            seg = struct.unpack(">H", data[i + 2:i + 4])[0]; i += 2 + seg
        return "JPEG ?x?"
    return "not an image"


# ---- tool implementations ---------------------------------------------------
def build(target: str = "", configuration: str = "Debug", max_items: int = 40, extra_args: str = "") -> str:
    """Run `dotnet build` and return a grouped, de-duplicated error summary.

    target: path to a .sln or .csproj relative to the repo root (default:
    Vexillum.sln if it exists). Returns success flag, counts by error code,
    the first `max_items` distinct errors as file:line: CODE message, and a
    note when the target still uses legacy (non-SDK) project files.
    """
    t = ROOT / (target or "Vexillum.sln")
    if not t.exists():
        return f"BUILD: target not found: {rel(t)}"
    legacy = []
    projs = [t] if t.suffix == ".csproj" else [
        ROOT / m.replace("\\", "/") for m in re.findall(r'"([^"]+\.csproj)"', t.read_text(errors="ignore"))]
    for p in projs:
        if p.exists() and "<Project Sdk=" not in p.read_text(errors="ignore")[:400]:
            legacy.append(rel(p))
    cmd = ["dotnet", "build", str(t), "-c", configuration, "-nologo", "-v:q", "-clp:NoSummary"]
    if extra_args:
        cmd += extra_args.split()
    code, out = run(cmd, timeout=900)
    pat = re.compile(r"^(?P<file>.*?)(?:\((?P<line>\d+),(?P<col>\d+)\))?:\s*(?P<kind>error|warning)\s+(?P<code>[A-Z]+\d+):\s*(?P<msg>.*?)(?:\s+\[(?P<proj>[^\]]+)\])?\s*$")
    errors, warnings = {}, {}
    for line in out.splitlines():
        m = pat.match(line.strip())
        if not m:
            continue
        key = (m["file"], m["line"], m["code"], m["msg"][:160])
        (errors if m["kind"] == "error" else warnings)[key] = m
    by_code = Counter(k[2] for k in errors)
    by_file = Counter(rel(k[0]) for k in errors)
    lines = [f"BUILD: {'SUCCESS' if code == 0 and not errors else 'FAILED'} (exit {code}) target={rel(t)} config={configuration}",
             f"errors={len(errors)} warnings={len(warnings)}"]
    if legacy:
        lines.append(f"LEGACY (non-SDK) projects: {', '.join(legacy)} -> expected to fail with MSB3644 until PORTING step 1")
    if by_code:
        lines.append("by code: " + ", ".join(f"{c}x{n}" for c, n in by_code.most_common()))
        lines.append("by file: " + ", ".join(f"{f}({n})" for f, n in by_file.most_common(15)))
    for i, (k, m) in enumerate(errors.items()):
        if i >= max_items:
            lines.append(f"... {len(errors) - max_items} more"); break
        loc = f"{rel(k[0])}:{k[1]}" if k[1] else rel(k[0])
        lines.append(f"  {loc}: {k[2]} {m['msg'][:200]}")
    if not errors and code != 0:
        lines.append(tail(out, 30))
    return "\n".join(lines)


def port_audit(paths: list[str] | None = None) -> str:
    """Count Windows-only / XNA-only API usage per file (comments ignored).

    paths: optional list of repo-relative files or directories to narrow the
    scan. Historical source dirs are scanned; Shims/, Lzma/, util/misc are not.
    A hazard in a historical file is fine as long as a shim resolves it; the
    summary therefore also reports whether the Shims/ project exists.
    """
    hits: dict[str, dict[str, int]] = defaultdict(dict)
    for f in source_files(paths):
        src = strip_comments(f.read_text(errors="ignore"))
        for name, _, rx in HAZARDS:
            n = len(re.findall(rx, src))
            if n:
                hits[name][rel(f)] = n
    proj_hits: dict[str, dict[str, int]] = defaultdict(dict)
    for f in source_files(paths, exts=(".csproj", ".contentproj", ".sln")):
        src = f.read_text(errors="ignore")
        for name, _, rx in CSPROJ_HAZARDS:
            n = len(re.findall(rx, src))
            if n:
                proj_hits[name][rel(f)] = n
    total = sum(sum(v.values()) for v in hits.values())
    out = [f"PORT AUDIT: {total} source hazards in {len({f for v in hits.values() for f in v})} files; "
           f"{sum(len(v) for v in proj_hits.values())} project-file hazards",
           f"Shims project present: {(ROOT / 'Shims').is_dir()}"]
    for name, why, _ in HAZARDS:
        files = hits.get(name, {})
        if not files:
            continue
        out.append(f"\n[{name}] {len(files)} files, {sum(files.values())} hits -- {why}")
        for f, n in sorted(files.items(), key=lambda kv: -kv[1]):
            out.append(f"  {f}: {n}")
    for name, why, _ in CSPROJ_HAZARDS:
        files = proj_hits.get(name, {})
        if files:
            out.append(f"\n[{name}] -- {why}: " + ", ".join(sorted(files)))
    return "\n".join(out)


def invariant_check() -> str:
    """Verify wire-format and gameplay constants in source against docs/PROTOCOL.md.

    Checks the StreamHelper entity-type table, enum declaration orders,
    protocol/port/frame/map constants, terrain bit packing, little-endian
    readers on both ends, and that every wire entity type still exists with a
    parameterless constructor. Any MISMATCH line is a blocker.
    """
    res = []

    def ok(label, cond, detail=""):
        res.append(f"{'OK      ' if cond else 'MISMATCH'} {label}{(' -- ' + detail) if detail and not cond else ''}")

    sh = find_file("StreamHelper.cs")
    if sh:
        m = re.search(r"entityTypes\s*=\s*new List<string>\(new string\[\]\s*\{(.*?)\}\)", sh.read_text(), re.S)
        got = re.findall(r'"([^"]+)"', m.group(1)) if m else []
        ok("StreamHelper.entityTypes order", got == EXPECTED_ENTITY_TYPES, f"got {got}")
    else:
        ok("StreamHelper.cs present", False)
    for enum, expected in EXPECTED_ENUMS.items():
        found = None
        for f in source_files():
            m = re.search(r"enum\s+" + enum + r"\s*\{(.*?)\}", strip_comments(f.read_text(errors="ignore")), re.S)
            if m:
                found = [x.strip().split("=")[0].strip() for x in m.group(1).split(",") if x.strip()]
                break
        ok(f"enum {enum} order", found == expected, f"got {found}")
    for (fname, rx), exp in EXPECTED_CONSTS.items():
        f = find_file(fname)
        if not f:
            ok(f"{fname} present", False); continue
        m = re.search(rx, f.read_text())
        ok(f"{fname} {rx.split(chr(92))[0].strip()}", bool(m) and m.group(1) == exp, f"got {m.group(1) if m else None}")
    for fname in ("Client.cs", "ServerPlayer.cs"):
        f = find_file(fname)
        if f and "net" in str(f) or f and "Server" in str(f):
            src = strip_comments(f.read_text())
            ok(f"{fname} uses EndianBitConverter.Little", "EndianBitConverter.Little" in src and "EndianBitConverter.Big" not in src)
        else:
            ok(f"{fname} present", False)
    gmc = find_file("GameModeCommand.cs")
    if gmc:
        vals = re.findall(r"const byte (\w+) = (\d+);", gmc.read_text())
        ok("GameModeCommand values", [v for _, v in vals] == [str(i) for i in range(len(vals))] and len(vals) == 8, f"got {vals}")
    # entity types exist with parameterless ctors
    allsrc = {f: strip_comments(f.read_text(errors="ignore")) for f in source_files()}
    for full in EXPECTED_ENTITY_TYPES:
        name = full.split(".")[-1]
        if name == "NullEntity":
            continue
        ns = ".".join(full.split(".")[:-1])
        defs = [f for f, s in allsrc.items() if re.search(r"\bclass\s+" + name + r"\b", s) and ("namespace " + ns) in s.replace("namespace  ", "namespace ")]
        if not defs:
            ok(f"type {full} exists in namespace", False); continue
        s = allsrc[defs[0]]
        ctors = re.findall(r"public\s+" + name + r"\s*\(([^)]*)\)", s)
        ok(f"type {full} parameterless ctor", not ctors or any(not c.strip() for c in ctors), f"ctors: {ctors}")
    bad = [r for r in res if r.startswith("MISMATCH")]
    return f"INVARIANTS: {'OK' if not bad else str(len(bad)) + ' MISMATCH'}\n" + "\n".join(res)


def preservation_check(base: str = ORIGINAL_COMMIT, show_diff: bool = False) -> str:
    """Compare historical source files against the author's original commit.

    Lists every .cs file under the historical directories that differs from
    `base` (default 370aa81, the initial commit), and for each, the changed
    lines that lack a `// PORT:` marker. Deleted or added files are listed
    too. Vendored code (Lzma/, util/misc/) and Shims/ are ignored.
    """
    code, names = run(["git", "diff", "--name-status", base, "--", *HISTORICAL_DIRS])
    if code != 0:
        return "PRESERVATION: git diff failed\n" + names
    out, violations = [], 0
    for line in names.splitlines():
        parts = line.split("\t")
        status, path = parts[0], parts[-1]
        if not path.endswith(".cs") or "/util/misc/" in path:
            continue
        if status.startswith("D"):
            out.append(f"DELETED   {path}"); violations += 1; continue
        if status.startswith("A"):
            out.append(f"ADDED     {path} (new file in a historical dir; should it live in Shims/?)"); continue
        _, diff = run(["git", "diff", "-U0", base, "--", path])
        added = [l[1:] for l in diff.splitlines() if l.startswith("+") and not l.startswith("+++")]
        removed = [l[1:] for l in diff.splitlines() if l.startswith("-") and not l.startswith("---")]
        unmarked = [l for l in added if "// PORT:" not in l]
        flag = "OK   " if not unmarked and not removed or all("// PORT:" in l for l in added) else "EDIT "
        if flag == "EDIT ":
            violations += 1
        out.append(f"{flag}     {path}: +{len(added)} -{len(removed)} lines, {len(unmarked)} added lines without '// PORT:'")
        if show_diff:
            out.append(diff)
    return f"PRESERVATION vs {base}: {violations} file(s) with unmarked edits or deletions, {len(out)} file(s) differ\n" + "\n".join(out)


def runtime_status() -> str:
    """Report toolchain, solution/projects, built binaries and runtime dir state.

    The right first call in a fresh session: tells you whether the SDK-style
    solution exists yet, which projects are legacy, what has been built and
    whether Test/ has Content, Maps and server config.
    """
    out = ["TOOLCHAIN"]
    for label, cmd in [("dotnet", ["dotnet", "--version"]), ("mono", ["mono", "--version"]),
                       ("uv", ["uv", "--version"]), ("wine (must not be used)", ["wine", "--version"])]:
        c, o = run(cmd, timeout=30)
        out.append(f"  {label}: {o.strip().splitlines()[0] if o.strip() else 'missing'}")
    c, o = run(["dotnet", "--list-runtimes"], timeout=30)
    out.append("  runtimes: " + "; ".join(l.split(" [")[0] for l in o.splitlines()))
    out.append(f"  arch: {os.uname().machine}; ilspycmd: {(Path.home() / '.dotnet/tools/ilspycmd').exists()}")
    ng = Path.home() / ".nuget/packages/monogame.framework.desktopgl"
    out.append(f"  MonoGame DesktopGL in NuGet cache: {sorted(p.name for p in ng.iterdir()) if ng.exists() else 'none'}")
    out.append("\nPROJECTS")
    for sln in sorted(ROOT.glob("*.sln")) + sorted(ROOT.glob("*.slnx")):
        out.append(f"  solution: {sln.name}")
    for p in source_files(exts=(".csproj",)) + sorted((ROOT / "Shims").rglob("*.csproj") if (ROOT / "Shims").exists() else []):
        src = p.read_text(errors="ignore")
        sdk = "<Project Sdk=" in src[:400]
        tf = re.search(r"<TargetFrameworks?>([^<]+)<", src)
        out.append(f"  {rel(p)}: {'SDK ' + (tf.group(1) if tf else '?') if sdk else 'LEGACY'}")
    out.append("\nBUILT BINARIES (outside Test/)")
    found = False
    for name in ("VexillumServer.dll", "VexillumGame.dll", "Game.dll", "VexillumServer.exe", "VexillumGame.exe"):
        for p in ROOT.rglob(name):
            if "Test" in p.parts or "dlls" in p.parts:
                continue
            found = True
            out.append(f"  {rel(p)}  {time.strftime('%Y-%m-%d %H:%M', time.localtime(p.stat().st_mtime))}")
    if not found:
        out.append("  none")
    out.append("\nRUNTIME DIR " + rel(RUNTIME_DIR))
    for sub in ("Content", "Content/ui", "Maps", "Server"):
        d = RUNTIME_DIR / sub
        out.append(f"  {sub}/: {len(list(d.iterdir())) if d.is_dir() else 'MISSING'} entries")
    for f in ("settings.xml", "controls.xml", "steam_appid.txt", "Server/settings.txt", "Server/ops.txt", "Server/banned.txt"):
        out.append(f"  {f}: {'ok' if (RUNTIME_DIR / f).exists() else 'MISSING'}")
    procs = run(["pgrep", "-fl", "Vexillum"], timeout=10)[1].strip()
    out.append("\nRUNNING: " + (procs.replace("\n", "; ") if procs else "nothing"))
    return "\n".join(out)


def _find_binary(stem: str) -> list[str] | None:
    cands = [p for p in ROOT.rglob(stem + ".dll") if "Test" not in p.parts and "dlls" not in p.parts and "obj" not in p.parts]
    if not cands:
        return None
    best = max(cands, key=lambda p: p.stat().st_mtime)
    return ["dotnet", str(best)]


def _launch(cmd: list[str], cwd: Path, log_path: Path, env_extra: dict) -> subprocess.Popen:
    SCRATCH.mkdir(parents=True, exist_ok=True)
    env = os.environ.copy(); env.update(env_extra); env["VEXILLUM_LOG_STDOUT"] = "1"
    f = open(log_path, "w")
    return subprocess.Popen(cmd, cwd=str(cwd), stdout=f, stderr=subprocess.STDOUT, env=env, start_new_session=True)


def _stop(p: subprocess.Popen) -> str:
    if p.poll() is not None:
        return f"exited with {p.returncode}"
    try:
        os.killpg(p.pid, signal.SIGTERM)
        for _ in range(30):
            if p.poll() is not None:
                return f"terminated (SIGTERM), exit {p.returncode}"
            time.sleep(0.1)
        os.killpg(p.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass
    return "killed (SIGKILL)"


def _wait_for(log_path: Path, marker: str, timeout: float, p: subprocess.Popen) -> bool:
    end = time.time() + timeout
    while time.time() < end:
        if marker in log_path.read_text(errors="ignore"):
            return True
        if p.poll() is not None:
            return False
        time.sleep(0.25)
    return False


def _screenshot(name: str) -> str:
    if sys.platform != "darwin":
        return ""
    path = SCRATCH / f"{name}-{int(time.time())}.png"
    c, o = run(["screencapture", "-x", str(path)], timeout=20)
    return str(path) if c == 0 and path.exists() else f"(screenshot failed: {o.strip()})"


def run_server(seconds: int = 8, runtime_dir: str = "Test", port: int = 0, lines: int = 60) -> str:
    """Start the built server in the runtime dir, wait, stop it, return its output.

    Looks for the newest VexillumServer.dll under the repo (not Test/). Sets
    VEXILLUM_LOG_STDOUT=1. Success marker in the output: 'Ready for connections'.
    """
    cmd = _find_binary("VexillumServer")
    if not cmd:
        return "RUN SERVER: no VexillumServer.dll built yet (run build first)"
    if port:
        cmd += ["--port", str(port)]
    rt = ROOT / runtime_dir
    log = SCRATCH / "server-stdout.log"
    p = _launch(cmd, rt, log, {})
    ready = _wait_for(log, "Ready for connections", seconds, p)
    time.sleep(max(0, seconds - 2) if ready else 0)
    how = _stop(p)
    out = log.read_text(errors="ignore")
    dbg = rt / "Server" / "debug_server.log"
    return (f"RUN SERVER: {'READY' if ready else 'NOT READY'}; {how}; cmd={' '.join(cmd)}\n--- stdout/stderr (tail) ---\n{tail(out, lines)}"
            + (f"\n--- Server/debug_server.log (tail) ---\n{tail(dbg.read_text(errors='ignore'), 30)}" if dbg.exists() else ""))


def run_client(seconds: int = 12, screenshot: bool = True, connect: str = "", runtime_dir: str = "Test", lines: int = 60) -> str:
    """Start the built client, optionally --connect host:port, screenshot, stop.

    Returns stdout/stderr tail, the debug_client.log tail, and the screenshot
    path (read it with the Read tool to see the window).
    """
    cmd = _find_binary("VexillumGame")
    if not cmd:
        return "RUN CLIENT: no VexillumGame.dll built yet (run build first)"
    if connect:
        cmd += ["--connect", connect]
    rt = ROOT / runtime_dir
    log = SCRATCH / "client-stdout.log"
    p = _launch(cmd, rt, log, {})
    end = time.time() + seconds
    while time.time() < end and p.poll() is None:
        time.sleep(0.25)
    shot = _screenshot("client") if screenshot and p.poll() is None else ""
    how = _stop(p)
    out = log.read_text(errors="ignore")
    dbg = rt / "debug_client.log"
    return (f"RUN CLIENT: {how}; cmd={' '.join(cmd)}\nscreenshot: {shot or 'none'}\n--- stdout/stderr (tail) ---\n{tail(out, lines)}"
            + (f"\n--- debug_client.log (tail) ---\n{tail(dbg.read_text(errors='ignore'), 30)}" if dbg.exists() else ""))


def smoke_test(seconds: int = 25, runtime_dir: str = "Test", port: int = 24224, clients: int = 1, lines: int = 50) -> str:
    """Server + N clients on loopback. PASS when the server logs N logins and every
    client reaches the level without disconnecting.

    Markers: server 'Ready for connections' then 'logged in as' (N times);
    each client 'Set terrain state' (packet 3) and no 'Disconnected'. With
    clients>=2 the server must also log a 'joined the game' chat for each, which
    proves entity/player broadcast between clients. Takes a screenshot.
    """
    scmd, ccmd = _find_binary("VexillumServer"), _find_binary("VexillumGame")
    if not scmd or not ccmd:
        return f"SMOKE: missing binaries server={bool(scmd)} client={bool(ccmd)}"
    rt = ROOT / runtime_dir
    slog = SCRATCH / "smoke-server.log"
    sp = _launch(scmd + ["--port", str(port)], rt, slog, {})
    if not _wait_for(slog, "Ready for connections", min(20, seconds), sp):
        how = _stop(sp)
        return f"SMOKE: FAIL server never became ready ({how})\n{tail(slog.read_text(errors='ignore'), lines)}"
    cps, clogs = [], []
    for i in range(max(1, clients)):
        clog = SCRATCH / f"smoke-client{i}.log"
        cps.append(_launch(ccmd + ["--connect", f"127.0.0.1:{port}"], rt, clog, {"VEXILLUM_INSTANCE": str(i)}))
        clogs.append(clog)
        time.sleep(1.5)
    deadline = time.time() + seconds
    def count(marker, text): return text.count(marker)
    def remote_logins(text): return len(re.findall(r"\d+\.\d+\.\d+\.\d+:\d+ logged in as", text))
    def shared_creates(texts):
        sets = [set(re.findall(r"Create:\d+@\d+", t)) for t in texts]
        return set.intersection(*sets) if sets else set()
    while time.time() < deadline:
        stext = slog.read_text(errors="ignore")
        texts = [c.read_text(errors="ignore") for c in clogs]
        if remote_logins(stext) >= len(cps) and all("Set terrain state" in t for t in texts) \
                and (len(cps) < 2 or shared_creates(texts)):
            break
        time.sleep(0.5)
    time.sleep(min(6, seconds // 4))
    shot = _screenshot("smoke") if any(cp.poll() is None for cp in cps) else ""
    chows = [_stop(cp) for cp in cps]; show = _stop(sp)
    stext = slog.read_text(errors="ignore")
    ctexts = [c.read_text(errors="ignore") for c in clogs]
    dbg = rt / "debug_client.log"
    if dbg.exists():
        ctexts[0] += "\n--- debug_client.log ---\n" + dbg.read_text(errors="ignore")
    logins = remote_logins(stext)
    terrain = sum("Set terrain state" in t for t in ctexts)
    disconnected = [i for i, t in enumerate(ctexts) if "Disconnected" in t or "Invalid command" in t]
    # Cross-client broadcast evidence: every client logs server entity creates as "Create:<id>@<frame>"
    # (Client.cs packet 42); the same line in every client log proves the server broadcast reached all of them.
    shared = shared_creates(ctexts)
    reasons = []
    if logins < len(cps): reasons.append(f"server logged {logins}/{len(cps)} logins")
    if terrain < len(cps): reasons.append(f"{terrain}/{len(cps)} clients received terrain (packet 3)")
    if disconnected: reasons.append(f"clients {disconnected} logged a disconnect")
    if len(cps) >= 2 and not shared: reasons.append("no entity-create broadcast seen by all clients (need one identical 'Create:id@frame' line in every client log; the server does not log outgoing chat)")
    verdict = "PASS" if not reasons else "FAIL"
    out = [f"SMOKE: {verdict} {'(' + '; '.join(reasons) + ')' if reasons else ''}",
           f"clients={len(cps)} {chows}; server {show}; screenshot: {shot or 'none'}",
           f"logins={logins} terrain={terrain}/{len(cps)} shared entity creates across clients={len(shared)}",
           f"--- server (tail) ---\n{tail(stext, lines)}"]
    for i, t in enumerate(ctexts):
        out.append(f"--- client {i} (tail) ---\n{tail(t, lines)}")
    return "\n".join(out)


def read_log(which: str = "client", lines: int = 80, runtime_dir: str = "Test") -> str:
    """Tail a log: which = client | server | client-stdout | server-stdout | smoke-server | smoke-client | smoke-client<N>."""
    rt = ROOT / runtime_dir
    paths = {"client": rt / "debug_client.log", "server": rt / "Server/debug_server.log",
             "client-stdout": SCRATCH / "client-stdout.log", "server-stdout": SCRATCH / "server-stdout.log",
             "smoke-client": SCRATCH / "smoke-client0.log", "smoke-server": SCRATCH / "smoke-server.log"}
    p = paths.get(which) or (SCRATCH / f"{which}.log" if re.fullmatch(r"smoke-client\d+", which) else None)
    if not p or not p.exists():
        return f"no log: {which} ({p})"
    return tail(p.read_text(errors="ignore"), lines)


def _read_map(path: Path):
    data = path.read_bytes()
    if len(data) < 17 or struct.unpack("<i", data[:4])[0] != MAP_MAGIC:
        raise ValueError("bad magic (not a Vexillum map)")
    payload = lzma.decompress(data[4:], format=lzma.FORMAT_ALONE)
    r = io.BytesIO(payload)
    long_name = read_bstring(r)
    entries = []
    while r.tell() < len(payload):
        fn = read_bstring(r)
        if not fn.strip():
            break
        ln = struct.unpack("<q", r.read(8))[0]
        entries.append((fn, r.read(ln)))
    return long_name, entries, len(data), len(payload)


def map_info(path: str) -> str:
    """Inspect a .map: magic, sizes, long name, embedded files with image
    dimensions, and parsed data.txt regions. First call when a map fails."""
    p = ROOT / path if not os.path.isabs(path) else Path(path)
    try:
        long_name, entries, comp, uncomp = _read_map(p)
    except Exception as ex:
        return f"MAP {rel(p)}: ERROR {ex}"
    out = [f"MAP {rel(p)}: '{long_name}' compressed={comp} uncompressed={uncomp} files={len(entries)}"]
    for fn, data in entries:
        if fn.endswith(".txt"):
            out.append(f"  {fn:16} {len(data):9d} bytes")
            for line in data.decode("utf-8", "replace").splitlines():
                out.append(f"      {line}")
        else:
            out.append(f"  {fn:16} {len(data):9d} bytes  {image_size(data)}")
    missing = [f for f in MAP_FILE_ORDER if f.split(".")[0] not in {e[0].split(".")[0] for e in entries}]
    if missing:
        out.append(f"  MISSING entries the game needs: {missing}")
    return "\n".join(out)


def extract_map(path: str, out_dir: str = "") -> str:
    """Unpack a .map into <out_dir>/<mapname>_<file> like MapExtractor does."""
    p = ROOT / path if not os.path.isabs(path) else Path(path)
    name = p.stem
    d = Path(out_dir) if out_dir else (SCRATCH / "maps" / name)
    d.mkdir(parents=True, exist_ok=True)
    long_name, entries, _, _ = _read_map(p)
    for fn, data in entries:
        (d / f"{name}_{fn}").write_bytes(data)
    (d / "longname.txt").write_text(long_name)
    return f"extracted {len(entries)} files of '{long_name}' to {d}"


def create_map(folder: str, long_name: str, out_path: str = "") -> str:
    """Pack a folder (as produced by extract_map) into a .map the game accepts.

    Files are looked up as <mapname>_<file> or <file>, in the shipped order,
    with jpg/png interchangeable per stem. The LZMA-alone header carries the
    real uncompressed size, matching SevenZipHelper.Compress.
    """
    d = Path(folder) if os.path.isabs(folder) else ROOT / folder
    name = d.name
    w = io.BytesIO()
    write_bstring(w, long_name)
    used = []
    for fn in MAP_FILE_ORDER:
        stem = fn.split(".")[0]
        cands = [c for c in d.iterdir() if c.name in (f"{name}_{fn}", fn)] or \
                [c for c in d.iterdir() if c.stem in (f"{name}_{stem}", stem) and c.suffix in (".jpg", ".jpeg", ".png", ".txt")]
        if not cands:
            return f"create_map: missing {fn} in {d}"
        c = cands[0]
        data = c.read_bytes()
        write_bstring(w, c.name.replace(f"{name}_", "", 1))
        w.write(struct.pack("<q", len(data))); w.write(data)
        used.append(c.name)
    write_bstring(w, "")
    payload = w.getvalue()
    comp = lzma.compress(payload, format=lzma.FORMAT_ALONE, filters=[
        {"id": lzma.FILTER_LZMA1, "dict_size": 1 << 23, "lc": 3, "lp": 0, "pb": 2, "mode": lzma.MODE_NORMAL, "nice_len": 128, "mf": lzma.MF_BT4}])
    comp = comp[:5] + struct.pack("<q", len(payload)) + comp[13:]
    out = Path(out_path) if out_path else d.parent / f"{name}.map"
    out.write_bytes(struct.pack("<i", MAP_MAGIC) + comp)
    check = _read_map(out)
    return f"wrote {rel(out)} ({out.stat().st_size} bytes) from {used}; verify: {len(check[1])} entries, '{check[0]}'"


def _png_decode_rgba(data: bytes):
    """Minimal PNG decoder (8-bit RGB/RGBA/gray/palette, non-interlaced) -> (w, h, bytes RGBA)."""
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("not a PNG")
    pos, idat, plte, trns, w = 8, [], b"", b"", 0
    while pos < len(data):
        ln = struct.unpack(">I", data[pos:pos + 4])[0]; typ = data[pos + 4:pos + 8]; body = data[pos + 8:pos + 8 + ln]
        if typ == b"IHDR":
            w, h, depth, ctype, _, _, interlace = struct.unpack(">IIBBBBB", body)
            if depth != 8 or interlace:
                raise ValueError(f"unsupported PNG depth={depth} interlace={interlace}")
        elif typ == b"PLTE": plte = body
        elif typ == b"tRNS": trns = body
        elif typ == b"IDAT": idat.append(body)
        elif typ == b"IEND": break
        pos += 12 + ln
    raw = zlib.decompress(b"".join(idat))
    ch = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[ctype]
    stride = w * ch
    out = bytearray(h * stride); prev = bytearray(stride); p = 0
    for y in range(h):
        f = raw[p]; line = bytearray(raw[p + 1:p + 1 + stride]); p += 1 + stride
        if f == 1:
            for i in range(ch, stride): line[i] = (line[i] + line[i - ch]) & 255
        elif f == 2:
            for i in range(stride): line[i] = (line[i] + prev[i]) & 255
        elif f == 3:
            for i in range(stride): line[i] = (line[i] + ((line[i - ch] if i >= ch else 0) + prev[i]) // 2) & 255
        elif f == 4:
            for i in range(stride):
                a = line[i - ch] if i >= ch else 0; b = prev[i]; c = prev[i - ch] if i >= ch else 0
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                line[i] = (line[i] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        out[y * stride:(y + 1) * stride] = line; prev = line
    rgba = bytearray(w * h * 4)
    for i in range(w * h):
        px = out[i * ch:(i + 1) * ch]
        if ctype == 6: rgba[i * 4:i * 4 + 4] = px
        elif ctype == 2: rgba[i * 4:i * 4 + 3] = px; rgba[i * 4 + 3] = 255
        elif ctype == 0: rgba[i * 4:i * 4 + 3] = px * 3; rgba[i * 4 + 3] = 255
        elif ctype == 4: rgba[i * 4:i * 4 + 3] = px[:1] * 3; rgba[i * 4 + 3] = px[1]
        elif ctype == 3:
            k = px[0]; rgba[i * 4:i * 4 + 3] = plte[k * 3:k * 3 + 3]; rgba[i * 4 + 3] = trns[k] if k < len(trns) else 255
    return w, h, bytes(rgba)


def terrain_reference(path: str) -> str:
    """Independent oracle for the System.Drawing shim: decode a map's collision.png
    in pure Python, apply Level's constructor rules, and hash the terrain bitfield
    exactly as TerrainArray.ToBytes() lays it out (column-major, bit 0 = solid,
    8 pixels per byte). The C# test must produce the same SHA-256 from
    `new Level(...)` + `GetTerrainState()`. Also reports the alpha histogram so
    partially transparent pixels (where GDI+ MakeTransparent rounding matters)
    are visible.
    """
    import hashlib
    p = ROOT / path if not os.path.isabs(path) else Path(path)
    _, entries, _, _ = _read_map(p)
    col = dict(entries).get("collision.png")
    if col is None:
        return "no collision.png in map"
    w, h, rgba = _png_decode_rgba(col)
    solid = bytearray(w * h)          # index x*h + y like the C# column-major walk
    counts = Counter(); alpha_hist = Counter(); ladders = transparent_when_destroyed = 0
    for x in range(w):
        for y in range(h):
            i = ((h - y - 1) * w + x) * 4         # bitmap row flip, as in Level
            r, g, b, a = rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]
            alpha_hist[a] += 1
            if a == 0:
                color = 0                          # GDI+ MakeTransparent(Transparent) leaves 0x00000000
            else:
                color = (a << 24) | (r << 16) | (g << 8) | b
            if color == 0xFFFF00FF: t = 0; counts["empty (magenta)"] += 1
            elif color == 0: t = 1; counts["solid (alpha 0)"] += 1
            elif color == 0xFFFFFFFF: t = 1; counts["solid (white)"] += 1
            elif color == 0xFF0000FF: t = 0; counts["empty+clear (blue)"] += 1
            elif color == 0xFFFFFF00: t = 0; ladders += 1; counts["ladder (yellow)"] += 1
            elif color == 0xFFFFFF80: t = 0; ladders += 1; counts["ladder+clear"] += 1
            else:
                t = 1; counts["destructible"] += 1
                if g == 128: transparent_when_destroyed += 1
            solid[x * h + y] = t
    bits = bytearray((w * h) // 8)
    for idx in range(w * h):
        if solid[idx]:
            bits[idx >> 3] |= 1 << (idx & 7)
    partial = sum(n for a, n in alpha_hist.items() if 0 < a < 255)
    lines = [f"TERRAIN REFERENCE {rel(p)}: {w}x{h}",
             f"  sha256(ToBytes) = {hashlib.sha256(bits).hexdigest()}",
             f"  bytes = {len(bits)}, solid pixels = {sum(solid)}",
             "  classes: " + ", ".join(f"{k}={v}" for k, v in counts.most_common()),
             f"  ladders={ladders} transparent_when_destroyed={transparent_when_destroyed}",
             f"  alpha: opaque={alpha_hist.get(255, 0)} zero={alpha_hist.get(0, 0)} partial={partial}"
             + ("  <-- partial alpha present; shim must match GDI+ rounding" if partial else "")]
    return "\n".join(lines)


def xnb_info(path: str) -> str:
    """Parse an XNB header: platform, version, flags, size and type readers.
    Effect readers from XNA cannot be loaded by MonoGame; everything else can."""
    p = ROOT / path if not os.path.isabs(path) else Path(path)
    data = p.read_bytes()
    if data[:3] != b"XNB":
        return f"{rel(p)}: not an XNB"
    platform, version, flags = chr(data[3]), data[4], data[5]
    size = struct.unpack("<I", data[6:10])[0]
    compressed = bool(flags & 0x80 or flags & 0x40)
    out = [f"XNB {rel(p)}: platform={platform} version={version} flags=0x{flags:02x} ({'HiDef ' if flags & 1 else 'Reach '}{'LZX' if flags & 0x80 else 'LZ4' if flags & 0x40 else 'uncompressed'}) size={size}"]
    if compressed:
        out.append(f"  decompressed size={struct.unpack('<I', data[10:14])[0]}; readers not parsed (compressed)")
        return "\n".join(out)
    r = io.BytesIO(data[10:])
    n = read_7bit(r)
    for _ in range(n):
        name = read_bstring(r); ver = struct.unpack("<i", r.read(4))[0]
        short = name.split(",")[0]
        note = "  <-- XNA effect bytecode, NOT loadable by MonoGame" if "EffectReader" in short else ""
        out.append(f"  reader: {short} v{ver}{note}")
    out.append(f"  shared resources: {read_7bit(r)}")
    return "\n".join(out)


def decompile(assembly: str, type_name: str = "", max_lines: int = 400) -> str:
    """List types in an assembly (dlls/*.dll etc.) or decompile one type with
    ilspycmd. Useful for writing shims with the exact original API surface."""
    tool = Path.home() / ".dotnet/tools/ilspycmd"
    if not tool.exists():
        return "ilspycmd not installed (dotnet tool install -g ilspycmd)"
    a = ROOT / assembly if not os.path.isabs(assembly) else Path(assembly)
    cmd = [str(tool)] + (["-t", type_name, str(a)] if type_name else ["-l", "c", str(a)])
    c, o = run(cmd, timeout=120, env={"DOTNET_ROLL_FORWARD": "Major"})
    o = "\n".join(l for l in o.splitlines() if "latest version" not in l and "please update" not in l)
    lines = o.splitlines()
    return "\n".join(lines[:max_lines]) + (f"\n... {len(lines) - max_lines} more lines" if len(lines) > max_lines else "")


# ---- persistent processes + in-process debugging --------------------------
PROC_DIR = SCRATCH / "proc"
DEFAULT_DEBUG_PORTS = {"server": 7801, "client": 7802}


def _proc_meta(name: str) -> dict | None:
    f = PROC_DIR / f"{name}.json"
    if not f.exists():
        return None
    try:
        m = json.loads(f.read_text())
    except Exception:
        return None
    try:
        os.kill(m["pid"], 0)
        m["alive"] = True
    except OSError:
        m["alive"] = False
    return m


def _proc_names() -> list[str]:
    PROC_DIR.mkdir(parents=True, exist_ok=True)
    return sorted(f.stem for f in PROC_DIR.glob("*.json"))


def _client_name(instance: int) -> str:
    return "client" if instance == 0 else f"client{instance}"


def proc_start(target: str = "server", connect: str = "", port: int = 0, runtime_dir: str = "Test",
               debug_port: int = 0, instance: int = 0, wait_seconds: int = 20) -> str:
    """Start the server or a client and LEAVE IT RUNNING (unlike run_server/run_client).

    target: 'server' or 'client'. connect: host:port for the client. instance:
    0,1,2... to run several clients ('client', 'client1', ...). debug_port:
    port for the in-process debug console (default 7801 server, 7802+instance
    client; 0 = default; -1 = disabled). Waits up to wait_seconds for the
    readiness marker. Use proc_stop to end it. State lives in /tmp/vexillum-dev/proc.
    """
    PROC_DIR.mkdir(parents=True, exist_ok=True)
    if target not in ("server", "client"):
        return "proc_start: target must be 'server' or 'client'"
    name = "server" if target == "server" else _client_name(instance)
    old = _proc_meta(name)
    if old and old["alive"]:
        return f"proc_start: {name} is already running (pid {old['pid']}); proc_stop it first"
    cmd = _find_binary("VexillumServer" if target == "server" else "VexillumGame")
    if not cmd:
        return f"proc_start: no binary built for {target} (run build first)"
    if target == "server" and port:
        cmd += ["--port", str(port)]
    if target == "client" and connect:
        cmd += ["--connect", connect]
    if debug_port == 0:
        debug_port = DEFAULT_DEBUG_PORTS[target] + (instance if target == "client" else 0)
    env = {"VEXILLUM_LOG_STDOUT": "1", "VEXILLUM_INSTANCE": str(instance)}
    if debug_port > 0:
        env["VEXILLUM_DEBUG_PORT"] = str(debug_port)
    rt = ROOT / runtime_dir
    log = PROC_DIR / f"{name}.log"
    p = _launch(cmd, rt, log, env)
    meta = {"name": name, "target": target, "pid": p.pid, "cmd": cmd, "cwd": str(rt), "log": str(log),
            "debug_port": debug_port if debug_port > 0 else None, "port": (port or 24224) if target == "server" else (connect or "menu"), "started": time.strftime("%Y-%m-%d %H:%M:%S")}
    (PROC_DIR / f"{name}.json").write_text(json.dumps(meta, indent=1))
    marker = "Ready for connections" if target == "server" else ("Set terrain state" if connect else "PortProgram: cwd")
    ready = _wait_for(log, marker, wait_seconds, p)
    if p.poll() is not None:
        (PROC_DIR / f"{name}.json").unlink(missing_ok=True)
        return f"proc_start: {name} exited immediately with {p.returncode}\n{tail(log.read_text(errors='ignore'), 40)}"
    dbg = ""
    if debug_port > 0:
        dbg = "debug console " + ("up" if "DebugHost: listening" in log.read_text(errors="ignore") else "not yet reported") + f" on 127.0.0.1:{debug_port}"
    return (f"proc_start: {name} pid {p.pid} {'READY' if ready else 'started (marker not seen yet: ' + marker + ')'}; {dbg}\n"
            f"log: {log}\n{tail(log.read_text(errors='ignore'), 15)}")


def proc_stop(target: str = "all") -> str:
    """Stop processes started with proc_start: target = all | server | client | client<N>."""
    names = _proc_names() if target == "all" else [target]
    out = []
    for name in names:
        m = _proc_meta(name)
        if not m:
            out.append(f"{name}: not tracked"); continue
        if m["alive"]:
            try:
                os.killpg(m["pid"], signal.SIGTERM)
            except ProcessLookupError:
                pass
            for _ in range(40):
                try:
                    os.kill(m["pid"], 0); time.sleep(0.1)
                except OSError:
                    break
            else:
                try: os.killpg(m["pid"], signal.SIGKILL)
                except ProcessLookupError: pass
            out.append(f"{name}: stopped (pid {m['pid']})")
        else:
            out.append(f"{name}: was not running")
        (PROC_DIR / f"{name}.json").unlink(missing_ok=True)
    return "\n".join(out) or "nothing to stop"


def proc_status() -> str:
    """List processes started with proc_start (pid, alive, ports, log) plus any other Vexillum processes."""
    out = []
    for name in _proc_names():
        m = _proc_meta(name)
        if not m:
            continue
        out.append(f"{name}: pid {m['pid']} {'ALIVE' if m['alive'] else 'DEAD'} target={m['target']} port={m['port']} debug_port={m['debug_port']} started={m['started']} log={m['log']}")
        try:
            out.append("   last: " + tail(Path(m["log"]).read_text(errors="ignore"), 1).strip()[:200])
        except Exception:
            pass
    other = run(["pgrep", "-fl", "Vexillum(Game|Server).dll"], timeout=10)[1].strip()
    tracked = {str(_proc_meta(n)["pid"]) for n in _proc_names() if _proc_meta(n)}
    untracked = [l for l in other.splitlines() if l.split()[0] not in tracked]
    if untracked:
        out.append("untracked Vexillum processes (not started by proc_start): " + "; ".join(untracked))
    return "\n".join(out) or "no tracked processes"


def proc_logs(target: str = "server", lines: int = 80, grep: str = "") -> str:
    """Tail the stdout/stderr log of a proc_start process (server | client | client<N>),
    or 'client-file' / 'server-file' for the game's own debug_client.log / Server/debug_server.log
    in the runtime dir. grep: regex to filter lines (case-insensitive)."""
    if target in ("client-file", "server-file"):
        rt = RUNTIME_DIR
        p = rt / ("debug_client.log" if target == "client-file" else "Server/debug_server.log")
    else:
        m = _proc_meta(target)
        if not m:
            return f"no tracked process '{target}' (tracked: {', '.join(_proc_names()) or 'none'})"
        p = Path(m["log"])
    if not p.exists():
        return f"no log at {p}"
    text = p.read_text(errors="ignore")
    if grep:
        rx = re.compile(grep, re.I)
        text = "\n".join(l for l in text.splitlines() if rx.search(l))
    return tail(text, lines)


def screenshot(name: str = "shot") -> str:
    """Capture the display to a PNG (macOS screencapture) and return its path; read it with the Read tool."""
    return _screenshot(name) or "screenshot unavailable on this platform"


def _debug_call(target: str, code: str, timeout: int) -> dict:
    import socket
    m = _proc_meta(target)
    if not m:
        raise RuntimeError(f"no tracked process '{target}' (tracked: {', '.join(_proc_names()) or 'none'})")
    if not m["alive"]:
        raise RuntimeError(f"{target} (pid {m['pid']}) is not running")
    if not m.get("debug_port"):
        raise RuntimeError(f"{target} was started without a debug console (debug_port=-1)")
    with socket.create_connection(("127.0.0.1", m["debug_port"]), timeout=timeout + 5) as sock:
        sock.sendall((json.dumps({"code": code, "timeout": timeout * 1000}) + "\n").encode("utf-8"))
        buf = b""
        while not buf.endswith(b"\n"):
            chunk = sock.recv(65536)
            if not chunk:
                break
            buf += chunk
    return json.loads(buf.decode("utf-8"))


def eval(target: str = "client", code: str = "", timeout: int = 15) -> str:
    """Evaluate a C# script inside the running client or server (started with proc_start).

    Script state persists between calls (send '!reset' to clear). Available in
    scripts: Game (dynamic, the Vexillum.Vexillum instance on the client),
    Server (dynamic, the Server.Server instance), Sync(() => ...) to run on the
    game thread, Get(obj,"field")/Set/Call for private members, Static("Type","member"),
    TypeOf("Name"), Dump(obj), Log(x). Usings: System, System.Linq, Vexillum.*,
    Microsoft.Xna.Framework. The last expression's value is returned.
    Examples: eval client 'Game.View.GetType().Name'
              eval server 'Sync(() => ((IEnumerable<object>)Server.players).Count())'
              eval client 'var c = Get(Game, "client"); Dump(Get(c, "player"))'
    """
    if not code:
        return "eval: code is required"
    try:
        r = _debug_call(target, code, timeout)
    except Exception as ex:
        return f"eval: {ex}"
    parts = []
    if r.get("ok"):
        parts.append(f"OK ({r.get('ms', '?')} ms)\n{r.get('result', '')}")
    else:
        parts.append(f"ERROR ({r.get('ms', '?')} ms)\n{r.get('error', '')}")
    if r.get("log"):
        parts.append("--- log ---\n" + r["log"].rstrip())
    return "\n".join(parts)


PROBES = {
    "client": {
        "view": "Game.View == null ? \"no view\" : Game.View.GetType().FullName",
        "frame": "Sync(() => { var v = Game.View as GameView; return v == null ? \"not in game\" : \"frame=\" + v.Level.frame + \" time=\" + v.Level.GetTime() + \" entities=\" + v.Level.getEntities().Count + \" visible=\" + v.Level.visibleEntities; })",
        "player": "Sync(() => { var v = Game.View as GameView; if (v == null) return \"not in game\"; var p = (LocalPlayer)Get(v, \"player\"); return p == null ? \"no local player\" : Dump(p) + \"\\nentity: \" + Dump(p.Entity); })",
        "entities": "Sync(() => { var v = Game.View as GameView; if (v == null) return \"not in game\"; return v.Level.getEntities().Select(e => e.GetType().Name + \" #\" + e.ID + \" pos=\" + e.Position + \" vel=\" + e.Velocity + (e.player != null ? \" player=\" + e.player.name : \"\")).ToList(); })",
        "players": "Sync(() => { var v = Game.View as GameView; if (v == null) return \"not in game\"; var c = Get(Get(v, \"player\"), \"client\"); var ps = (System.Collections.IEnumerable)Get(c, \"players\"); return ps.Cast<Player>().Select(p => p.name + \" class=\" + p.CurrentClass + \" score=\" + p.Score + \" ping=\" + p.pingString + \" hp=\" + (p.Entity != null ? p.Entity.Health.ToString() : \"-\") + \" pos=\" + (p.Entity != null ? p.Entity.Position.ToString() : \"-\")).ToList(); })",
        "gamemode": "Sync(() => { var v = Game.View as GameView; if (v == null) return \"not in game\"; return Dump(((LocalPlayer)Get(v, \"player\")).GetGameMode()); })",
        "threads": "System.Diagnostics.Process.GetCurrentProcess().Threads.Count + \" threads; GC=\" + (GC.GetTotalMemory(false) / 1048576) + \" MB\"",
    },
    "server": {
        "players": "Sync(() => ((System.Collections.IEnumerable)Server.players).Cast<Player>().Select(p => p.name + \" class=\" + p.CurrentClass + \" score=\" + p.Score + \" ping=\" + p.pingString + \" bot=\" + p.isBot + \" hp=\" + (p.Entity != null ? p.Entity.Health.ToString() : \"-\") + \" pos=\" + (p.Entity != null ? p.Entity.Position.ToString() : \"-\")).ToList())",
        "frame": "Sync(() => \"frame=\" + Server.level.frame + \" time=\" + Server.level.GetTime() + \" map=\" + Server.level.ShortName + \" entities=\" + Server.level.getEntities().Count + \" ready=\" + Server.ready)",
        "entities": "Sync(() => Server.level.getEntities().Select(e => e.GetType().Name + \" #\" + e.ID + \" pos=\" + e.Position + \" vel=\" + e.Velocity + (e.player != null ? \" player=\" + e.player.name : \"\")).ToList())",
        "gamemode": "Sync(() => Dump(Server.gameMode))",
        "level": "Sync(() => Dump(Server.level))",
        "threads": "System.Diagnostics.Process.GetCurrentProcess().Threads.Count + \" threads; GC=\" + (GC.GetTotalMemory(false) / 1048576) + \" MB\"",
    },
}


def probe(target: str = "server", what: str = "players") -> str:
    """Run a canned inspection script in a running process.
    client: view | frame | player | entities | players | gamemode | threads
    server: players | frame | entities | gamemode | level | threads
    Use eval for anything else; 'list' shows the scripts."""
    role = "server" if target == "server" else "client"
    if what == "list":
        return "\n".join(f"{k}: {v}" for k, v in PROBES[role].items())
    code = PROBES[role].get(what)
    if not code:
        return f"unknown probe '{what}' for {role}; options: {', '.join(PROBES[role])}"
    return eval(target, code)


TOOLS = {f.__name__: f for f in [build, port_audit, invariant_check, preservation_check, runtime_status,
                                 run_server, run_client, smoke_test, read_log, map_info, extract_map,
                                 create_map, terrain_reference, xnb_info, decompile,
                                 proc_start, proc_stop, proc_status, proc_logs, screenshot, eval, probe]}


# ---- entry points -----------------------------------------------------------
def _coerce(fn, kwargs: dict) -> dict:
    import inspect
    sig = inspect.signature(fn)
    out = {}
    for k, v in kwargs.items():
        if k not in sig.parameters:
            raise SystemExit(f"unknown argument {k} for {fn.__name__}; expected {list(sig.parameters)}")
        ann = sig.parameters[k].annotation
        if ann in (int, "int"):
            v = int(v)
        elif ann in (bool, "bool"):
            v = str(v).lower() in ("1", "true", "yes")
        elif "list" in str(ann):
            v = [x for x in str(v).split(",") if x]
        out[k] = v
    return out


def cli(argv: list[str]) -> int:
    if not argv or argv[0] in ("help", "-h", "--help"):
        print(__doc__)
        for name, fn in TOOLS.items():
            print(f"{name}: {(fn.__doc__ or '').strip().splitlines()[0]}")
        return 0
    fn = TOOLS.get(argv[0])
    if not fn:
        print(f"unknown tool {argv[0]}"); return 2
    kwargs = dict(a.split("=", 1) for a in argv[1:] if "=" in a)
    print(fn(**_coerce(fn, kwargs)))
    return 0


def serve() -> None:
    try:
        from mcp.server.fastmcp import FastMCP as Server          # mcp 1.x
    except ImportError:
        from mcp.server.mcpserver import MCPServer as Server      # mcp 2.x
    mcp = Server("vexillum-dev")
    for fn in TOOLS.values():
        mcp.tool()(fn)
    mcp.run()


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] != "serve":
        sys.exit(cli(sys.argv[1:]))
    serve()
