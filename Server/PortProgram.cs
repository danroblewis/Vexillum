using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using Vexillum;

namespace Server
{
    /// <summary>
    /// Cross-platform entry point for the dedicated server (docs/PORTING.md
    /// steps 7 and 9). The author's <see cref="Program"/> (Program.cs) is left
    /// untouched: this class parses the port's command line, prepares the
    /// runtime directory, invokes Program.Main through reflection and then
    /// waits for the server threads so the process lifetime is explicit.
    ///
    ///   --root &lt;dir&gt;   chdir there first (runtime directory shaped like Test/)
    ///   --port &lt;n&gt;     listen on that port (rewrites the "port" line of Server/settings.txt)
    ///   --help          print this and exit
    ///   VEXILLUM_LOG_STDOUT is accepted for symmetry with the client; the server
    ///   already prints Util.Debug to the console (Util.IsServer).
    /// </summary>
    static class PortProgram
    {
        // Verbatim copy of HostServerForm.defaultConfig (ServerStart/HostServerForm.cs),
        // which is not built on this platform; the server used to rely on the
        // WinForms launcher to create settings.txt on first run.
        const string defaultConfig = "#Name of your server, will be shown in the server list.\nname Vexillum Server\n" +
                            "#The port number that the server should run on (must be accessible from the Internet.)\nport 24224\n" +
                            "#Maximum number of players\nmaxplayers 12\n"+
                            "#Default weapons\nweapons RocketLauncher SMG Sword\n"+
                            "#Captures needed to win\nmaxcaptures 4\n"+
                            "#Time to respawn in milliseconds\nrespawntime 5000\n"+
                            "#Should the server verify usernames with playvexillum.com? Recommended if it will be open to the Internet\nverifynames true\n"+
                            "#Should the server be displayed in the server list?\npublic true\n"+
                            "#List of maps for the server\nmaps bases complex\n"+
                            "#Maximum number of bots used to fill the server widh players\nmaxbots 6";

        static int Main(string[] args)
        {
            string root = null;
            int port = -1;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--help" || a == "-h" || a == "/?")
                {
                    PrintHelp();
                    return 0;
                }
                else if (a == "--root" && i + 1 < args.Length)
                {
                    root = args[++i];
                }
                else if (a == "--port" && i + 1 < args.Length)
                {
                    if (!int.TryParse(args[++i], out port) || port <= 0 || port > 65535)
                    {
                        Console.Error.WriteLine("Invalid --port value: " + args[i]);
                        return 2;
                    }
                }
                // Unknown arguments are ignored.
            }

            if (root != null)
            {
                Directory.SetCurrentDirectory(root);
            }
            Console.WriteLine("PortProgram: cwd=" + Directory.GetCurrentDirectory() + (port > 0 ? " port=" + port : ""));

            try
            {
                PrepareSettings(port);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Could not prepare Server/settings.txt: " + ex);
                return 2;
            }



            // The author's entry point, unchanged.
            MethodInfo main = typeof(Program).GetMethod("Main",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            try
            {
                main.Invoke(null, new object[] { args });
            }
            catch (TargetInvocationException ex)
            {
                // Unwrap so the real stack trace is logged and the author's
                // AppDomain.UnhandledException handler still runs; the runtime
                // then exits non-zero.
                Exception inner = ex.InnerException ?? ex;
                Util.Debug("PortProgram: Program.Main threw " + inner.GetType().FullName + ": " + inner.Message);
                Util.WriteDebugLog();
                ExceptionDispatchInfo.Capture(inner).Throw();
            }

            // Program.Main returns as soon as the threads are started (or
            // early if settings.txt failed to parse). Foreground threads keep
            // a .NET 9 process alive by themselves (verified), but waiting on
            // the stepping thread makes the lifetime explicit and gives a
            // place to flush the log and return an exit code.
            object server = typeof(Program).GetField("server", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            if (server == null)
            {
                Util.WriteDebugLog();
                Console.Error.WriteLine("Server did not start (see the settings.txt error above)");
                return 1;
            }
            WaitForServer((Server)server);
            Util.WriteDebugLog();
            return 0;
        }

        /// <summary>
        /// Creates Server/settings.txt with the launcher's default text when it
        /// is missing, and rewrites its "port" line when --port asks for a
        /// different value. The file keeps its other lines untouched.
        /// </summary>
        private static void PrepareSettings(int port)
        {
            string path = Util.GetServerFile("settings.txt");
            if (!File.Exists(path))
            {
                Console.WriteLine("PortProgram: creating " + path + " with the default configuration");
                File.WriteAllBytes(path, Encoding.ASCII.GetBytes(defaultConfig));
            }
            if (port <= 0)
                return;

            string text = File.ReadAllText(path);
            string[] lines = text.Split('\n');
            bool found = false;
            bool changed = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                string[] parts = line.Split(' ');
                if (parts[0] == "port")
                {
                    found = true;
                    int current;
                    if (parts.Length < 2 || !int.TryParse(parts[1], out current) || current != port)
                    {
                        lines[i] = "port " + port;
                        changed = true;
                    }
                    break;
                }
            }
            if (!found)
            {
                text = text.TrimEnd('\r', '\n') + "\n#The port number that the server should run on (must be accessible from the Internet.)\nport " + port + "\n";
                changed = true;
            }
            else if (changed)
            {
                text = string.Join("\n", lines);
            }
            if (changed)
            {
                Console.WriteLine("PortProgram: setting port " + port + " in " + path);
                File.WriteAllBytes(path, Encoding.ASCII.GetBytes(text));
            }
        }

        private static void WaitForServer(Server server)
        {
            Thread stepThread = null;
            try
            {
                FieldInfo f = typeof(Server).GetField("stepThread", BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null)
                    stepThread = f.GetValue(server) as Thread;
            }
            catch (Exception)
            {
            }
            if (stepThread != null)
            {
                stepThread.Join();
            }
            else
            {
                while (server.running)
                    Thread.Sleep(500);
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("VexillumServer [--root <dir>] [--port <n>] [--help]");
            Console.WriteLine("  --root <dir>   run with <dir> as the working directory (Maps/, Server/settings.txt)");
            Console.WriteLine("  --port <n>     listen on port n (updates the port line in Server/settings.txt)");
        }
    }
}
