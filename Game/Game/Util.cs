using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Drawing;
using Microsoft.Xna.Framework.Graphics;
using System.Net;
using System.IO;
using System.Text.RegularExpressions;
using Nuclex.Input;
using Microsoft.Xna.Framework;
using Vexillum.view;
using Vexillum.util;

namespace Vexillum
{
    public class Util
    {
        public static bool IsServer = false;
        public const float EPSILON = 0.1f;
        public const float RAD_15 = 0.261799388f;
        private const string delimiter = "]|||]";
        private static Bitmap nullBitmap = new Bitmap(1, 1);
        private static StringBuilder debug = new StringBuilder(1024);
        public static String debugFileName;
        private static FileStream lockFile;

        public static Texture2D loadTexture(string path)
        {
            Bitmap bmp = loadBitmap(path);
            return loadTexture(bmp);
        }
        public static Texture2D loadTexture(Bitmap bmp)
        {
            if (IsServer)
                return null;
            int bufferSize = bmp.Height * bmp.Width * 4;
            System.IO.MemoryStream stream = new System.IO.MemoryStream(bufferSize);
            bmp.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            Texture2D tex = Texture2D.FromStream(Vexillum.game.GraphicsDevice, stream);
            return tex;
        }
        public static Bitmap loadBitmap(string path)
        {
            Bitmap bmp = new Bitmap("Content/"+path);
            return bmp;
        }
        public static string HttpPost(string path, string data)
        {
            string ported; if (global::Vexillum.Port.Master.MasterServer.TryHandle(path, data, out ported)) return ported; // PORT: playvexillum.com no longer exists; Shims/MasterServer answers the original scripts
            ServicePointManager.Expect100Continue = false;
            try
            {
                Uri uri = new Uri(Vexillum.Server + "/" + path);
                HttpWebRequest r = (HttpWebRequest)WebRequest.Create(uri);
                r.ServicePoint.Expect100Continue = false;
                r.Expect = null;
                r.Method = "post";
                r.Credentials = CredentialCache.DefaultCredentials;
                r.Timeout = 10000;
                byte[] byteData = Encoding.ASCII.GetBytes(data);
                r.ContentType = "application/x-www-form-urlencoded";
                r.ContentLength = byteData.Length;
                using (Stream stream = r.GetRequestStream())
                {
                    stream.Write(byteData, 0, byteData.Length);
                }
                HttpWebResponse response = (HttpWebResponse)r.GetResponse();
                StreamReader reader = new StreamReader(response.GetResponseStream());
                string result = reader.ReadToEnd().Trim();
                reader.Close();
                response.Close();
                return result;
            }
            catch (Exception ex)
            {
                Debug(ex.ToString());
                return "";
            }
        }
        public static string HttpGet(string path)
        {
            string ported; if (global::Vexillum.Port.Master.MasterServer.TryHandle(path, null, out ported)) return ported; // PORT: playvexillum.com no longer exists; Shims/MasterServer answers the original scripts
            try
            {
                Uri uri = new Uri(Vexillum.Server+"/"+path);
                HttpWebRequest r = (HttpWebRequest) WebRequest.Create(uri);
                r.Credentials = CredentialCache.DefaultCredentials;
                r.Timeout = 10000;
                HttpWebResponse response = (HttpWebResponse) r.GetResponse();
                StreamReader reader = new StreamReader(response.GetResponseStream());
                string result = reader.ReadToEnd().Trim();
                reader.Close();
                response.Close();
                return result;
            }
            catch (Exception ex)
            {
                Debug(ex.ToString());
                return "";
            }
        }
        public static string[][] HttpGetArray(string path)
        {
            string result = HttpGet(path);
            if (result == "")
                return new string[][] {};
            string[] lines = Regex.Split(result, "\n");
            if(lines.Length == 0)
                return new string[][] {};

            string[][] array = new string[lines.Length][];
            for (int i = 0; i < lines.Length; i++)
            {

                string[] line = Regex.Split(lines[i], Regex.Escape(delimiter));
                array[i] = line;
            }
            return array;
        }
        public static string GetGameFile(string fileName)
        {
            return Path.Combine(".", fileName);
        }
        public static string GetServerFile(string fileName)
        {
            string path = GetGameFile("Server");
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
            return Path.Combine(path, fileName);
        }
        public static int GetMouseButtonInt(MouseButtons btn)
        {
            if (btn == MouseButtons.Left)
                return 0;
            else if (btn == MouseButtons.Middle)
                return 1;
            else
                return 2;
        }
        public static MouseButtons GetMouseButton(int btn)
        {
            if (btn == 0)
                return MouseButtons.Left;
            else if (btn == 1)
                return MouseButtons.Middle;
            else
                return MouseButtons.Right;
        }
        public static void Debug(Level l, string message)
        {
            Debug("[" + l.frame + "] " + message);
        }
        public static void Debug(string message)
        {
            DateTime date = DateTime.Now;
            message = date.ToString("[yyyy-MM-dd HH:mm:ss] ")+message;
            if (IsServer)
                Console.WriteLine(message);
            else
            {
                System.Diagnostics.Debug.Print(message);
            }
            debug.Append(message+"\n");
            if (debug.Length > 1024)
            {
                WriteDebugLog();
            }
        }
        public static string GetDebugFilename()
        {
            if(IsServer) {
                return GetServerFile("debug_server.log");
            } else {
                return GetGameFile("debug_client.log");
            }
        }
        public static void WriteDebugLog()
        {
            FileStream file = null;
            try
            {

                debugFileName = GetDebugFilename();
                lock (debugFileName)
                {
                    if (File.Exists(debugFileName) && new FileInfo(debugFileName).Length > 768000)
                        File.Delete(debugFileName);
                    file = File.Open(debugFileName, FileMode.Append);
                    byte[] bytes = Encoding.ASCII.GetBytes(debug.ToString());
                    file.Write(bytes, 0, bytes.Length);
                    debug.Clear();
                }
            }
            catch (Exception ex)
            {
            }
            finally
            {
                if(file != null)
                    file.Close();
            }
        }
        public static int RoundToMultiple(float i, int a)
        {
            return (int) Math.Round(i/a) * a;
        }
        public static Vec2 Round(Vec2 v)
        {
            return new Vec2((int)v.X, (int)v.Y);
        }
        public static int Deg(float rad)
        {
            return (int) (rad * (180/(float)Math.PI));
        }
        public static float Rad(float deg)
        {
            return deg * ((float)Math.PI/180);
        }
        public static float NormalizeAngle(float angle)
        {
            if (angle > Math.PI)
                return (float)(-Math.PI + angle - Math.PI);
            else if (angle < -Math.PI)
                return (float)(Math.PI + angle + Math.PI);
            else
                return angle;
        }
        public static void OpenLockFile()
        {
            try
            {
                lockFile = File.Open(GetGameFile("lock"), FileMode.OpenOrCreate, FileAccess.Read, FileShare.None);
            }
            catch (Exception ex)
            {
                //Debug("Error opening lock file: " + ex);
                //Debug(ex.StackTrace);
            }
        }
        public static void CloseLockFile()
        {
            try
            {
                lockFile.Close();
            }
            catch (Exception ex)
            {
                //Debug("Error closing lock file: " + ex);
                //Debug(ex.StackTrace);
            }

        }
        public static ServerConfig ParseServerConfig(string str)
        {
            ServerConfig sc = new ServerConfig();
            int l = 0;
            try
            {
                string[] lines = str.Split('\n');
                foreach(string s in lines)
                {
                    string[] parts = s.Split(' ');
                    string cmd = parts[0];
                    switch (cmd)
                    {
                        case "port":
                            sc.port = int.Parse(parts[1]);
                            break;
                        case "name":
                            sc.name = "";
                            for (int i = 1; i < parts.Length; i++)
                            {
                                sc.name += parts[i];
                                if (i != parts.Length - 1)
                                    sc.name += " ";
                            }
                            break;
                        case "verifynames":
                            sc.verifyNames = bool.Parse(parts[1]);
                            break;
                        case "maxplayers":
                            sc.maxPlayers = int.Parse(parts[1]);
                            break;
                        case "maxcaptures":
                            sc.maxCaptures = int.Parse(parts[1]);
                            break;
                        case "respawntime":
                            sc.respawnTime = int.Parse(parts[1]);
                            break;
                        case "weapons":
                            sc.weapons = new string[parts.Length - 1];

                            for (int i = 1; i < parts.Length; i++)
                            {
                                sc.weapons[i - 1] = parts[i];
                            }
                            break;
                        case "maps":
                            sc.maps = new string[parts.Length - 1];

                            for (int i = 1; i < parts.Length; i++)
                            {
                                sc.maps[i - 1] = parts[i];
                            }
                            break;
                        case "public":
                            sc.isPublic = bool.Parse(parts[1]);
                            break;
                        case "maxbots":
                            sc.maxBots = int.Parse(parts[1]);
                            break;
                    }
                    l++;
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Error parsing config file on line " + l+1);
            }
            return sc;
        }
        public static bool ValidateUsername(string name)
        {
            return Regex.Match(name, "^[A-Za-z0-9\\-_]+$").Success; 
        }
        public static string EscapeUriString(string value)
        {
            const int limit = 2000;

            StringBuilder sb = new StringBuilder();
            int loops = value.Length / limit;

            for (int i = 0; i <= loops; i++)
            {
                if (i < loops)
                {
                    sb.Append(Uri.EscapeDataString(value.Substring(limit * i, limit)));
                }
                else
                {
                    sb.Append(Uri.EscapeDataString(value.Substring(limit * i)));
                }
            }
            return value;
        }
        public class ServerConfig
        {
            public int port = VexillumConstants.DEFAULT_PORT;
            public string name = "Vexillum Server";
            public int maxPlayers = 12;
            public string[] weapons = new string[] { "RocketLauncher", "SMG", "Sword" };
            public string[] maps = null;
            public int maxCaptures = 4;
            public int respawnTime = 5000;
            public bool verifyNames = true;
            public bool isPublic = true;
            public int maxBots = 6;
        }
    }
}
