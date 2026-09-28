using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Vexillum.Acceptance
{
    /// <summary>
    /// Starts the real dedicated server (Server/bin/&lt;Config&gt;/net8.0/VexillumServer)
    /// inside a <see cref="ScratchRuntime"/> on a free TCP port and captures its
    /// stdout/stderr. Environment: VEXILLUM_LOG_STDOUT=1, VEXILLUM_MASTER=off
    /// (never publishes to the public registry), VEXILLUM_LAN_PORT=&lt;free udp&gt;,
    /// VEXILLUM_DEBUG_PORT=&lt;free tcp&gt; (in-process C# console, see
    /// <see cref="DebugConsole"/>). The constructor blocks until the server logs
    /// "Ready for connections" (or throws with the output so far). Dispose kills
    /// the process tree.
    /// </summary>
    public sealed class ServerProcess : IDisposable
    {
        /// <summary>Marker the author's server prints once its TcpListener accepts.</summary>
        public const string ReadyMarker = "Ready for connections";

        private readonly Process process;
        private readonly StringBuilder output = new StringBuilder();
        private readonly object outputLock = new object();
        private readonly ManualResetEventSlim outputChanged = new ManualResetEventSlim(false);
        private DebugConsole console;
        private bool disposed;

        /// <summary>The runtime directory the server runs in.</summary>
        public ScratchRuntime Runtime { get; private set; }
        /// <summary>TCP game port (passed as --port, also rewritten into Server/settings.txt by the launcher).</summary>
        public int Port { get; private set; }
        /// <summary>Port of the in-process debug console.</summary>
        public int DebugPort { get; private set; }
        /// <summary>UDP port used for LAN beacons (VEXILLUM_LAN_PORT).</summary>
        public int LanPort { get; private set; }
        /// <summary>OS process id.</summary>
        public int Pid { get { return process.Id; } }
        /// <summary>True while the process is alive.</summary>
        public bool IsRunning { get { return !process.HasExited; } }
        /// <summary>Exit code once the process has ended.</summary>
        public int ExitCode { get { return process.ExitCode; } }

        /// <summary>Directory where the output of every disposed server is kept for post-mortems.</summary>
        public static string LogDir { get { return Path.Combine(Path.GetTempPath(), "vexillum-acceptance", "logs"); } }
        /// <summary>Path of this server's saved output (written on Dispose).</summary>
        public string SavedLogPath { get; private set; }

        /// <summary>Everything the server printed so far (stdout and stderr, in arrival order).</summary>
        public string Output
        {
            get { lock (outputLock) return output.ToString(); }
        }

        /// <summary>Options for <see cref="ServerProcess(ScratchRuntime, Options)"/>.</summary>
        public sealed class Options
        {
            /// <summary>Wait for the ready marker before returning (default true).</summary>
            public bool WaitForReady = true;
            /// <summary>How long to wait for the ready marker.</summary>
            public TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);
            /// <summary>Start the debug console (default true).</summary>
            public bool DebugConsole = true;
            /// <summary>Emit LAN beacons on a private UDP port (default true; set false for VEXILLUM_LAN=off).</summary>
            public bool Lan = true;
            /// <summary>Extra environment variables.</summary>
            public Dictionary<string, string> Environment = new Dictionary<string, string>();
            /// <summary>Extra command line arguments after --root/--port.</summary>
            public List<string> ExtraArgs = new List<string>();
            /// <summary>Use this port instead of a free one (0 = allocate).</summary>
            public int Port = 0;
        }

        /// <summary>Starts a server in <paramref name="runtime"/> with default options and waits until it is ready.</summary>
        public ServerProcess(ScratchRuntime runtime) : this(runtime, new Options())
        {
        }

        public ServerProcess(ScratchRuntime runtime, Options options)
        {
            Runtime = runtime;
            string exe = Repo.ServerExe;
            if (!File.Exists(exe))
                throw new FileNotFoundException("server not built; run `dotnet build Vexillum.sln` first", exe);

            Port = options.Port > 0 ? options.Port : FreePort.Tcp();
            DebugPort = options.DebugConsole ? FreePort.Tcp() : 0;
            LanPort = FreePort.Udp();

            ProcessStartInfo psi = new ProcessStartInfo(exe);
            psi.WorkingDirectory = runtime.Root;
            psi.ArgumentList.Add("--root");
            psi.ArgumentList.Add(runtime.Root);
            psi.ArgumentList.Add("--port");
            psi.ArgumentList.Add(Port.ToString());
            foreach (string a in options.ExtraArgs)
                psi.ArgumentList.Add(a);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardInput = true;
            psi.CreateNoWindow = true;
            psi.Environment["VEXILLUM_LOG_STDOUT"] = "1";
            psi.Environment["VEXILLUM_MASTER"] = "off";
            psi.Environment["VEXILLUM_LAN"] = options.Lan ? "on" : "off";
            psi.Environment["VEXILLUM_LAN_PORT"] = LanPort.ToString();
            if (DebugPort > 0)
                psi.Environment["VEXILLUM_DEBUG_PORT"] = DebugPort.ToString();
            else
                psi.Environment.Remove("VEXILLUM_DEBUG_PORT");
            foreach (KeyValuePair<string, string> kv in options.Environment)
                psi.Environment[kv.Key] = kv.Value;

            process = new Process();
            process.StartInfo = psi;
            process.OutputDataReceived += OnLine;
            process.ErrorDataReceived += OnLine;
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (options.WaitForReady)
            {
                if (WaitFor(Regex.Escape(ReadyMarker), options.ReadyTimeout) == null)
                {
                    string text = Output;
                    Dispose();
                    throw new TimeoutException("server on port " + Port + " did not print '" + ReadyMarker + "' within " + options.ReadyTimeout.TotalSeconds + " s\n" + text);
                }
            }
        }

        private void OnLine(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null)
                return;
            lock (outputLock)
            {
                output.Append(e.Data).Append('\n');
            }
            outputChanged.Set();
        }

        /// <summary>
        /// Waits until a line of the output matches <paramref name="pattern"/>
        /// (regex, searched over the whole output so far, multiline) and returns
        /// the match, or null on timeout. Also returns null if the process exits
        /// before a match (check <see cref="IsRunning"/>).
        /// </summary>
        public Match WaitFor(string pattern, TimeSpan timeout)
        {
            Regex rx = new Regex(pattern, RegexOptions.Multiline);
            DateTime deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                Match m = rx.Match(Output);
                if (m.Success)
                    return m;
                TimeSpan left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || process.HasExited)
                    return rx.Match(Output).Success ? rx.Match(Output) : null;
                outputChanged.Reset();
                outputChanged.Wait(left < TimeSpan.FromMilliseconds(250) ? left : TimeSpan.FromMilliseconds(250));
            }
        }

        /// <summary>Same as WaitFor(pattern, timeout) with the timeout in seconds.</summary>
        public Match WaitFor(string pattern, double seconds)
        {
            return WaitFor(pattern, TimeSpan.FromSeconds(seconds));
        }

        /// <summary>Waits for the n-th occurrence (1-based) of the pattern.</summary>
        public bool WaitForCount(string pattern, int count, TimeSpan timeout)
        {
            Regex rx = new Regex(pattern, RegexOptions.Multiline);
            DateTime deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                if (rx.Matches(Output).Count >= count)
                    return true;
                TimeSpan left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || process.HasExited)
                    return rx.Matches(Output).Count >= count;
                outputChanged.Reset();
                outputChanged.Wait(left < TimeSpan.FromMilliseconds(250) ? left : TimeSpan.FromMilliseconds(250));
            }
        }

        /// <summary>Number of output lines matching the regex so far.</summary>
        public int Count(string pattern)
        {
            return new Regex(pattern, RegexOptions.Multiline).Matches(Output).Count;
        }

        /// <summary>The output lines matching the regex so far.</summary>
        public List<string> Lines(string pattern)
        {
            Regex rx = new Regex(pattern);
            List<string> r = new List<string>();
            foreach (string l in Output.Split('\n'))
                if (rx.IsMatch(l))
                    r.Add(l);
            return r;
        }

        /// <summary>A (lazily connected, reused) debug console for this server.</summary>
        public DebugConsole Console
        {
            get
            {
                if (DebugPort == 0)
                    throw new InvalidOperationException("server started without a debug console");
                if (console == null || !console.IsConnected)
                    console = new DebugConsole(DebugPort, TimeSpan.FromSeconds(15));
                return console;
            }
        }

        /// <summary>Waits for the process to exit by itself.</summary>
        public bool WaitForExit(TimeSpan timeout)
        {
            return process.WaitForExit((int)timeout.TotalMilliseconds);
        }

        /// <summary>Kills the server (process tree) if it is still running.</summary>
        public void Kill()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    process.WaitForExit(5000);
                }
            }
            catch (Exception)
            {
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            if (console != null)
            {
                console.Dispose();
                console = null;
            }
            Kill();
            try
            {
                Directory.CreateDirectory(LogDir);
                SavedLogPath = Path.Combine(LogDir, "server-" + Port + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                File.WriteAllText(SavedLogPath, Output);
            }
            catch (Exception)
            {
            }
            try { process.Dispose(); } catch (Exception) { }
        }
    }
}
