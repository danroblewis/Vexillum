using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using Vexillum.Port;
using Vexillum.util;
using Vexillum.view;

namespace Vexillum
{
    /// <summary>
    /// Cross-platform entry point for the MonoGame port (docs/PORTING.md step 7).
    /// The author's <see cref="Program"/> (Program.cs) is left untouched: this
    /// class only parses the port's command line, prepares the process, and
    /// then invokes Program.Main through reflection. Without arguments the
    /// game behaves exactly as before (CLAUDE.md rule 19).
    ///
    ///   --root &lt;dir&gt;          chdir there first (runtime directory shaped like Test/)
    ///   --connect &lt;host&gt;:&lt;port&gt; join that server once the main menu is up
    ///   --help                 print this and exit
    ///   VEXILLUM_LOG_STDOUT=1  echo Util.Debug lines to stdout
    /// </summary>
    static class PortProgram
    {
        static int Main(string[] args)
        {
            string root = null;
            string connectHost = null;
            int connectPort = 0;

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
                else if (a == "--connect" && i + 1 < args.Length)
                {
                    string target = args[++i];
                    int colon = target.LastIndexOf(':');
                    connectHost = colon > 0 ? target.Substring(0, colon) : target;
                    if (colon < 0 || !int.TryParse(target.Substring(colon + 1), out connectPort))
                        connectPort = VexillumConstants.DEFAULT_PORT;
                }
                // Unknown arguments are ignored.
            }

            if (root != null)
            {
                // Before anything else: Util.Debug, the lock file, Content/ and
                // settings.xml are all relative to the current directory.
                Directory.SetCurrentDirectory(root);
            }

            if (Environment.GetEnvironmentVariable("VEXILLUM_LOG_STDOUT") == "1")
            {
                // Util.Debug calls System.Diagnostics.Debug.Print on the client.
                // Verified on .NET 9: Debug output is routed through Trace's
                // listeners, so a ConsoleTraceListener puts every line on stdout.
                // Only in Debug builds (Debug.Print is [Conditional("DEBUG")];
                // Release builds keep writing debug_client.log only).
                Trace.Listeners.Add(new ConsoleTraceListener());
                Trace.AutoFlush = true;
            }

            // MonoGame resolves Content/ against the executable's directory;
            // the game resolves everything else against the current directory.
            // Make them agree (docs/PORTING.md step 10).
            bool contentDirOk = RuntimeDirectory.UseCurrentDirectoryForContent();

            // Makes the author's Content.Load<Effect>("Blur") load on MonoGame
            // (docs/PORTING.md step 8).
            XnaEffectContent.Register();

            Util.Debug("PortProgram: cwd=" + Directory.GetCurrentDirectory()
                + (contentDirOk ? "" : " (WARNING: could not point TitleContainer at cwd; Content/ resolves next to the executable)")
                + (connectHost != null ? " connect=" + connectHost + ":" + connectPort : ""));

            if (connectHost != null)
            {
                StartConnectThread(connectHost, connectPort);
            }

            // Optional in-process debug console for tooling (Shims/DebugHost):
            // VEXILLUM_DEBUG_PORT=<n> makes the client evaluate C# snippets sent
            // to 127.0.0.1:<n>. Never enabled by default.
            string debugPortText = Environment.GetEnvironmentVariable("VEXILLUM_DEBUG_PORT");
            int debugPort;
            if (!string.IsNullOrEmpty(debugPortText) && int.TryParse(debugPortText, out debugPort) && debugPort > 0)
            {
                global::Vexillum.Port.Debugging.DebugHost.Start(debugPort, "client",
                    delegate() { return Vexillum.game; }, null);
            }

            // The author's entry point, unchanged. Program is the non-public
            // static class in Program.cs (inside "#if WINDOWS || XBOX"; the
            // csproj defines WINDOWS).
            MethodInfo main = typeof(Program).GetMethod("Main",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            try
            {
                main.Invoke(null, new object[] { args });
            }
            catch (TargetInvocationException ex)
            {
                // Unwrap so the real exception (with its original stack trace)
                // reaches the author's AppDomain.UnhandledException handler,
                // which shows the message box (shimmed to stderr) and writes
                // the log. The runtime then terminates the process with a
                // non-zero exit code (SIGABRT/134 on macOS and Linux).
                Exception inner = ex.InnerException ?? ex;
                Util.Debug("PortProgram: Program.Main threw " + inner.GetType().FullName + ": " + inner.Message);
                Util.WriteDebugLog();
                ExceptionDispatchInfo.Capture(inner).Throw();
            }

            if (Vexillum.game == null)
            {
                // Program.Main returned without constructing the game: its
                // SteamManager.Initialize() check failed. The original just
                // returns; report it so scripts do not mistake this for a run.
                Util.Debug("PortProgram: the game was never started (SteamManager.Initialize() returned false?)");
                Util.WriteDebugLog();
                Console.Error.WriteLine("Vexillum did not start; see debug_client.log");
                return 1;
            }
            return 0;
        }

        /// <summary>
        /// Reproduces what the original client does after "Host Server" and on
        /// packet 253: call Vexillum.ConnectWhenServerReady from a non-UI
        /// thread once the main menu exists. Waits for LoadContent to have
        /// created the MainMenuView (that happens after every Content.Load).
        /// </summary>
        private static void StartConnectThread(string host, int port)
        {
            Thread t = new Thread(delegate()
            {
                const int pollMs = 250;
                const int giveUpMs = 60000;
                int waited = 0;
                while (waited < giveUpMs)
                {
                    Thread.Sleep(pollMs);
                    waited += pollMs;
                    Vexillum game = Vexillum.game;
                    if (game == null)
                        continue;
                    AbstractView view = game.View;
                    if (view is MainMenuView && view.Menu != null && view.Menu.visible)
                    {
                        Util.Debug("PortProgram: main menu ready after " + waited + " ms, connecting to " + host + ":" + port);
                        try
                        {
                            // Polls the server (up to 40 x 500 ms) and finally
                            // calls Connect, which starts the client thread.
                            game.ConnectWhenServerReady(host, port, 40);
                        }
                        catch (Exception ex)
                        {
                            Util.Debug("PortProgram: --connect failed: " + ex);
                        }
                        return;
                    }
                }
                Util.Debug("PortProgram: gave up waiting for the main menu after " + waited + " ms; --connect ignored");
            });
            t.Name = "PortConnect";
            t.IsBackground = true;
            t.Start();
        }

        private static void PrintHelp()
        {
            Console.WriteLine("VexillumGame [--root <dir>] [--connect <host>:<port>] [--help]");
            Console.WriteLine("  --root <dir>            run with <dir> as the working directory (Content/, Maps/, settings.xml)");
            Console.WriteLine("  --connect <host>:<port> join that server as soon as the main menu is loaded");
            Console.WriteLine("  VEXILLUM_LOG_STDOUT=1   echo the debug log to stdout (Debug builds)");
            Console.WriteLine("  VEXILLUM_DEBUG_PORT=<n>  start the in-process debug console on 127.0.0.1:<n>");
        }
    }
}
