using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml.Serialization;
using Microsoft.Xna.Framework.Input;
using Vexillum.util;
using Xunit;

namespace Vexillum.Acceptance.toolsconfig
{
    /// <summary>
    /// controls.xml (Game/Game/ControlSystem.cs) and settings.xml
    /// (Game/Game/util/Settings.cs) persistence. Both classes keep a static
    /// dictionary and a path of "./<file>" resolved against the current
    /// directory, so every test runs in its own temp cwd and clears the
    /// statics first. ControlSystem is internal: driven through reflection.
    /// </summary>
    [Collection(ToolsConfigCollection.Name)]
    public class ControlsSettingsTests
    {
        private static readonly Type controlSystem = typeof(KeyAction).Assembly.GetType("Vexillum.ControlSystem", true);

        private static void LoadControls() { Reflect.CallStatic(controlSystem, "LoadControls"); }
        private static void ClearControls() { Reflect.CallStatic(controlSystem, "ClearControls"); }
        private static KeyAction GetAction(Keys k) { return (KeyAction)Reflect.CallStatic(controlSystem, "GetAction", k); }
        private static Dictionary<Keys, KeyAction> AllControls() { return (Dictionary<Keys, KeyAction>)Reflect.CallStatic(controlSystem, "GetAllControls"); }

        private static Controls ReadControlsXml(string path)
        {
            using (FileStream f = File.OpenRead(path))
                return (Controls)new XmlSerializer(typeof(Controls)).Deserialize(f);
        }

        private static void WriteControlsXml(string path, string[] actions, Keys[] keys)
        {
            using (FileStream f = File.Create(path))
                new XmlSerializer(typeof(Controls)).Serialize(f, new Controls(actions, keys));
        }

        /// <summary>The document without its XML declaration line, LF-normalised and trimmed.</summary>
        private static string StripDeclaration(string xml)
        {
            xml = xml.Replace("\r\n", "\n").Trim();
            if (xml.StartsWith("<?xml"))
                xml = xml.Substring(xml.IndexOf("?>") + 2).Trim();
            return xml;
        }

        private static readonly string[] defaultActions =
        {
            "Move_Left", "Move_Right", "Move_Down", "Jump", "Reload", "Chat", "SendChat", "Pause", "GrapplingHook", "Show_Scoreboard"
        };
        private static readonly Keys[] defaultKeys =
        {
            Keys.A, Keys.D, Keys.S, Keys.W, Keys.R, Keys.OemPeriod, Keys.Enter, Keys.Escape, Keys.F, Keys.Tab
        };

        // TOOLS-06 (a)
        [Fact]
        public void Missing_controls_xml_is_created_with_the_defaults_in_declaration_order()
        {
            using (TempDir dir = new TempDir())
            using (new CwdScope(dir.Path))
            {
                ClearControls();
                LoadControls();

                string path = dir.File("controls.xml");
                Assert.True(File.Exists(path));
                Controls written = ReadControlsXml(path);
                Assert.Equal(defaultActions, written.actions);
                Assert.Equal(defaultKeys, written.keys);
                // Same document the 2013 build shipped in Test/controls.xml (the XML
                // declaration differs: .NET 9 adds encoding="utf-8"; the element tree is identical).
                Assert.Equal(StripDeclaration(File.ReadAllText(Path.Combine(Repo.RuntimeDir, "controls.xml"))), StripDeclaration(File.ReadAllText(path)));
                Controls shipped = ReadControlsXml(Path.Combine(Repo.RuntimeDir, "controls.xml"));
                Assert.Equal(shipped.actions, written.actions);
                Assert.Equal(shipped.keys, written.keys);

                Assert.Equal(KeyAction.Move_Left, GetAction(Keys.A));
                Assert.Equal(KeyAction.Jump, GetAction(Keys.W));
                Assert.Equal(KeyAction.Show_Scoreboard, GetAction(Keys.Tab));
                Assert.Equal(KeyAction.None, GetAction(Keys.Z));
                Assert.Equal(10, AllControls().Count);
            }
        }

        // TOOLS-06 (b)
        [Fact]
        public void Complete_custom_mapping_replaces_the_defaults()
        {
            using (TempDir dir = new TempDir())
            using (new CwdScope(dir.Path))
            {
                Keys[] keys = { Keys.K, Keys.L, Keys.J, Keys.I, Keys.T, Keys.OemComma, Keys.Space, Keys.P, Keys.G, Keys.Q };
                WriteControlsXml(dir.File("controls.xml"), defaultActions, keys);
                ClearControls();
                LoadControls();

                Assert.Equal(KeyAction.Move_Left, GetAction(Keys.K));
                Assert.Equal(KeyAction.Jump, GetAction(Keys.I));
                Assert.Equal(KeyAction.Show_Scoreboard, GetAction(Keys.Q));
                // Ten actions in the file: SetDefaultControls is not applied, A is unbound.
                Assert.Equal(KeyAction.None, GetAction(Keys.A));
                Assert.Equal(KeyAction.None, GetAction(Keys.W));
                Assert.Equal(10, AllControls().Count);
                // Loading does not rewrite the file.
                Assert.Equal(keys, ReadControlsXml(dir.File("controls.xml")).keys);
            }
        }

        // TOOLS-06 (c)
        [Fact]
        public void Partial_controls_xml_is_completed_with_defaults_but_keeps_its_own_bindings()
        {
            using (TempDir dir = new TempDir())
            using (new CwdScope(dir.Path))
            {
                // Three actions: two on new keys, one remapping the default key A to Jump.
                WriteControlsXml(dir.File("controls.xml"),
                    new string[] { "Move_Left", "Move_Right", "Jump" },
                    new Keys[] { Keys.K, Keys.L, Keys.A });
                ClearControls();
                LoadControls();

                Assert.Equal(KeyAction.Move_Left, GetAction(Keys.K));
                Assert.Equal(KeyAction.Move_Right, GetAction(Keys.L));
                Assert.Equal(KeyAction.Jump, GetAction(Keys.A));          // the file wins over the default Move_Left
                Assert.Equal(KeyAction.Jump, GetAction(Keys.W));          // default filled in
                Assert.Equal(KeyAction.Move_Down, GetAction(Keys.S));
                Assert.Equal(KeyAction.Chat, GetAction(Keys.OemPeriod));
                Assert.Equal(KeyAction.Show_Scoreboard, GetAction(Keys.Tab));
                Assert.Equal(12, AllControls().Count);                    // 3 from the file + 9 defaults not already bound
            }
        }

        // TOOLS-06: the enum is wire format (KeyAction bytes in the position packets)
        [Fact]
        public void KeyAction_declaration_order_is_the_wire_order()
        {
            Assert.Equal(new KeyAction[]
            {
                KeyAction.None, KeyAction.Move_Left, KeyAction.Move_Right, KeyAction.Move_Down, KeyAction.Jump, KeyAction.Reload,
                KeyAction.Chat, KeyAction.SendChat, KeyAction.Pause, KeyAction.GrapplingHook, KeyAction.Show_Scoreboard
            }, (KeyAction[])Enum.GetValues(typeof(KeyAction)));
            Assert.Equal(0, (int)KeyAction.None);
            Assert.Equal(10, (int)KeyAction.Show_Scoreboard);
        }

        // ---------------------------------------------------------------
        // settings.xml
        // ---------------------------------------------------------------

        private static void ClearSettings()
        {
            ((Dictionary<SettingType, object>)Reflect.GetStatic(typeof(Settings), "settings")).Clear();
        }

        private static Settings.SerializedSettings ReadSettingsXml(string path)
        {
            using (FileStream f = File.OpenRead(path))
                return (Settings.SerializedSettings)new XmlSerializer(typeof(Settings.SerializedSettings)).Deserialize(f);
        }

        // TOOLS-07
        [Fact]
        public void Missing_settings_xml_is_created_with_empty_username_and_password()
        {
            using (TempDir dir = new TempDir())
            using (new CwdScope(dir.Path))
            {
                ClearSettings();
                Settings.LoadSettings();

                string path = dir.File("settings.xml");
                Assert.True(File.Exists(path));
                string xml = File.ReadAllText(path);
                Assert.Contains("<SerializedSettings", xml);
                Assert.Contains("<SettingType>Username</SettingType>", xml);
                Assert.Contains("<SettingType>Password</SettingType>", xml);
                Assert.Equal(2, xml.Split("xsi:type=\"xsd:string\"").Length - 1);   // two empty string values
                // Same keys and values as the shipped Test/settings.xml (empty-element spelling may differ).
                Settings.SerializedSettings written = ReadSettingsXml(path);
                Settings.SerializedSettings shipped = ReadSettingsXml(Path.Combine(Repo.RuntimeDir, "settings.xml"));
                Assert.Equal(new SettingType[] { SettingType.Username, SettingType.Password }, written.keys);
                Assert.Equal(shipped.keys, written.keys);
                Assert.Equal(new object[] { "", "" }, written.values);
                Assert.Equal(shipped.values, written.values);

                Assert.Equal("", Settings.Get(SettingType.Username));
                Assert.Equal("", Settings.Get(SettingType.Password));
            }
        }

        // TOOLS-07
        [Fact]
        public void Set_saves_immediately_and_a_fresh_load_reads_it_back()
        {
            using (TempDir dir = new TempDir())
            using (new CwdScope(dir.Path))
            {
                ClearSettings();
                Settings.LoadSettings();
                Settings.Set(SettingType.Username, "Bob");

                Settings.SerializedSettings onDisk = ReadSettingsXml(dir.File("settings.xml"));
                int u = Array.IndexOf(onDisk.keys, SettingType.Username);
                Assert.True(u >= 0);
                Assert.Equal("Bob", onDisk.values[u]);

                ClearSettings();
                Settings.LoadSettings();
                Assert.Equal("Bob", Settings.Get(SettingType.Username));
                Assert.Equal("", Settings.Get(SettingType.Password));
            }
        }

        // TOOLS-07
        [Fact]
        public void Settings_xml_with_only_username_gets_password_filled_in()
        {
            using (TempDir dir = new TempDir())
            using (new CwdScope(dir.Path))
            {
                File.WriteAllText(dir.File("settings.xml"),
                    "<?xml version=\"1.0\"?>\n" +
                    "<SerializedSettings xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\n" +
                    "  <keys><SettingType>Username</SettingType></keys>\n" +
                    "  <values><anyType xsi:type=\"xsd:string\">Alice</anyType></values>\n" +
                    "</SerializedSettings>\n", Encoding.UTF8);
                ClearSettings();
                Settings.LoadSettings();

                Assert.Equal("Alice", Settings.Get(SettingType.Username));
                Assert.Equal("", Settings.Get(SettingType.Password));
                // LoadSettings itself does not rewrite the file.
                Assert.Single(ReadSettingsXml(dir.File("settings.xml")).keys);
            }
        }
    }
}
