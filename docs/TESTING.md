# Testing Vexillum

Three layers, from fastest to slowest. All of them observe the game's real
behaviour; none of them changes a historical file (`preservation_check`
must stay clean).

| layer | project | what runs | command |
|---|---|---|---|
| unit | `Tests/Vexillum.Tests` (xunit) | shims, the terrain oracle, the Nuclex port; no processes | `make test` |
| acceptance | `Tests/Vexillum.Acceptance` (xunit) | the real `VexillumServer` executable in a scratch copy of `Test/`, driven by a scripted protocol client and the in-process debug console; `Level` physics headless | `make acceptance` |
| e2e | `Tests/e2e` (pytest) | real server **and real game windows**, driven through the debug console; screenshots | `make e2e` (needs a display) |

`make test-all` runs unit + acceptance. Filter single tests with
`dotnet test Tests/Vexillum.Acceptance --filter "FullyQualifiedName~ScriptedClient"` or
`python3 -m pytest Tests/e2e -m e2e -k smoke`.

Everything is built with `dotnet build Vexillum.sln` first (the acceptance
project references `Server.csproj` so the executable it starts is up to
date). Executables: `Server/bin/Debug/net9.0/VexillumServer`,
`ZombieSurvival/bin/Debug/net9.0/VexillumGame`.

## Rules for every test

* **Never touch `Test/`.** Use a scratch runtime (`ScratchRuntime` /
  `scratch_runtime`): a copy of `Test/` without the shipped exes and logs, in
  a unique temp folder, deleted afterwards.
* **Own ports.** Every server gets a free TCP port (`FreePort.Tcp()` /
  `free_tcp_port()`), a free UDP port for the LAN beacon
  (`VEXILLUM_LAN_PORT`) and a free debug-console port. Never hard-code 24224.
* **`VEXILLUM_MASTER=off`** for every server a test starts (the harness sets
  it): nothing is ever published to the public ntfy registry.
* **No sleeps as synchronisation.** Poll with a timeout: `ServerProcess.WaitFor`,
  `ScriptedClient.WaitFor<T>` / `WaitUntil`, `wait_until()` in pytest. A
  sleep is acceptable only as a *duration* (hold a key for 100 ms).
* **No tautologies.** Assert on state the exercised code produced (a packet
  the server sent, a position physics computed), never on values the test
  set itself.
* **Kill what you start.** Dispose `ServerProcess`/`ScriptedClient`
  (`using`), rely on the pytest fixtures; check `pgrep -fl Vexillum` when a
  run was interrupted, and only kill processes started from your scratch
  directories.
* **Known bugs.** When a test exposes a bug listed under "Known original
  bugs" in `docs/PORTING.md` (or a new one), write the test for the *correct*
  behaviour and skip it:
  * xunit: `[Fact(Skip = "Known original bug: <what>, docs/PORTING.md")]`
  * pytest: `@pytest.mark.xfail(reason="Known original bug: <what>, docs/PORTING.md", strict=True)`

  Add newly found bugs to that list in `docs/PORTING.md`. Never change the
  game to make a test pass (invariant 11: bug fixes are separate, named
  commits after the port).

### Bots and `maxbots` (read before configuring a server)

`Server.UpdateBots` hangs the `Server Main` thread when `RemoveBot` has to
remove a bot but finds none of the team it picks (known original bug, see
`docs/PORTING.md`). This happens with small `maxbots` values:

* `maxbots 0` and one human joins: `maxAllowedBots` = -1, nothing to remove,
  infinite loop. Every later `AddTask` (including logins) stalls; the probe
  still answers because the acceptor thread is alive.
* `maxbots 2`, one human plus its one bot, a second human joins: 50% hang.

Safe configurations: the **default `maxbots 6`** with up to three human
clients (bots then exist on both teams), or **`maxbots 1` for a
single-client test** (never any bot; a second successful join would hang).
Bots move, fire and switch weapons; filter packets by entity id
(`ScriptedClient.MyEntityId`, `PlayerNamed(name).EntityId`) rather than
expecting a quiet server.

## Acceptance harness (`Tests/Vexillum.Acceptance`)

Namespace `Vexillum.Acceptance`. One class per helper, each with XML docs:

| class | purpose |
|---|---|
| `Repo` | repo root (walks up to `Vexillum.sln`), `RuntimeDir` (= `Test/`), `ServerExe`, `ClientExe` |
| `ScratchRuntime` | private copy of `Test/`; `RemoveMap`, `SetServerSetting(key, value)`, `GetServerSetting`, `SetPlayerList("ops"/"banned", ...)`, `ReadServerLog`, `ReadClientLog`, `MapPath`, `MapNames` |
| `FreePort` | `Tcp()`, `Udp()`, `IsTcpFree(port)` |
| `ServerProcess` | starts `VexillumServer --root <scratch> --port <n>` with the right environment, waits for `Ready for connections`; `Output`, `WaitFor(regex, timeout)`, `WaitForCount`, `Lines`, `Console` (a `DebugConsole`), `Kill`; output saved to `$TMPDIR/vexillum-acceptance/logs/` on dispose |
| `DebugConsole` | in-process C# eval (`Eval`, `EvalT<T>`, `Sync`), probes `ServerPlayers()`, `ServerPlayer(name)`, `ServerFrame()`, `ServerTime()`, `ServerMap()`, `ServerReady()`, `ServerEntities()`, `ClientEntities()` |
| `ScriptedClient` | the client side of `docs/PROTOCOL.md` over a raw `TcpClient`: `Login`, `Status`, `Probe` (byte 255), `SendPosition*`, `SendWeaponActivate/Action/Select`, `SendHitscan`, `SendChat`, `SendRaw`; every server packet decoded into a `ServerPacket` record (`Packets.cs`); `WaitFor<T>`, `WaitForNext<T>`, `NoneWithin<T>`, `WaitUntil`, `Packets<T>()`; `JoinGame(name)` runs the whole handshake; `MyEntityId`, `Me`, `MyEntity`, `Players`, `Entities` (positions from 30/31/32), `Terrain` (packet 3 as a `TerrainSnapshot`), `ReceivedMapBytes` (220-222), `Frame`, `Disconnect` |
| `Protocol` | packet ids, entity-type table, angle/movement encoders (a copy checked against `StreamHelper`) |
| `MapFile` | parses a `.map` without decoding images; `Md5(path)` as the client sends it; `Width`/`Height` |
| `TerrainSnapshot` | the `ToBytes()` bitfield: `IsSolid(x, y)`, `SolidCount`, `Sha256Hex`, `CountDifferences` |
| `HeadlessLevel` | a concrete `Level` from a shipped map with `Util.IsServer = true`: `AddHumanoid`, `AddHumanoidAtSpawn`, `Add`, `StepFrames(n)`, `IsSolid`, `CollisionNibble`, `IsLadder`, `EntityAt`, `Snapshot`, `Spawns`, `Flags`, `GroundBelow` |
| `SyntheticLevel` | a concrete `Level` over hand-made collision bitmaps described in world coordinates (`Build(w, h, (x, y) => colour)`, `Uniform`, colour constants `Empty`/`Solid`/`Ladder`/`Clear`/`Destructible(nibble)`) or a shipped map without regions (`FromShippedMap`); records `Collisions`, `HitscanHits`, `Deaths`, owns the `TaskQueue` (`Tasks`, processed after each `StepFrames` frame), `UseServerCollision()` calls `OnCollide` like ServerLevel, `CreateEntity<T>(fullName)` builds internal entity types (Rocket, ClusterBomb) |
| `ServerFixture` | `IClassFixture` with one runtime + one server per class; override `Configure` to edit settings before the start |
| `GameStateCollection`, `DisplayCollection` | serial collections, see below |

### Parallelism

xunit runs classes in parallel (at most `MaxParallelThreads = 4`, set in
`Collections.cs`) and the tests inside a class serially. A class that owns a
`ServerProcess` in its own `ScratchRuntime` needs no attribute. Tests that
touch process-wide game state (`HeadlessLevel`, `LevelLoader`,
`Entity.ResetID`, `Util.IsServer`, `HumanoidTypes`, the current directory)
go into `[Collection(GameStateCollection.Name)]`, which runs serially and
not alongside other collections. Tests that need the display or the shared
mouse/keyboard use `[Collection(DisplayCollection.Name)]`.

### Writing an acceptance test

Server + scripted client (protocol and server behaviour):

```csharp
public class ChatServerFixture : ServerFixture
{
    protected override void Configure(ScratchRuntime runtime, ServerProcess.Options options)
    {
        runtime.SetServerSetting("maps", "bases");
        runtime.SetServerSetting("maxbots", "1");   // single client, no bots
    }
}

public class ChatTests : IClassFixture<ChatServerFixture>
{
    private readonly ChatServerFixture fx;
    public ChatTests(ChatServerFixture fx) { this.fx = fx; }

    [Fact]
    public void ChatIsBroadcastWithTheDisplayName()
    {
        using (ScriptedClient c = new ScriptedClient(fx.Server))
        {
            c.JoinGame("alice");
            int mark = c.PacketCount;
            c.SendChat("hello");
            ChatPacket p = c.WaitFor<ChatPacket>(x => x.Text.EndsWith("> hello"), TimeSpan.FromSeconds(5), mark);
            Assert.Contains("alice", p.Text);
            Assert.NotNull(fx.Server.WaitFor("alice: hello", 5));   // the server logs chat
            Assert.Equal("alice", fx.Server.Console.ServerPlayer("alice").Name);
        }
    }
}
```

Headless physics (no process):

```csharp
[Collection(GameStateCollection.Name)]
public class GravityTests
{
    [Fact]
    public void FreeFallAccelerates()
    {
        using (ScratchRuntime rt = new ScratchRuntime())
        {
            HeadlessLevel level = HeadlessLevel.Load(rt, "bases");
            HumanoidEntity e = level.AddHumanoid(PlayerClass.Green, new Vec2(500, 900));
            level.StepFrames(1);
            float v1 = e.Velocity.Y;
            level.StepFrames(1);
            Assert.Equal(v1 - level.Gravity, e.Velocity.Y, 3);
        }
    }
}
```

Map download (packets 220-222): `rt.RemoveMap("bases")` before the client
joins; `JoinGame` then sends `Status(false)`, receives the map, installs it
into the scratch `Maps/` and continues; compare `c.ReceivedMapBytes` with
`MapFile.Read(Test/Maps/bases.map).CompressedPayload`.

Rejections: `JoinGame` throws `ServerDisconnectedException` with the
server's reason; for finer control call `Login`/`Status` yourself and
`WaitFor<DisconnectPacket>`. `SendRaw(...)` writes arbitrary bytes.

Debug console scripts see `Server` (dynamic) and can run on the game thread
with `Sync(() => ...)`; cast dynamics before using LINQ
(`((List<Entity>)Server.level.getEntities()).Select(...)`) and use
`Get(obj, "field")` for private members. Persisted server output for a failed
run is in `$TMPDIR/vexillum-acceptance/logs/`.

## E2E harness (`Tests/e2e`)

`conftest.py` builds on `.claude/mcp/vexillum_dev.py` imported as a module
(no shelling out); its process registry is redirected to a per-session temp
folder so interactive `proc_start` sessions are untouched. Fixtures and
helpers:

| name | purpose |
|---|---|
| `scratch_runtime` | private copy of `Test/` (`pathlib.Path`); edit `Server/settings.txt` with `set_server_setting(rt, key, value)` in a fixture that runs before `server` |
| `server` | the server (`proc_start`) on free ports with `VEXILLUM_MASTER=off`; `.port`, `.debug_port`, `.logs(grep)`, `.wait_log(regex)`, `.ev(code)`, `.frame()`, `.players()` |
| `make_client(instance)` / `client` | a game window joined to `server` (waits for `Set terrain state` and the `GameView`); `.ev`, `.logs` |
| `ev(target, code)` | in-process eval; raises `EvalError` |
| `wait_until(fn, timeout, interval)` | polling with timeout |
| `press(client, key, hold_ms)`, `key_down/up` | keyboard through `GameView.KeyPressed/KeyReleased` on the update thread (`Keys` names: `A`, `D`, `Space`, `R`, `Escape`, ...) |
| `mouse_move`, `mouse_down/up(client, button, x, y)`, `click` | mouse through `GameView.MouseMove/MouseDown/MouseUp` (`Left`/`Middle`/`Right`, 840x630 window coordinates) |
| `local_player_state(client)` | `{name, class, weapon, entity_id, x, y, health, score, bot}` from the client's `LocalPlayer` |
| `server_player_state(server, name)` | the same fields from `Server.players` |
| `screenshot(name, region=None)` | PNG path; `region=(x, y, w, h)` in screen points captures only that rectangle |
| `region_stats(path, x, y, w, h)` | mean colour, `stddev` and `nonuniform` (fraction of pixels off the mean) of a pixel rectangle |

Mark every test `@pytest.mark.e2e` (registered in `pytest.ini`, which also
sets `-p no:cacheprovider`). Set `VEXILLUM_E2E_KEEP=1` to keep the session
folder (logs, screenshots, scratch runtimes) after a run.

```python
import pytest
from conftest import local_player_state, press, wait_until, screenshot, region_stats

@pytest.mark.e2e
def test_walking_right_moves_the_player(server, client):
    me = wait_until(lambda: local_player_state(client), timeout=20)
    press(client, "D", hold_ms=400)
    wait_until(lambda: local_player_state(client)["x"] > me["x"], timeout=5)
    assert local_player_state(client)["x"] - me["x"] >= 8   # 2 px per frame at 60 Hz
    shot = screenshot("walk", region=(0, 0, 840, 630))
    assert region_stats(shot)["nonuniform"] > 0.05   # something is drawn
```

Display notes: the client is a real 840x630 window; `screenshot()` without a
region captures the whole display, so the window must be frontmost and not
covered. On a Retina display the PNG has twice the pixels of the screen
points you pass in `region`. Screenshots are macOS only (`screencapture`);
on other platforms `screenshot()` raises. Two clients in one scratch runtime
share `debug_client.log`; use `client.logs()` (stdout) per process instead.

## Reference hashes

`terrain_reference` (pure-Python oracle) for the shipped maps, also asserted
by `LevelTerrainTests` and the acceptance self-tests:

```
bases    059ee417b1b64d17f7ebe439dd8bf5b05f01177866873db6dbcd4947716987e3   3914x1024
complex  d5d14f4c61b455f074f82222ad6da7a5a491bf4ddcd53bde8056c3f2974695b6   2736x818
```
