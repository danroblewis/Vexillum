# Vexillum wire protocol (version 3)

Derived from `Game/Game/net/Client.cs`, `Game/Game/net/StreamHelper.cs`,
`Server/ServerPlayer.cs`, `Server/Server.cs`. The protocol is an invariant:
a ported client must interoperate with the shipped `Test/VexillumServer.exe`
and vice versa. Change it only by bumping `VexillumConstants.PROTOCOL_VERSION`
on purpose, in both directions, in one commit.

## Transport

* TCP, default port `VexillumConstants.DEFAULT_PORT` = 24224.
* Both directions: **little-endian** `MiscUtil.IO.EndianBinaryReader/Writer`
  (`EndianBitConverter.Little`). Strings use the .NET `BinaryWriter` format
  (7-bit encoded byte length, UTF-8).
* No framing. Each message starts with a one-byte command id followed by the
  fields below. A reader that desynchronises is disconnected with
  "Invalid command".
* Status probe: a fresh connection that sends the single byte `255` receives a
  single `bool` (server ready) and is closed.

## Primitive encodings (`StreamHelper`)

| name | encoding |
|---|---|
| Vec2 | `int16 x`, `int16 y` (truncated to integers) |
| Angle | `sbyte` = `radians / π * 127` |
| MovementData | 1 byte `BitArray`: bit0 moving, bit1 direction (true = right), bit2 jumping |
| Enum | `byte` ordinal index into `Enum.GetValues(type)` (declaration order) |
| EntityType | `byte` index into `StreamHelper.entityTypes` (see table) |
| FrameByte | first time (`cFrame == -1`): `int32`; afterwards `sbyte` delta from previous frame |
| KeyAction | `int16` ordinal in `KeyAction` declaration order |
| Color | 3 bytes r, g, b |

### Entity type table (index → fully qualified type name, order is wire format)

```
0  Vexillum.Entities.BlueFlagEntity
1  Vexillum.Entities.CrateEntity
2  Vexillum.Entities.GrapplingHook
3  Vexillum.Entities.GreenFlagEntity
4  Vexillum.Entities.HumanoidEntity
5  Vexillum.Entities.Rocket
6  Vexillum.Entities.Weapons.RocketLauncher
7  Vexillum.Entities.NullEntity        (does not exist in source; index reserved)
8  Vexillum.Entities.Weapons.ClusterBombLauncher
9  Vexillum.Entities.Weapons.SMG
10 Vexillum.Entities.Weapons.Sword
11 Vexillum.Entities.PixelEntity
12 Vexillum.Entities.ClusterBomb
13 Vexillum.Entities.Bomblet
```

### Enums whose declaration order is wire format

* `PlayerClass`: Green, Blue, Spectator, None
* `KeyAction`: None, Move_Left, Move_Right, Move_Down, Jump, Reload, Chat, SendChat, Pause, GrapplingHook, Show_Scoreboard
* `Sounds`: WALK1..WALK5, ROCKET, EXPLOSION, SMG, CLICK, CLICK2, SWORD1, SWORD2, SWORD3 (packet 98 sends the ordinal)
* `GameModeCommand` constants 0..7 (byte values, see below)

## Connection lifecycle

```
client                                  server
  ── 1 Login(steamId,name,ticket) ──►
  ◄── 2 ServerID(protocol=3, mapName) ──
  (map exists locally?)  yes ──► 2 Status(ready=true, md5)
                         no  ──► 2 Status(ready=false, zeros)
                              ◄── 220 LevelBegin(len) / 221 chunks / 222 LevelEnd
                              ──► 2 Status(ready=true, md5)
  ◄── 3 TerrainState(lzma bitfield)
  ◄── 4 EntityList
  ◄── 5 PlayerSpawn (self)  ◄── 5 PlayerSpawn (others)
  ◄── 9 LevelFinish            → client creates GameView, starts position thread
  ◄── 130 pings, 131 scores, 120-122 gamemode state, 60/61 chat
  ... steady state: 8/30/31/32 from server every 50-100 ms, 14-19 from client every 100 ms
```

If the md5 does not match the server's copy, the server sends the map.
The server currently accepts any Steam ticket (auth failure path is commented
out) and only checks: server full, banned name, name length 1..128, duplicate name.

## Client → server

| id | name | payload |
|---|---|---|
| 0 | Ping reply | none |
| 1 | Login | `uint64 steamId`, `string name`, `int32 ticketLen`, `bytes ticket` |
| 2 | Status | `bool ready`, `byte[16] mapMd5` |
| 10 | Select weapon (legacy) | `byte index` |
| 11 | Weapon activate | `byte button` (0 L, 1 M, 2 R, 255 fire grapple, 254 release grapple), `byte down` (1/0), `float armAngle` |
| 12 | Weapon action | KeyAction |
| 13 | Select weapon | `byte index` |
| 14 | Position unchanged | none |
| 15 | Position unchanged + movement | Angle, MovementData |
| 16 | Position delta | `sbyte dx`, `sbyte dy` |
| 17 | Position delta + movement | `sbyte dx`, `sbyte dy`, Angle, MovementData |
| 18 | Position absolute | Vec2 |
| 19 | Position absolute + movement | Vec2, Angle, MovementData |
| 20 | Hitscan | Angle, Vec2 origin |
| 60 | Chat | `string` (leading `/` = command: spec, newgame, ban, unban, kick, op, deop, green, blue; `/lag <ms>` is client-local) |
| 255 | Status probe | none (answered with one `bool`, then closed) |

## Server → client

| id | name | payload |
|---|---|---|
| 0 | Ping | none |
| 2 | Server ID | `byte protocolVersion`, `string mapName` |
| 3 | Terrain state | `int32 len`, LZMA bytes → `TerrainArray.SetBytes` |
| 4 | Entity list | `int16 count`, then per entity: `int16 id`, EntityType, Vec2 pos, and for HumanoidEntity an extra Enum(PlayerClass) |
| 5 | Player spawn | `int16 entityId`, `uint64 steamId`, `string name`, `float health`, Enum(PlayerClass), `byte nWeapons`, nWeapons × EntityType, `byte weaponIndex` |
| 8 | Frame sync | FrameByte |
| 9 | Level finish | none |
| 11 | Ammo | `byte weaponIndex`, `byte totalAmmo`, `byte clipAmmo` |
| 13 | Weapon select | `int16 entityId`, `byte index` |
| 14 | Weapon fire | `int16 entityId`, `byte index` |
| 15 | Explode | `int32 frame`, Vec2, `int16 radius`, `int32 seed`, `bool nonlethal` |
| 18 | Teleport self | Vec2 |
| 20 | Hitscan | `int16 entityId` |
| 21 | Class change | `int16 entityId`, Enum(PlayerClass), `byte nWeapons`, nWeapons × EntityType |
| 22 | Grappling hook | `int32 frame`, `int16 playerEntityId`, `int16 hookEntityId` (-1 = none) |
| 30 | Positions | FrameByte, `int16 count`, count × (`int16 id`, Vec2, MovementData) |
| 31 | Velocities | `int16 count`, count × (`int16 id`, Vec2, MovementData) |
| 32 | Arm angles | `int16 count`, count × (`int16 id`, Angle) |
| 40 | Entity create | `int32 frame`, `int16 id`, EntityType, Vec2, `bool isPlayer` |
| 41 | Entity remove | `int32 frame`, `int16 id` |
| 42 | Projectile create | `int32 frame`, `int16 id`, EntityType, Vec2, `float angle`, `int16 ownerId` |
| 60 | Chat line | `string` |
| 61 | Message | `byte messageId` (`Messages.*`), `byte nArgs`, nArgs × `string`, `int16 durationMs` |
| 98 | Sound | `int32 frame`, `byte soundOrdinal`, `int16 entityId` |
| 110 | Health | `int16 entityId`, `float health` |
| 120 | Gamemode byte | `byte cmd`, `byte value` |
| 121 | Gamemode short | `byte cmd`, `int16 value` |
| 122 | Gamemode string | `byte cmd`, `string value` |
| 130 | Ping times | `byte n`, n × (`int16 entityId`, `int16 ms`) |
| 131 | Score | `int16 entityId`, `int16 score` |
| 220 | Level begin | `int32 totalLen` |
| 221 | Level chunk | `int16 len`, bytes (max 1020) |
| 222 | Level end | none → client writes `Maps/<name>.map` (magic + bytes) |
| 253 | Level changing | none → client disconnects and polls status until ready |
| 254 | Disconnect | `string reason` |

`GameModeCommand`: 0 GREENFLAG_CARRIER (short entityId or -1), 1
BLUEFLAG_CARRIER, 2 GREEN_SCORE (byte), 3 BLUE_SCORE (byte), 4 MAX_CAPTURES
(byte), 5 GREEN_WIN, 6 BLUE_WIN, 7 DEATH.

## Timing

* Client sends a position packet every 100 ms (`SendPlayerPosition`), choosing
  14/16/18 (or 15/17/19 when movement or arm angle changed).
* Server `Entity Updater` thread sends 31 every 50 ms and 30+31+32 every
  100 ms, only for entities whose state changed for that client.
* Ping every 5 s each way; 15 s without a ping = disconnect.
* Server keeps `MAX_PING / TIME_PER_FRAME` = 312 historical frames of entity
  boxes for lag-compensated hitscans (`FrameList`); a hitscan whose client
  frame is older than that disconnects the player with "Too much lag".

## Lobby protocol (`MPClient`, dead service)

Separate connection to a chat server (`127.0.0.1:34224` in source),
**big-endian**, every message prefixed with `int16 length`, strings as
`int16 len` + UTF-8 ("JString"). Commands: 0 ping, 1 login(name, key),
2 chat(text). Keep it compiling but treat it as disabled.
