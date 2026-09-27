using System;
using Nuclex.Input;
using Vexillum.util;
using Xunit;

namespace Vexillum.Acceptance.toolsconfig
{
    /// <summary>
    /// Pure helpers: the Messages table (packet 61 message ids), TextUtil
    /// colour codes and TextRenderer.RemoveColors, Util math and mouse-button
    /// helpers, Util.ValidateUsername.
    /// </summary>
    public class MessagesTests
    {
        // TOOLS-08
        [Fact]
        public void Message_table_formats_the_documented_texts()
        {
            Assert.Equal("Bob§1 joined the game", Messages.Parse(Messages.PLAYER_JOIN, new string[] { "Bob" }));
            Assert.Equal("Bob" + TextUtil.COLOR_WHITE + " left the game", Messages.Parse(Messages.PLAYER_LEAVE, new string[] { "Bob" }));
            Assert.Equal("You were killed by X", Messages.Parse(Messages.KILLED_BY, new string[] { "X" }));
            Assert.Equal("You killed X", Messages.Parse(Messages.YOU_KILLED, new string[] { "X" }));
            Assert.Equal("The §8green§1 team has won the game!", Messages.Parse(Messages.GREEN_WIN, new string[0]));
            Assert.Equal("The §6blue§1 team has won the game!", Messages.Parse(Messages.BLUE_WIN, new string[0]));
            Assert.StartsWith("§9", Messages.Parse(Messages.OURFLAG_TAKEN, new string[0]));
            Assert.StartsWith("§9", Messages.Parse(Messages.OURFLAG_CAPTURED, new string[0]));
            Assert.Equal("hi", Messages.Parse(Messages.CUSTOM, new string[] { "hi" }));
            Assert.Equal("Return the enemy flag to your flag to capture it!", Messages.Parse(Messages.NOOB_INSTRUCTIONS, new string[0]));
            Assert.Equal("You have captured the enemy's flag!", Messages.Parse(Messages.OURFLAG_CAPTURED_1, new string[0]));
        }

        // TOOLS-08: ids 0..16 are the byte in packet 61; every one resolves and $0 is substituted
        [Fact]
        public void Every_message_id_resolves_and_substitutes_its_argument()
        {
            Assert.Equal(0, Messages.CUSTOM);
            Assert.Equal(1, Messages.PLAYER_JOIN);
            Assert.Equal(9, Messages.GREEN_WIN);
            Assert.Equal(14, Messages.KILLED_BY);
            Assert.Equal(16, Messages.YOU_KILLED);
            for (int id = 0; id <= 16; id++)
            {
                string m = Messages.Parse(id, new string[] { "Bob" });
                Assert.False(string.IsNullOrEmpty(m), "message " + id);
                Assert.DoesNotContain("$0", m);
            }
            Assert.Throws<IndexOutOfRangeException>(() => Messages.Parse(17, new string[0]));
        }

        // TOOLS-08
        [Fact]
        public void Colour_codes_are_section_sign_plus_digit_and_RemoveColors_strips_them()
        {
            Assert.Equal('§', TextUtil.colorChar);
            Assert.Equal("§0", TextUtil.COLOR_BLACK);
            Assert.Equal("§1", TextUtil.COLOR_WHITE);
            Assert.Equal("§6", TextUtil.COLOR_BLUE);
            Assert.Equal("§8", TextUtil.COLOR_GREEN);
            Assert.Equal("§9", TextUtil.COLOR_ORANGE);
            Assert.Equal("abc", TextRenderer.RemoveColors("a§1b§9c"));
            Assert.Equal("plain", TextRenderer.RemoveColors("plain"));
            // Only a digit after the sign is a code.
            Assert.Equal("x§y", TextRenderer.RemoveColors("x§y"));
            Assert.Equal("Bob joined the game", TextRenderer.RemoveColors(Messages.Parse(Messages.PLAYER_JOIN, new string[] { "Bob" })));
        }

        // TOOLS-12
        [Fact]
        public void NormalizeAngle_wraps_into_minus_pi_to_pi()
        {
            Assert.Equal(4.0f - 2 * (float)Math.PI, Util.NormalizeAngle(4.0f), 4);
            Assert.Equal(-4.0f + 2 * (float)Math.PI, Util.NormalizeAngle(-4.0f), 4);
            Assert.Equal(3.0f, Util.NormalizeAngle(3.0f));
            Assert.Equal(-3.0f, Util.NormalizeAngle(-3.0f));
            Assert.Equal(0f, Util.NormalizeAngle(0f));
        }

        // TOOLS-12
        [Fact]
        public void RoundToMultiple_rounds_then_multiplies()
        {
            Assert.Equal(5, Util.RoundToMultiple(7.4f, 5));
            Assert.Equal(10, Util.RoundToMultiple(7.6f, 5));
            Assert.Equal(0, Util.RoundToMultiple(2.4f, 5));
            Assert.Equal(-5, Util.RoundToMultiple(-7.4f, 5));
        }

        // TOOLS-12
        [Fact]
        public void Deg_Rad_and_Round_truncate_like_the_original()
        {
            Assert.Equal(180, Util.Deg((float)Math.PI));
            Assert.Equal(89, Util.Deg(Util.Rad(89.9f)));
            Assert.Equal((float)Math.PI, Util.Rad(180), 5);
            Vec2 r = Util.Round(new Vec2(1.9f, -1.9f));
            Assert.Equal(1f, r.X);
            Assert.Equal(-1f, r.Y);
        }

        // TOOLS-12: the button int is the byte in packet 11
        [Fact]
        public void Mouse_button_codes_map_left_middle_right_and_anything_else_to_right()
        {
            Assert.Equal(0, Util.GetMouseButtonInt(MouseButtons.Left));
            Assert.Equal(1, Util.GetMouseButtonInt(MouseButtons.Middle));
            Assert.Equal(2, Util.GetMouseButtonInt(MouseButtons.Right));
            Assert.Equal(2, Util.GetMouseButtonInt(MouseButtons.Left | MouseButtons.Right));
            Assert.Equal(2, Util.GetMouseButtonInt(MouseButtons.X1));

            Assert.Equal(MouseButtons.Left, Util.GetMouseButton(0));
            Assert.Equal(MouseButtons.Middle, Util.GetMouseButton(1));
            Assert.Equal(MouseButtons.Right, Util.GetMouseButton(2));
            Assert.Equal(MouseButtons.Right, Util.GetMouseButton(7));
            for (int i = 0; i < 3; i++)
                Assert.Equal(i, Util.GetMouseButtonInt(Util.GetMouseButton(i)));
        }

        // TOOLS-09
        [Theory]
        [InlineData("Bob_1-x", true)]
        [InlineData("Bob", true)]
        [InlineData("", false)]
        [InlineData("a b", false)]
        [InlineData("ü", false)]
        [InlineData("0.5 dan", false)]
        [InlineData("0.5", false)]
        public void ValidateUsername_accepts_only_ascii_letters_digits_dash_underscore(string name, bool expected)
        {
            Assert.Equal(expected, Util.ValidateUsername(name));
        }

        // TOOLS-09: the offline identity Vexillum.SetSteamIdentity builds ("<double> <persona name>") never validates
        [Fact]
        public void Offline_identity_shape_fails_ValidateUsername()
        {
            // Same expression as Vexillum.SetSteamIdentity (Game/Game/Vexillum.cs:248); it needs a Game instance so it is reproduced here.
            string identity = new Random().NextDouble() + " " + Steamworks.SteamFriends.GetPersonaName();
            Assert.Contains(" ", identity);
            Assert.False(Util.ValidateUsername(identity));
        }
    }
}
