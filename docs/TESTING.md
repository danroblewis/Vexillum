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

## Counts and status

Last full run of every layer on this branch (macOS arm64, .NET 9, MonoGame
3.8.4). The acceptance suite was run three times in a row with identical
results; the skips are the known original bugs listed further down.

| layer | area (namespace / folder) | tests | pass | skip | run only this area |
|---|---|---|---|---|---|
| unit | `Vexillum.Tests` | 31 | 31 | 0 | `make test` |
| acceptance | harness self-tests (`Vexillum.Acceptance.*SelfTests`, `KnownServerBugs`) | 14 | 13 | 1 | `--filter "FullyQualifiedName~SelfTests"` |
| acceptance | protocol (`Vexillum.Acceptance.protocol`, `Protocol/`) | 66 | 63 | 3 | `--filter "FullyQualifiedName~Acceptance.protocol"` |
| acceptance | server gameplay (`Vexillum.Acceptance.servergameplay`, `Gameplay/`) | 43 | 41 | 2 | `--filter "FullyQualifiedName~servergameplay"` |
| acceptance | physics and terrain (`Vexillum.Acceptance.physicsterrain`, `World/`) | 81 | 75 | 6 | `--filter "FullyQualifiedName~physicsterrain"` |
| acceptance | tools and config (`Vexillum.Acceptance.toolsconfig`, `Tools/`) | 82 | 80 | 2 | `--filter "FullyQualifiedName~toolsconfig"` |
| **acceptance** | **all** (`make acceptance`, about 3 minutes) | **286** | **272** | **14** | |
| e2e | `Tests/e2e/test_*.py` (launcher, main menu, movement, weapons, chat/HUD, session, spectator, terrain rendering, harness smoke) | 41 | 41 | 0 xfail | `make e2e` (about 4 minutes, opens windows) |

(`dotnet test` counts theory cases: the 73 `[Fact]`/`[Theory]` methods of
`Tools/` expand to 82 cases.) The filters are `dotnet test
Tests/Vexillum.Acceptance -c Debug --no-build --filter ...` after `make`;
the e2e run takes `-k <substring>` to pick tests.

An acceptance test that starts a server takes a few seconds; a class shares
one server through its fixture. Nothing in any layer touches `Test/`,
listens on `24224` (the harness constants `Protocol.DefaultPort` and
`clientlib.DEFAULT_PORT` exist only for assertions on the game's default) or
publishes to the public registry (`VEXILLUM_MASTER=off`).

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
| `MapWriter` | writes a `.map` (magic + LZMA records) from entries, `MinimalEntries(w, h, collisionArgb, dataTxt)` for a synthetic map with its own `data.txt`, `Png`/`FilledPng` through the Drawing shim |
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

## Skipped tests: known original bugs

Each test below asserts the behaviour the author intended and is skipped
because the 2013 code does not deliver it; the bug is described under
"Known original bugs" in `docs/PORTING.md` and stays unfixed until a
deliberate, named commit (invariant 11). Un-skip the test in that commit.

| test | bug |
|---|---|
| `HarnessSelfTests.KnownServerBugs.ServerKeepsServingWithMaxbotsZero` | `Server.UpdateBots` loops forever when `RemoveBot` finds no bot of the class it picks (`maxbots 0` and one human) |
| `Gameplay/CommandTests.Kick_disconnects_the_named_player` | `/kick` compares `p.name` (the issuer) instead of `other.name` |
| `Gameplay/CommandTests.Ban_disconnects_the_named_player` | `/ban` compares `p.name` (the issuer) instead of `other.name` |
| `Protocol/EncodingTests.Strings_of_128_bytes_or_more_round_trip` | MiscUtil `Write7BitEncodedInt` writes a stray `0x00` for lengths of 128 or more |
| `Protocol/RejectionTests.Name_of_128_characters_is_accepted` | same 7-bit length bug: the 128-character name arrives as an empty name plus a garbage ticket length |
| `Protocol/RejectionTests.Name_of_129_characters_is_rejected_as_invalid` | same 7-bit length bug: the server never sees the name it should reject |
| `Protocol/BufferedPacketTests.Health_packet_reaches_the_other_clients_as_soon_as_the_server_writes_it` | `ServerPlayer.SendPlayerHealth` (and `SendGameMode*`, `SendSound`, `SendGrapplingHook`) write without `WriteData`, so 110/120-122/98/22 only leave with the next flushed packet; the actual behaviour is asserted by `Health_packet_only_leaves_the_server_together_with_the_next_flushed_packet` |
| `World/TerrainArrayTests.ToBytes_and_SetBytes_round_trip_a_3x3_array` | `TerrainArray.ToBytes`/`SetBytes` overrun the `(w*h)/8` buffer when `w*h` is not a multiple of 8 |
| `World/EntityOutlineTests.NextID_never_returns_zero` | `Entity.NextID` hands out id 0 (the "no entity" outline value) after the `short` counter wraps |
| `World/HealthAndHitscanTests.Setting_health_to_zero_before_the_entity_is_added_does_not_throw` | `HumanoidEntity.Health` setter calls `Level.OnEntityDeath` while `Level` is null |
| `World/HealthAndHitscanTests.Hitscan_stops_at_an_entity_box_and_skips_the_ignored_entity` | `Entity.tCorner`/`bCorner` are only computed in the `Size` setter, so `Entity.TestPoint` tests a box around the origin |
| `World/ExplosionTests.Explosion_at_the_entity_centre_gives_a_finite_velocity` | `Level.DrawCircle` normalises the zero vector: an explosion centred on an entity leaves its velocity NaN |
| `World/LevelLoaderTests.Malformed_data_line_is_reported_with_its_line_number_and_the_other_regions_still_load` | `LevelLoader.LoadData` reports a malformed `data.txt` line through `Vexillum.Error`, which dereferences the null `Vexillum.game` on the server, so the load aborts with a `NullReferenceException` instead of naming the line and continuing |
| `Tools/ServerConfigTests.Parse_error_reports_the_one_based_line_number` | `ParseServerConfig` reports `"line " + l+1` (string concatenation) |
| `Tools/MasterServerTests.EscapeUriString_escapes_reserved_characters` | `Util.EscapeUriString` returns the unescaped input |

Other known original bugs are pinned by tests that assert the *current*
behaviour instead, named after the quirk where it is the point of the test
(`Tools/ServerConfigTests.Parse_error_reports_the_line_with_the_concatenation_quirk`,
`Protocol/RejectionTests.Server_full_rejects_the_extra_login_and_precedes_every_other_check`
for `IsFull` with `>`) or noted in a comment where a test has to live with it
(the packets 110/120/121/122/98/22 that only leave with the next flushed
packet, the doubled 41 for a taken flag). When such a bug is fixed, those
tests are updated in the same commit.

No e2e test is `xfail` at the moment.

## Flaky quarantine

Empty. A test that cannot be made deterministic within a reasonable effort
is marked `Skip = "flaky: <reason>"` (xunit) or `@pytest.mark.skip(reason="flaky: ...")`
and listed here with what was tried; until then every test in the suite is
expected to pass on every run. Two tests were fixed rather than quarantined
while integrating the areas:

* `Protocol/CombatTests.Grappling_hook_fires_is_rate_limited_and_releases`
  scanned seven fixed aim angles from the random spawn for terrain 80..400 px
  away and failed for some spawns; it now sweeps the full circle in 1 degree
  steps and picks the candidate nearest the middle of the window.
* `World/ServerExplosionDamageTests.Explosion_damage_is_half_the_distance_ratio_...`
  asserted `health > 99.9` for a point-blank explosion, but the settled
  position's fractional part makes the distance anything below 1 px, so the
  formula it had just asserted allows health down to 98.72; the bound is now
  the one that follows from `d < 1`.

Things not covered yet (from the area catalogues): e2e items E2E-12 (CLICK2
sound on weapon select), E2E-14 (ammo HUD at clip 0), E2E-18 (Steam overlay
callback), E2E-19 (forced scoreboard at game over via `maxcaptures`), E2E-20
(blur/camera shake after an explosion), E2E-22 (StatusDialog Cancel while
waiting), E2E-27 (MultiplayerView lobby); TOOLS-09's running-client half
(`Identity.username`/`uid` in a real `VexillumGame`) and TOOLS-23's
client-side `AssetManager` cache identity, which need a window; PHYS-33's
"real client logs `Set terrain state`" clause; PHYS-29 runs headless
(no stance instance on a real server).

## Reference hashes

`terrain_reference` (pure-Python oracle) for the shipped maps, also asserted
by `LevelTerrainTests` and the acceptance self-tests:

```
bases    059ee417b1b64d17f7ebe439dd8bf5b05f01177866873db6dbcd4947716987e3   3914x1024
complex  d5d14f4c61b455f074f82222ad6da7a5a491bf4ddcd53bde8056c3f2974695b6   2736x818
```
