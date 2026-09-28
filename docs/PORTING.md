# Porting notes

The 2013 sources compile unchanged apart from lines marked `// PORT:`; missing
libraries are provided by the `Shims/` projects and the Nuclex submodule. This
file records the original bugs that the acceptance tests document with `Skip`
(the port does not fix them).

## Known original bugs (do not fix silently; log here and fix deliberately)

* `Util.EscapeUriString` builds `sb` then returns the unescaped `value`.
* `Util.ParseServerConfig` reports a bad line as `"line " + l+1`: string
  concatenation, so the zero-based counter `l` and the literal `1` are
  glued together (`line 11` for the second line, `line 01` for the first)
  instead of the one-based number. Found by the acceptance tests
  (`Tests/Vexillum.Acceptance/Tools/ServerConfigTests.cs`).
* `ServerPlayer.SetMovement` compares `movement[2]` against `direction` and
  `movement[3]` against `jumping` (indices shifted by one) before assigning
  correctly; the net effect is that `movementChanged` fires too often.
* `Server.Chat` `/kick` and `/ban` compare `p.name` (the issuer) instead of
  `other.name`, so they only ever hit the issuer. The command also splits on
  spaces, so a name containing a space (every offline identity, which is
  `<random> <persona>`) can never be named at all; the e2e test
  `test_disconnect_by_server_shows_the_reason_and_returns_to_the_menu`
  renames its client to a one-word op name for that reason.
* `ServerDialog.ShowHostDialog` checks for `./ops.txt` but writes
  `Server/ops.txt`, so on a platform where `VexillumServerStart.exe` starts
  it rewrites the ops file with the current username on every press (found
  while writing `Tests/e2e/test_main_menu.py`; here `Process.Start` throws
  first, so nothing is written).
* `Server.IsFull` uses `>` so `maxPlayers + 1` players can join.
* `Server.UpdateBots` loops forever on the `Server Main` thread when
  `RemoveBot` has to remove a bot but finds none of the class it picks
  (`numBlue > numGreen ? Blue : Green`, computed from counts that still
  include the joining/leaving human): every `AddTask` then stalls and the
  server stops answering, although the acceptor thread keeps accepting.
  Reproduced by `maxbots 0` with one human joining (`maxAllowedBots` = -1
  and there is no bot to remove) and by `maxbots 2` when a second human
  joins while the single bot is on the other team. Found by the acceptance
  harness (Tests/Vexillum.Acceptance, `KnownServerBugs`); tests keep
  `maxbots` at the default 6 or use `maxbots 1` for single-client runs.
  Also reproduced by a level change (`/newgame`, a win) that drops two
  connected humans: `setLevel` adds `maxbots` bots whose `AddPlayer` tasks
  interleave with the two removals, and the second removal can meet a tied
  count with no bot on the team it picks. Note that `players.Count` includes
  every accepted connection, logged in or not, so an idle socket counts as a
  human for the fill. The gameplay tests (Tests/Vexillum.Acceptance/Gameplay,
  `GameplayFixture`) keep `Server.maxBots` equal to the number of open
  connections through the debug console and change level with a single
  connected client.
* `PlayerList.Save` opens `<list>.txt` in the working directory with
  `FileMode.Truncate` while `Load` reads `Server/<list>.txt`, so `/op`,
  `/deop`, `/ban` and `/unban` never persist ("Error saving ops list" in the
  log); the in-memory list works until the server restarts.
* `SurvivalGameMode.PlayerHealthChanged` queues the health packet (110)
  through `Server.SendPlayerHealth` → `AddTask`. When the death is triggered
  off the `Server Main` thread (the `/green` and `/blue` chat commands →
  `ResetPlayer`), `SetClass(Spectator)` → `SetType` has reset `Health` to
  `MaxHealth` by the time the task runs, so the clients are told health 100
  for a player that just died. Deaths on the game thread (hitscans,
  explosions) report 0.
* `SurvivalGameMode.TakeFlag` removes the taken flag twice (`RemoveFlag`,
  then `level.TakeFlag` → `RemoveFlag`), so every client receives two 41
  packets for the flag entity.
* `SurvivalGameMode.OnFlagCollide` is asymmetric: a green player cannot pick
  up a dropped blue flag while either drop timer runs, a blue player picks up
  a dropped green flag at once (no guard on the blue branch).
* `ServerPlayer.SendPlayerHealth`, `SendGameModeByte/Short/String`,
  `SendSound` and `SendGrapplingHook` write into the buffer without
  `WriteData`, so 110/120/121/122/98/22 only leave with the next flushed
  packet (a capture arrives as 40, then 121 and 120 with the 131; a hitscan's
  110 waits for the next 30/31 broadcast).
* MiscUtil's `EndianBinaryWriter.Write7BitEncodedInt` (the vendored 2013
  copy and the `JTForks.MiscUtil` package alike) advances its buffer index
  twice per continuation byte, so every string of 128 or more UTF-8 bytes
  is written with a stray `0x00` after the first length byte while the
  reader decodes lengths correctly. Both directions desynchronise on such
  a string: a 128-character name is read as an empty name followed by a
  garbage ticket length, and a chat line whose coloured display name plus
  text reaches 128 bytes breaks the client's stream. In practice the name
  limit the wire enforces is 127, not the 128 `ServerPlayer` checks. Found
  by the protocol acceptance tests (`Protocol/EncodingTests`,
  `Protocol/RejectionTests`).
* `Vexillum.BeginSpriteBatch(Effect)` ignores the effect parameter (see
  ARCHITECTURE.md rendering notes). Behaviour depends on Immediate mode.
* `Level.Explode(int,int,int,Player,Weapon)` seeds `Random` with
  `DateTime.Now.Millisecond`, so client-initiated explosions are not
  deterministic across peers (server-initiated ones send the seed).
* `TextRenderer.DrawFormattedString` draws one `DrawString` per character.
  Slow but it defines the exact kerning the UI was laid out for.
* `HumanoidEntity.Health` setter calls `Level.OnEntityDeath` while `Level`
  can be null during construction (`SetType` sets `Health` before the entity
  is added). Works today because `Health = MaxHealth` is nonzero.
* `TerrainArray.ToBytes`/`SetBytes` size the bitfield as `(width*height)/8`
  (integer division) but index it for every pixel: when `width*height` is
  not a multiple of 8, `ToBytes` throws `IndexOutOfRangeException` if a tail
  pixel is solid and `SetBytes` always throws. Both shipped maps have sizes
  divisible by 8, so it never fires today. Found by
  `Tests/Vexillum.Acceptance/World/TerrainArrayTests`.
* `Entity.NextID` hands out id 0 once the `short` counter wraps past -2
  (`CheckIDs` only skips -1 and ids in use), and 0 is the "no entity" value
  of the terrain outline (`TerrainArray.GetEntity`), so such an entity is
  invisible to collisions and explosions. Needs ~65k entity allocations per
  map. Found by `World/EntityOutlineTests`.
* `Entity.tCorner`/`bCorner` are computed only in the `Size` setter (around
  the position at that time, i.e. the origin for a fresh humanoid) and never
  updated by `Position`, so `Entity.TestPoint` and therefore
  `Level.AddHitscan` test a box around the origin instead of the entity. The
  server's hitscan is unaffected (it uses `Frame.EntityDef` corners rebuilt
  every frame). Found by `World/HealthAndHitscanTests`.
* `Level.DrawCircle` normalises `e.Position - explodeCenter` without a zero
  check: an explosion centred exactly on an entity gives it a NaN velocity
  (XNA `Vector2.Normalize` of the zero vector). The entity then never moves
  again (`d` is NaN, the position loop never runs) although damage is still
  computed correctly (`ratio` 1, amount 0). Found by `World/ExplosionTests`.
* `LevelLoader.LoadData` reports a malformed `data.txt` line through
  `Vexillum.Error`, which calls `game.Exit()` on the static `Vexillum.game`;
  that is null on the server (and headless), so the per-line `catch` throws
  a `NullReferenceException` that escapes into the outer `catch`: the loader
  logs "Error loading level:" with a stack trace and returns null instead of
  reporting "Parse error on line N" and going on with the next line (the
  line counter `l` also only advances inside the `try`, so a later report
  would name the wrong line). Found by the acceptance tests
  (`Tests/Vexillum.Acceptance/World/LevelLoaderTests.cs`).
