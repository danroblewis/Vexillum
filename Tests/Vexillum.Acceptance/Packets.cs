using System;
using System.Collections.Generic;
using Vexillum.util;

namespace Vexillum.Acceptance
{
    /// <summary>Base of every decoded server → client packet (docs/PROTOCOL.md).</summary>
    public abstract class ServerPacket
    {
        /// <summary>Packet id byte.</summary>
        public byte Id;
        /// <summary>0-based arrival index in the client's packet list.</summary>
        public int Sequence;
        public DateTime ReceivedAt;
        public override string ToString() { return GetType().Name.Replace("Packet", "") + "#" + Sequence; }
    }

    public sealed class PingPacket : ServerPacket { }

    public sealed class ServerIdPacket : ServerPacket
    {
        public byte ProtocolVersion;
        public string MapName;
        public override string ToString() { return base.ToString() + "(v" + ProtocolVersion + " map=" + MapName + ")"; }
    }

    public sealed class TerrainStatePacket : ServerPacket
    {
        public int CompressedLength;
        public byte[] Compressed;
        /// <summary>The decompressed TerrainArray.ToBytes() bitfield.</summary>
        public byte[] Bits;
        public override string ToString() { return base.ToString() + "(" + CompressedLength + " -> " + Bits.Length + " bytes)"; }
    }

    public sealed class EntityListEntry
    {
        public short Id;
        public byte TypeIndex;
        public string TypeName;
        public Vec2 Position;
        /// <summary>Only for HumanoidEntity.</summary>
        public global::Vexillum.Game.PlayerClass? Class;
        public override string ToString() { return TypeName + "#" + Id + "@" + Position + (Class != null ? " " + Class : ""); }
    }

    public sealed class EntityListPacket : ServerPacket
    {
        public List<EntityListEntry> Entities = new List<EntityListEntry>();
        public override string ToString() { return base.ToString() + "(" + Entities.Count + ")"; }
    }

    public sealed class PlayerSpawnPacket : ServerPacket
    {
        public short EntityId;
        public ulong SteamId;
        public string Name;
        public float Health;
        public global::Vexillum.Game.PlayerClass Class;
        public byte[] WeaponTypeIndexes;
        public string[] Weapons;
        public byte WeaponIndex;
        public override string ToString() { return base.ToString() + "(" + Name + " #" + EntityId + " " + Class + " hp=" + Health + " weapons=" + string.Join(",", Weapons) + ")"; }
    }

    public sealed class FrameSyncPacket : ServerPacket
    {
        public int Frame;
        /// <summary>True when sent as an int32 (first frame), false for an sbyte delta.</summary>
        public bool Absolute;
    }

    public sealed class LevelFinishPacket : ServerPacket { }

    public sealed class AmmoPacket : ServerPacket
    {
        public byte WeaponIndex, TotalAmmo, ClipAmmo;
        public override string ToString() { return base.ToString() + "(w" + WeaponIndex + " total=" + TotalAmmo + " clip=" + ClipAmmo + ")"; }
    }

    public sealed class WeaponSelectPacket : ServerPacket
    {
        public short EntityId;
        public byte Index;
    }

    public sealed class WeaponFirePacket : ServerPacket
    {
        public short EntityId;
        public byte Index;
    }

    public sealed class ExplodePacket : ServerPacket
    {
        public int Frame;
        public Vec2 Position;
        public short Radius;
        public int Seed;
        public bool Nonlethal;
        public override string ToString() { return base.ToString() + "(f" + Frame + " " + Position + " r=" + Radius + " seed=" + Seed + (Nonlethal ? " nonlethal" : "") + ")"; }
    }

    public sealed class TeleportPacket : ServerPacket
    {
        public Vec2 Position;
    }

    public sealed class HitscanPacket : ServerPacket
    {
        public short EntityId;
    }

    public sealed class ClassChangePacket : ServerPacket
    {
        /// <summary>-1 means the receiving player.</summary>
        public short EntityId;
        public global::Vexillum.Game.PlayerClass Class;
        public string[] Weapons;
    }

    public sealed class HookPacket : ServerPacket
    {
        public int Frame;
        public short PlayerEntityId;
        /// <summary>-1 = hook released.</summary>
        public short HookEntityId;
    }

    /// <summary>One entry of packet 30 (position) or 31 (velocity).</summary>
    public sealed class EntityMotion
    {
        public short Id;
        public Vec2 Vector;
        public byte MovementByte;
        public bool Moving { get { return (MovementByte & 1) != 0; } }
        public bool Direction { get { return (MovementByte & 2) != 0; } }
        public bool Jumping { get { return (MovementByte & 4) != 0; } }
        public override string ToString() { return "#" + Id + " " + Vector + " m=" + MovementByte; }
    }

    public sealed class PositionsPacket : ServerPacket
    {
        public int Frame;
        public List<EntityMotion> Entries = new List<EntityMotion>();
        public EntityMotion For(int id) { foreach (EntityMotion e in Entries) if (e.Id == id) return e; return null; }
    }

    public sealed class VelocitiesPacket : ServerPacket
    {
        public List<EntityMotion> Entries = new List<EntityMotion>();
        public EntityMotion For(int id) { foreach (EntityMotion e in Entries) if (e.Id == id) return e; return null; }
    }

    public sealed class AngleEntry
    {
        public short Id;
        public sbyte Raw;
        public float Angle;
    }

    public sealed class AnglesPacket : ServerPacket
    {
        public List<AngleEntry> Entries = new List<AngleEntry>();
        public AngleEntry For(int id) { foreach (AngleEntry e in Entries) if (e.Id == id) return e; return null; }
    }

    public sealed class EntityCreatePacket : ServerPacket
    {
        public int Frame;
        public short EntityId;
        public byte TypeIndex;
        public string TypeName;
        public Vec2 Position;
        public bool IsPlayer;
        public override string ToString() { return base.ToString() + "(f" + Frame + " " + TypeName + "#" + EntityId + "@" + Position + ")"; }
    }

    public sealed class EntityRemovePacket : ServerPacket
    {
        public int Frame;
        public short EntityId;
    }

    public sealed class ProjectileCreatePacket : ServerPacket
    {
        public int Frame;
        public short EntityId;
        public byte TypeIndex;
        public string TypeName;
        public Vec2 Position;
        public float Angle;
        public short OwnerId;
        public override string ToString() { return base.ToString() + "(f" + Frame + " " + TypeName + "#" + EntityId + "@" + Position + " owner=" + OwnerId + ")"; }
    }

    public sealed class ChatPacket : ServerPacket
    {
        public string Text;
        public override string ToString() { return base.ToString() + "(" + Text + ")"; }
    }

    public sealed class MessagePacket : ServerPacket
    {
        public byte MessageId;
        public string[] Args;
        public short DurationMs;
        public override string ToString() { return base.ToString() + "(id=" + MessageId + " [" + string.Join(",", Args) + "] " + DurationMs + "ms)"; }
    }

    public sealed class SoundPacket : ServerPacket
    {
        public int Frame;
        public byte SoundId;
        public short EntityId;
        /// <summary>Sound ordinal mapped through the Sounds enum declaration order.</summary>
        public global::Vexillum.util.Sounds Sound
        {
            get { Array v = Enum.GetValues(typeof(global::Vexillum.util.Sounds)); return (global::Vexillum.util.Sounds)v.GetValue(SoundId); }
        }
    }

    public sealed class HealthPacket : ServerPacket
    {
        public short EntityId;
        public float Health;
    }

    public sealed class GameModeBytePacket : ServerPacket
    {
        public byte Command;
        public byte Value;
        public override string ToString() { return base.ToString() + "(cmd=" + Command + " v=" + Value + ")"; }
    }

    public sealed class GameModeShortPacket : ServerPacket
    {
        public byte Command;
        public short Value;
        public override string ToString() { return base.ToString() + "(cmd=" + Command + " v=" + Value + ")"; }
    }

    public sealed class GameModeStringPacket : ServerPacket
    {
        public byte Command;
        public string Value;
    }

    public sealed class PingTimesPacket : ServerPacket
    {
        public List<KeyValuePair<short, short>> Entries = new List<KeyValuePair<short, short>>();
    }

    public sealed class ScorePacket : ServerPacket
    {
        public short EntityId;
        public short Score;
    }

    public sealed class LevelBeginPacket : ServerPacket
    {
        public int TotalLength;
    }

    public sealed class LevelChunkPacket : ServerPacket
    {
        public short Length;
        public byte[] Bytes;
    }

    public sealed class LevelEndPacket : ServerPacket
    {
        /// <summary>The whole map payload received through 220/221 (without the magic number).</summary>
        public byte[] MapBytes;
    }

    public sealed class LevelChangingPacket : ServerPacket { }

    public sealed class DisconnectPacket : ServerPacket
    {
        public string Reason;
        public override string ToString() { return base.ToString() + "(" + Reason + ")"; }
    }
}
