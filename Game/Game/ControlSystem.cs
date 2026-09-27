using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Storage;
using System.IO;
using System.Xml.Serialization;

namespace Vexillum
{
    public enum KeyAction
    {
        None, Move_Left, Move_Right, Move_Down, Jump, Reload, Chat, SendChat, Pause, GrapplingHook, Show_Scoreboard
    };
    class ControlSystem
    {
        public static Dictionary<Keys, KeyAction> controls = new Dictionary<Keys, KeyAction>();
        public static Dictionary<Keys, KeyAction> defaultControls = new Dictionary<Keys, KeyAction>();
        private static string path = Util.GetGameFile("controls.xml");
        static ControlSystem() {
            defaultControls[Keys.A] = KeyAction.Move_Left;
            defaultControls[Keys.D] = KeyAction.Move_Right;
            defaultControls[Keys.S] = KeyAction.Move_Down;
            defaultControls[Keys.W] = KeyAction.Jump;
            defaultControls[Keys.R] = KeyAction.Reload;
            defaultControls[Keys.OemPeriod] = KeyAction.Chat;
            defaultControls[Keys.Enter] = KeyAction.SendChat;
            defaultControls[Keys.Escape] = KeyAction.Pause;
            defaultControls[Keys.F] = KeyAction.GrapplingHook;
            defaultControls[Keys.Tab] = KeyAction.Show_Scoreboard;
        }
        public static void LoadControls()
        {
            if (!File.Exists(path))
            {
                SetDefaultControls();
                SaveControls();
            }
            else
            {
                FileStream fileIn = File.OpenRead(path);
                XmlSerializer serializer = new XmlSerializer(typeof(Controls));
                Controls loadedControls = (Controls)serializer.Deserialize(fileIn);
                updateControls(loadedControls);
                if (loadedControls.actions.Length != defaultControls.Count)
                    SetDefaultControls();
                fileIn.Close();
            }
        }
        public static void SaveControls()
        {
            if (File.Exists(path))
                File.Delete(path);
            FileStream fileOut = File.Create(path);
            XmlSerializer serializer = new XmlSerializer(typeof(Controls));
            serializer.Serialize(fileOut, getStruct());
            fileOut.Close();
        }
        public static KeyAction GetAction(Keys key)
        {
            if (!controls.Keys.Contains(key))
                return KeyAction.None;
            else
                return controls[key];
        }
        public static Dictionary<Keys, KeyAction> GetAllControls()
        {
            return controls;
        }
        public static void ClearControls()
        {
            controls.Clear();
        }
        public static void SetControl(Keys key, KeyAction action)
        {
            controls[key] = action;
        }
        private static void SetDefaultControls()
        {
            foreach (Keys k in defaultControls.Keys)
            {
                if(!controls.ContainsKey(k))
                    controls[k] = defaultControls[k];
            }
        }
        private static Controls getStruct()
        {
            string[] actions = new string[controls.Count];
            Keys[] keys = new Keys[controls.Count];
            for (int i = 0; i < controls.Count; i++)
            {
                actions[i] = controls.Values.ElementAt(i).ToString();
                keys[i] = controls.Keys.ElementAt(i);
            }
            return new Controls(actions, keys);
        }
        private static void updateControls(Controls structure)
        {
            for (int i = 0; i < structure.actions.Length; i++)
            {
                controls[structure.keys[i]] = (KeyAction) Enum.Parse(typeof(KeyAction), structure.actions[i]);
            }
        }
    }
    [Serializable]
    public struct Controls
    {
        public string[] actions;
        public Keys[] keys;
        public Controls(string[] actions, Keys[] keys)
        {
            this.actions = actions;
            this.keys = keys;
        }
    }
}
