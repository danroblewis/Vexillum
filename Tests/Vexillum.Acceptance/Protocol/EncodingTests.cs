using System;
using System.IO;
using MiscUtil.Conversion;
using MiscUtil.IO;
using Vexillum.Entities;
using Vexillum.Entities.Weapons;
using Vexillum.Game;
using Vexillum.net;
using Vexillum.util;
using Xunit;

namespace Vexillum.Acceptance.protocol
{
    /// <summary>
    /// Golden byte tests of the primitive encodings in docs/PROTOCOL.md, driven
    /// through the real StreamHelper over a MemoryStream (no process). Entity
    /// instances are needed for WriteEntityType, so the class runs in the
    /// serial game-state collection with Util.IsServer = true (no textures).
    /// </summary>
    [Collection(GameStateCollection.Name)]
    public class EncodingTests
    {
        private static byte[] Encode(Action<StreamHelper, EndianBinaryWriter> write)
        {
            MemoryStream ms = new MemoryStream();
            EndianBinaryWriter w = new EndianBinaryWriter(EndianBitConverter.Little, ms);
            StreamHelper h = new StreamHelper(null, w);
            write(h, w);
            w.Flush();
            return ms.ToArray();
        }

        private static T Decode<T>(byte[] bytes, Func<StreamHelper, EndianBinaryReader, T> read)
        {
            EndianBinaryReader r = new EndianBinaryReader(EndianBitConverter.Little, new MemoryStream(bytes));
            StreamHelper h = new StreamHelper(r, null);
            T t = read(h, r);
            Assert.Equal(bytes.Length, r.BaseStream.Position); // every byte consumed, none left over
            return t;
        }

        [Theory]
        [InlineData(0f, 0)]
        [InlineData((float)Math.PI, 127)]
        [InlineData(-(float)Math.PI, -127)]
        [InlineData((float)(Math.PI / 2), 63)]      // 0.5 * 127 = 63.5 truncated
        [InlineData(-(float)(Math.PI / 2), -63)]
        [InlineData((float)(Math.PI / 4), 31)]
        public void Angle_is_one_signed_byte_radians_over_pi_times_127(float radians, int expected)
        {
            byte[] b = Encode((h, w) => h.WriteAngle(radians));
            Assert.Equal(new byte[] { (byte)(sbyte)expected }, b);
            float back = Decode(b, (h, r) => h.ReadAngle());
            Assert.Equal(expected / 127f * (float)Math.PI, back, 5);
        }

        [Fact]
        public void Angle_round_trip_loses_at_most_one_step_of_127()
        {
            for (float a = -3.14f; a <= 3.14f; a += 0.037f)
            {
                float back = Decode(Encode((h, w) => h.WriteAngle(a)), (h, r) => h.ReadAngle());
                Assert.True(Math.Abs(back - a) <= Math.PI / 127 + 1e-5, "angle " + a + " came back as " + back);
            }
        }

        [Fact]
        public void Vec2_is_two_little_endian_int16_truncated_toward_zero()
        {
            byte[] b = Encode((h, w) => h.WriteVec2(new Vec2(700.7f, -3.9f)));
            Assert.Equal(new byte[] { 0xBC, 0x02, 0xFD, 0xFF }, b);   // 700 = 0x02BC, -3 = 0xFFFD
            Vec2 v = Decode(b, (h, r) => h.ReadVec2());
            Assert.Equal(700f, v.X);
            Assert.Equal(-3f, v.Y);
        }

        [Fact]
        public void Vec2_of_a_map_corner_survives_the_round_trip()
        {
            Vec2 v = Decode(Encode((h, w) => h.WriteVec2(new Vec2(3913, 1023))), (h, r) => h.ReadVec2());
            Assert.Equal(new Vec2(3913, 1023), v);
        }

        [Theory]
        [InlineData(false, false, false, 0)]
        [InlineData(true, false, false, 1)]
        [InlineData(false, true, false, 2)]
        [InlineData(false, false, true, 4)]
        [InlineData(true, true, false, 3)]
        [InlineData(true, true, true, 7)]
        public void MovementData_packs_moving_direction_jumping_into_bits_0_1_2(bool moving, bool direction, bool jumping, int expected)
        {
            byte[] b = Encode((h, w) => h.WriteMovementData(moving, direction, jumping));
            Assert.Equal(new byte[] { (byte)expected }, b);
            System.Collections.BitArray bits = Decode(b, (h, r) => h.ReadMovementData());
            Assert.Equal(8, bits.Length);
            Assert.Equal(moving, bits[0]);
            Assert.Equal(direction, bits[1]);
            Assert.Equal(jumping, bits[2]);
        }

        [Fact]
        public void FrameByte_is_int32_the_first_time_and_an_sbyte_delta_afterwards()
        {
            byte[] first = Encode((h, w) => h.WriteFrameByte(-1, 1000));
            Assert.Equal(new byte[] { 0xE8, 0x03, 0x00, 0x00 }, first);
            Assert.Equal(1000, Decode(first, (h, r) => h.ReadFrameByte(-1)));

            byte[] delta = Encode((h, w) => h.WriteFrameByte(1000, 1006));
            Assert.Equal(new byte[] { 6 }, delta);
            Assert.Equal(1006, Decode(delta, (h, r) => h.ReadFrameByte(1000)));

            byte[] negative = Encode((h, w) => h.WriteFrameByte(1000, 998));
            Assert.Equal(new byte[] { 0xFE }, negative);
            Assert.Equal(998, Decode(negative, (h, r) => h.ReadFrameByte(1000)));
        }

        [Fact]
        public void FrameByte_delta_beyond_127_wraps_silently()
        {
            // A gap of 200 frames (3.3 s without a position packet) cannot be
            // represented: the sbyte cast wraps and the receiver goes back 56 frames.
            byte[] delta = Encode((h, w) => h.WriteFrameByte(1000, 1200));
            Assert.Equal(new byte[] { unchecked((byte)(sbyte)200) }, delta);
            Assert.Equal(1000 - 56, Decode(delta, (h, r) => h.ReadFrameByte(1000)));
        }

        [Theory]
        [InlineData(PlayerClass.Green, 0)]
        [InlineData(PlayerClass.Blue, 1)]
        [InlineData(PlayerClass.Spectator, 2)]
        [InlineData(PlayerClass.None, 3)]
        public void Enum_is_one_byte_ordinal_in_declaration_order(PlayerClass value, int ordinal)
        {
            byte[] b = Encode((h, w) => h.WriteEnum(value));
            Assert.Equal(new byte[] { (byte)ordinal }, b);
            Assert.Equal(value, (PlayerClass)Decode(b, (h, r) => h.ReadEnum(typeof(PlayerClass))));
        }

        [Theory]
        [InlineData(KeyAction.None, 0)]
        [InlineData(KeyAction.Jump, 4)]
        [InlineData(KeyAction.Reload, 5)]
        [InlineData(KeyAction.GrapplingHook, 9)]
        [InlineData(KeyAction.Show_Scoreboard, 10)]
        public void KeyAction_is_an_int16_ordinal(KeyAction action, int ordinal)
        {
            byte[] b = Encode((h, w) => h.WriteAction(action));
            Assert.Equal(new byte[] { (byte)ordinal, 0 }, b);
            Assert.Equal(action, Decode(b, (h, r) => h.ReadAction()));
        }

        [Fact]
        public void Sound_ordinals_follow_the_Sounds_declaration_order()
        {
            Assert.Equal(5, global::Vexillum.AssetManager.GetIndex(Sounds.ROCKET));
            Assert.Equal(6, global::Vexillum.AssetManager.GetIndex(Sounds.EXPLOSION));
            Assert.Equal(7, global::Vexillum.AssetManager.GetIndex(Sounds.SMG));
            Assert.Equal(12, global::Vexillum.AssetManager.GetIndex(Sounds.SWORD3));
        }

        [Fact]
        public void EntityType_byte_is_the_index_of_the_runtime_type_in_the_table()
        {
            bool was = Util.IsServer;
            Util.IsServer = true; // static constructors of entities load no textures on the server
            try
            {
                Assert.Equal(new byte[] { 0 }, Encode((h, w) => h.WriteEntityType(new BlueFlagEntity())));
                Assert.Equal(new byte[] { 1 }, Encode((h, w) => h.WriteEntityType(new CrateEntity())));
                Assert.Equal(new byte[] { 2 }, Encode((h, w) => h.WriteEntityType(new GrapplingHook())));
                Assert.Equal(new byte[] { 3 }, Encode((h, w) => h.WriteEntityType(new GreenFlagEntity())));
                Assert.Equal(new byte[] { 6 }, Encode((h, w) => h.WriteEntityType(new RocketLauncher())));
                Assert.Equal(new byte[] { 9 }, Encode((h, w) => h.WriteEntityType(new SMG())));
                Assert.Equal(new byte[] { 10 }, Encode((h, w) => h.WriteEntityType(new Sword())));

                Assert.Equal(typeof(HumanoidEntity), Decode(new byte[] { 4 }, (h, r) => h.ReadEntityType()));
                Assert.Equal("Vexillum.Entities.Rocket", Decode(new byte[] { 5 }, (h, r) => h.ReadEntityType()).FullName);
                Assert.Equal(typeof(SMG), Decode(new byte[] { 9 }, (h, r) => h.ReadEntityType()));
                // Index 7 (NullEntity) has no class: the reader yields null and a
                // sender never produces it (IndexOf of an unknown type would be -1 = 0xFF).
                Assert.Null(Decode(new byte[] { 7 }, (h, r) => h.ReadEntityType()));
            }
            finally
            {
                Util.IsServer = was;
            }
        }

        [Fact]
        public void Strings_use_the_seven_bit_length_prefix_and_utf8()
        {
            byte[] b = Encode((h, w) => w.Write("alice"));
            Assert.Equal(new byte[] { 5, (byte)'a', (byte)'l', (byte)'i', (byte)'c', (byte)'e' }, b);
            Assert.Equal("alice", Decode(b, (h, r) => r.ReadString()));
            byte[] chat = Encode((h, w) => w.Write(Colour.Orange + "x"));
            Assert.Equal(new byte[] { 4, 0xC2, 0xA7, (byte)'9', (byte)'x' }, chat); // '§' is two UTF-8 bytes, length counts bytes
            byte[] max = Encode((h, w) => w.Write(new string('x', 127)));
            Assert.Equal(127, max[0]);
            Assert.Equal(128, max.Length);
            Assert.Equal(127, Decode(max, (h, r) => r.ReadString()).Length);
        }

        // Known original bug (docs/PORTING.md): MiscUtil's EndianBinaryWriter.Write7BitEncodedInt
        // advances its buffer index twice per continuation byte, so every string of
        // 128 or more UTF-8 bytes gets a stray 0x00 after the first length byte. The
        // reader decodes lengths correctly, so the two sides desynchronise: a
        // 200-byte string is framed as 72 bytes followed by garbage.
        [Fact]
        public void Strings_of_128_bytes_or_more_are_misframed_by_the_writer()
        {
            byte[] longer = Encode((h, w) => w.Write(new string('x', 200)));
            Assert.Equal(new byte[] { 0xC8, 0x00, 0x01 }, new byte[] { longer[0], longer[1], longer[2] });
            Assert.Equal(203, longer.Length);
            EndianBinaryReader r = new EndianBinaryReader(EndianBitConverter.Little, new MemoryStream(longer));
            Assert.Equal(72, r.Read7BitEncodedInt());            // 0xC8 & 0x7F, then the stray 0x00 ends the number
        }

        [Fact(Skip = "Known original bug: MiscUtil Write7BitEncodedInt inserts a zero byte for lengths >= 128, so strings of 128+ bytes cannot cross the wire, docs/PORTING.md")]
        public void Strings_of_128_bytes_or_more_round_trip()
        {
            byte[] longer = Encode((h, w) => w.Write(new string('x', 200)));
            Assert.Equal(new byte[] { 0xC8, 0x01 }, new byte[] { longer[0], longer[1] });  // 200 = 0x48 | 0x80, 0x01
            Assert.Equal(202, longer.Length);
            Assert.Equal(200, Decode(longer, (h, r) => r.ReadString()).Length);
        }

        [Fact]
        public void Color_is_three_bytes_r_g_b()
        {
            byte[] b = Encode((h, w) => h.WriteColor(new Microsoft.Xna.Framework.Color(10, 20, 30)));
            Assert.Equal(new byte[] { 10, 20, 30 }, b);
            Microsoft.Xna.Framework.Color c = Decode(b, (h, r) => h.ReadColor());
            Assert.Equal(new Microsoft.Xna.Framework.Color(10, 20, 30), c);
        }
    }
}
