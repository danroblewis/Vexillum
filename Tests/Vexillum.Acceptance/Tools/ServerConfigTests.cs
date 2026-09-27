using System;
using System.IO;
using Vexillum.util;
using Xunit;

namespace Vexillum.Acceptance.toolsconfig
{
    /// <summary>
    /// Util.ParseServerConfig (Game/Game/Util.cs) against the shipped
    /// Test/Server/settings.txt and hand-written variants. Pure function, no
    /// process state.
    /// </summary>
    public class ServerConfigTests
    {
        private static string ShippedSettingsText()
        {
            return File.ReadAllText(Path.Combine(Repo.RuntimeDir, "Server", "settings.txt"));
        }

        // TOOLS-03
        [Fact]
        public void Shipped_settings_txt_parses_every_key()
        {
            Util.ServerConfig sc = Util.ParseServerConfig(ShippedSettingsText());

            Assert.Equal(Protocol.DefaultPort, sc.port);
            Assert.Equal("Vexillum Server", sc.name);
            Assert.Equal(12, sc.maxPlayers);
            Assert.Equal(new string[] { "RocketLauncher", "SMG", "Sword" }, sc.weapons);
            Assert.Equal(4, sc.maxCaptures);
            Assert.Equal(5000, sc.respawnTime);
            Assert.True(sc.verifyNames);
            Assert.True(sc.isPublic);
            Assert.Equal(new string[] { "bases", "complex" }, sc.maps);
            Assert.Equal(6, sc.maxBots);
        }

        // TOOLS-03
        [Fact]
        public void Empty_text_yields_the_ServerConfig_defaults()
        {
            Util.ServerConfig sc = Util.ParseServerConfig("");

            Assert.Equal(VexillumConstants.DEFAULT_PORT, sc.port);
            Assert.Equal(Protocol.DefaultPort, sc.port);
            Assert.Equal("Vexillum Server", sc.name);
            Assert.Equal(12, sc.maxPlayers);
            Assert.Equal(new string[] { "RocketLauncher", "SMG", "Sword" }, sc.weapons);
            Assert.Null(sc.maps);
            Assert.Equal(4, sc.maxCaptures);
            Assert.Equal(5000, sc.respawnTime);
            Assert.True(sc.verifyNames);
            Assert.True(sc.isPublic);
            Assert.Equal(6, sc.maxBots);
        }

        // TOOLS-03: comment lines fall through the switch, a multi-word name is joined with single spaces
        [Fact]
        public void Comment_lines_are_ignored_and_names_keep_their_words()
        {
            Util.ServerConfig sc = Util.ParseServerConfig("#port 1\n# name Commented Out\nport 2\nname  Two  Spaces here\nmaxbots 0\n");

            Assert.Equal(2, sc.port);
            // Split(' ') keeps empty parts; the rebuild joins every part (empty ones
            // included) with one space, so runs of spaces survive.
            Assert.Equal(" Two  Spaces here", sc.name);
            Assert.Equal(0, sc.maxBots);
        }

        // TOOLS-03: CRLF files (the launcher's ReadLine never passes a CR, but a raw read might)
        [Fact]
        public void CRLF_text_parses_the_numeric_and_boolean_keys()
        {
            Util.ServerConfig sc = Util.ParseServerConfig(ShippedSettingsText().Replace("\n", "\r\n"));

            Assert.Equal(Protocol.DefaultPort, sc.port);
            Assert.Equal(12, sc.maxPlayers);
            Assert.Equal(4, sc.maxCaptures);
            Assert.Equal(5000, sc.respawnTime);
            Assert.Equal(6, sc.maxBots);
            Assert.True(sc.verifyNames);
            Assert.True(sc.isPublic);
            // The last word of a list keeps the CR (Split(' ') does not trim it).
            Assert.Equal("complex", sc.maps[1].TrimEnd('\r'));
            Assert.Equal(2, sc.maps.Length);
        }

        // TOOLS-04: pins the author's "line " + l+1 concatenation (string + int + int)
        [Fact]
        public void Parse_error_reports_the_line_with_the_concatenation_quirk()
        {
            Exception ex = Assert.Throws<Exception>(() => Util.ParseServerConfig("name X\nport abc\n"));
            // l == 1 (zero based, second line): "line " + 1 + 1 == "line 11", not "line 2".
            Assert.Equal("Error parsing config file on line 11", ex.Message);

            // A missing value ("port" alone on the first line) fails inside the try
            // (IndexOutOfRange on parts[1]) and reports the same way with l == 0.
            Exception ex2 = Assert.Throws<Exception>(() => Util.ParseServerConfig("port"));
            Assert.Equal("Error parsing config file on line 01", ex2.Message);
        }

        // TOOLS-04: the behaviour the message was meant to have
        [Fact(Skip = "Known original bug: ParseServerConfig reports \"line \" + l+1 (string concatenation, 'line 11' for the second line), docs/PORTING.md")]
        public void Parse_error_reports_the_one_based_line_number()
        {
            Exception ex = Assert.Throws<Exception>(() => Util.ParseServerConfig("name X\nport abc\n"));
            Assert.Equal("Error parsing config file on line 2", ex.Message);
        }
    }
}
