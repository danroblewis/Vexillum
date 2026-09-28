using System;
using System.Collections.Generic;

namespace Vexillum.Acceptance
{
    /// <summary>
    /// Wire-format constants of docs/PROTOCOL.md that the harness needs
    /// independently of the game (so a regression in the game shows up as a
    /// mismatch instead of being decoded "correctly" by the same code). The
    /// self-test <c>ProtocolTableMatchesStreamHelper</c> checks this copy
    /// against <c>StreamHelper.entityTypes</c>.
    /// </summary>
    public static class Protocol
    {
        public const int ProtocolVersion = 3;
        public const int DefaultPort = 24224;
        public const int MapMagic = 0x004F876B;

        /// <summary>EntityType byte → fully qualified type name (index 7 is reserved and has no class).</summary>
        public static readonly string[] EntityTypes = new string[]
        {
            "Vexillum.Entities.BlueFlagEntity",
            "Vexillum.Entities.CrateEntity",
            "Vexillum.Entities.GrapplingHook",
            "Vexillum.Entities.GreenFlagEntity",
            "Vexillum.Entities.HumanoidEntity",
            "Vexillum.Entities.Rocket",
            "Vexillum.Entities.Weapons.RocketLauncher",
            "Vexillum.Entities.NullEntity",
            "Vexillum.Entities.Weapons.ClusterBombLauncher",
            "Vexillum.Entities.Weapons.SMG",
            "Vexillum.Entities.Weapons.Sword",
            "Vexillum.Entities.PixelEntity",
            "Vexillum.Entities.ClusterBomb",
            "Vexillum.Entities.Bomblet",
        };

        /// <summary>Index of a type name in <see cref="EntityTypes"/>, -1 if absent.</summary>
        public static int EntityTypeIndex(string fullName)
        {
            return Array.IndexOf(EntityTypes, fullName);
        }

        /// <summary>Short class name of an EntityType byte ("HumanoidEntity"), or "?&lt;n&gt;".</summary>
        public static string EntityTypeName(int index)
        {
            if (index < 0 || index >= EntityTypes.Length)
                return "?" + index;
            string n = EntityTypes[index];
            return n.Substring(n.LastIndexOf('.') + 1);
        }

        /// <summary>Client → server packet ids.</summary>
        public static class C2S
        {
            public const byte PingReply = 0, Login = 1, Status = 2, SelectWeaponLegacy = 10, WeaponActivate = 11,
                WeaponAction = 12, SelectWeapon = 13, PositionUnchanged = 14, PositionUnchangedMovement = 15,
                PositionDelta = 16, PositionDeltaMovement = 17, PositionAbsolute = 18, PositionAbsoluteMovement = 19,
                Hitscan = 20, Chat = 60, StatusProbe = 255;
        }

        /// <summary>Server → client packet ids.</summary>
        public static class S2C
        {
            public const byte Ping = 0, ServerId = 2, TerrainState = 3, EntityList = 4, PlayerSpawn = 5, FrameSync = 8,
                LevelFinish = 9, Ammo = 11, WeaponSelect = 13, WeaponFire = 14, Explode = 15, Teleport = 18, Hitscan = 20,
                ClassChange = 21, Hook = 22, Positions = 30, Velocities = 31, Angles = 32, EntityCreate = 40,
                EntityRemove = 41, ProjectileCreate = 42, Chat = 60, Message = 61, Sound = 98, Health = 110,
                GameModeByte = 120, GameModeShort = 121, GameModeString = 122, PingTimes = 130, Score = 131,
                LevelBegin = 220, LevelChunk = 221, LevelEnd = 222, LevelChanging = 253, Disconnect = 254;
        }

        /// <summary>GameModeCommand values (Game/Game/game/GameModeCommand.cs).</summary>
        public static class GameMode
        {
            public const byte GreenFlagCarrier = 0, BlueFlagCarrier = 1, GreenScore = 2, BlueScore = 3, MaxCaptures = 4,
                GreenWin = 5, BlueWin = 6, Death = 7;
        }

        /// <summary>Weapon activate button codes (packet 11).</summary>
        public static class Button
        {
            public const byte Left = 0, Middle = 1, Right = 2, FireGrapple = 255, ReleaseGrapple = 254;
        }

        /// <summary>Angle → sbyte exactly as StreamHelper.WriteAngle.</summary>
        public static sbyte EncodeAngle(float radians)
        {
            return (sbyte)((radians / Math.PI) * 127);
        }

        /// <summary>sbyte → radians exactly as StreamHelper.ReadAngle.</summary>
        public static float DecodeAngle(sbyte raw)
        {
            return ((float)raw / 127) * (float)Math.PI;
        }

        /// <summary>MovementData byte: bit0 moving, bit1 direction (right), bit2 jumping.</summary>
        public static byte EncodeMovement(bool moving, bool direction, bool jumping)
        {
            return (byte)((moving ? 1 : 0) | (direction ? 2 : 0) | (jumping ? 4 : 0));
        }
    }
}
