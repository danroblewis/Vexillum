using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using MiscUtil.Conversion;
using MiscUtil.IO;

namespace Vexillum.Acceptance.protocol
{
    /// <summary>
    /// Starts the real game (ZombieSurvival/bin/&lt;Config&gt;/net9.0/VexillumGame) in
    /// a <see cref="ScratchRuntime"/> with <c>--connect host:port</c>, capturing
    /// its stdout (VEXILLUM_LOG_STDOUT=1 echoes Util.Debug). Needs a display:
    /// the game opens its 840x630 window. Dispose kills the process tree.
    /// </summary>
    internal sealed class ClientProcess : IDisposable
    {
        private readonly Process process;
        private readonly StringBuilder output = new StringBuilder();
        private readonly object outputLock = new object();
        private readonly ManualResetEventSlim outputChanged = new ManualResetEventSlim(false);
        private DebugConsole console;

        public ScratchRuntime Runtime { get; private set; }
        public int DebugPort { get; private set; }
        public string Output { get { lock (outputLock) return output.ToString(); } }
        public bool IsRunning { get { return !process.HasExited; } }

        public ClientProcess(ScratchRuntime runtime, string connectHost, int connectPort)
        {
            Runtime = runtime;
            string exe = Repo.ClientExe;
            if (!File.Exists(exe))
                throw new FileNotFoundException("client not built; run `dotnet build Vexillum.sln` first", exe);
            DebugPort = FreePort.Tcp();
            ProcessStartInfo psi = new ProcessStartInfo(exe);
            psi.WorkingDirectory = runtime.Root;
            psi.ArgumentList.Add("--root");
            psi.ArgumentList.Add(runtime.Root);
            psi.ArgumentList.Add("--connect");
            psi.ArgumentList.Add(connectHost + ":" + connectPort);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.Environment["VEXILLUM_LOG_STDOUT"] = "1";
            psi.Environment["VEXILLUM_MASTER"] = "off";
            psi.Environment["VEXILLUM_LAN"] = "off";
            psi.Environment["VEXILLUM_DEBUG_PORT"] = DebugPort.ToString();
            process = new Process();
            process.StartInfo = psi;
            process.OutputDataReceived += OnLine;
            process.ErrorDataReceived += OnLine;
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        private void OnLine(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null)
                return;
            lock (outputLock)
                output.Append(e.Data).Append('\n');
            outputChanged.Set();
        }

        /// <summary>Waits for a line matching the regex (multiline over the whole output); null on timeout or exit.</summary>
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

        public int Count(string pattern)
        {
            return new Regex(pattern, RegexOptions.Multiline).Matches(Output).Count;
        }

        /// <summary>The in-process debug console of the game (scripts see <c>Game</c>).</summary>
        public DebugConsole Console
        {
            get
            {
                if (console == null || !console.IsConnected)
                    console = new DebugConsole(DebugPort, TimeSpan.FromSeconds(15));
                return console;
            }
        }

        public void Dispose()
        {
            if (console != null)
            {
                console.Dispose();
                console = null;
            }
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
            try
            {
                Directory.CreateDirectory(ServerProcess.LogDir);
                File.WriteAllText(Path.Combine(ServerProcess.LogDir, "client-" + DebugPort + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log"), Output);
            }
            catch (Exception)
            {
            }
            try { process.Dispose(); } catch (Exception) { }
        }
    }

    /// <summary>
    /// A minimal stand-in for the server: answers the status probe (255 -> 1,
    /// close), parses packet 1 and hands the connection to a scenario that
    /// writes whatever server packets the test wants; every later client
    /// packet id is recorded (packet 2 with its ready flag and md5).
    /// </summary>
    internal sealed class FakeServer : IDisposable
    {
        public sealed class Connection
        {
            public ulong SteamId;
            public string Name;
            public int TicketLength;
            public EndianBinaryWriter Writer;
            public readonly List<byte> Ids = new List<byte>();
            public readonly List<KeyValuePair<bool, byte[]>> Status = new List<KeyValuePair<bool, byte[]>>();
            public bool Closed;
        }

        private readonly TcpListener listener;
        private readonly Action<Connection> scenario;
        private readonly object sync = new object();
        private readonly List<Connection> logins = new List<Connection>();
        private int probes;
        private bool disposed;

        public int Port { get; private set; }
        public int Probes { get { lock (sync) return probes; } }

        public FakeServer(Action<Connection> scenario)
        {
            this.scenario = scenario;
            Port = FreePort.Tcp();
            listener = new TcpListener(IPAddress.Any, Port);
            listener.Start();
            Thread t = new Thread(AcceptLoop);
            t.IsBackground = true;
            t.Start();
        }

        private void AcceptLoop()
        {
            while (!disposed)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); }
                catch (Exception) { return; }
                Thread t = new Thread(delegate() { Serve(c); });
                t.IsBackground = true;
                t.Start();
            }
        }

        private void Serve(TcpClient c)
        {
            Connection conn = null;
            try
            {
                using (c)
                {
                    NetworkStream s = c.GetStream();
                    EndianBinaryReader r = new EndianBinaryReader(EndianBitConverter.Little, s);
                    int first = r.ReadByte();
                    if (first == Protocol.C2S.StatusProbe)
                    {
                        lock (sync) probes++;
                        s.WriteByte(1);
                        s.Flush();
                        return;
                    }
                    if (first != Protocol.C2S.Login)
                        return;
                    conn = new Connection();
                    conn.SteamId = r.ReadUInt64();
                    conn.Name = r.ReadString();
                    conn.TicketLength = r.ReadInt32();
                    r.ReadBytes(conn.TicketLength);
                    conn.Writer = new EndianBinaryWriter(EndianBitConverter.Little, s);
                    lock (sync)
                    {
                        logins.Add(conn);
                        Monitor.PulseAll(sync);
                    }
                    scenario(conn);
                    while (true)
                    {
                        int id = r.ReadByte();
                        lock (sync)
                        {
                            conn.Ids.Add((byte)id);
                            if (id == Protocol.C2S.Status)
                                conn.Status.Add(new KeyValuePair<bool, byte[]>(r.ReadBoolean(), r.ReadBytes(16)));
                            else if (id == Protocol.C2S.Chat)
                                r.ReadString();
                            Monitor.PulseAll(sync);
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                if (conn != null)
                    lock (sync) { conn.Closed = true; Monitor.PulseAll(sync); }
            }
        }

        /// <summary>The n-th login (0-based) once it arrived, or null on timeout.</summary>
        public Connection WaitForLogin(int index, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            lock (sync)
            {
                while (logins.Count <= index)
                {
                    TimeSpan left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero)
                        return null;
                    Monitor.Wait(sync, left);
                }
                return logins[index];
            }
        }

        /// <summary>Waits until the connection recorded a client packet with that id, or it closed; true when the id arrived.</summary>
        public bool WaitForPacket(Connection conn, byte id, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            lock (sync)
            {
                while (!conn.Ids.Contains(id))
                {
                    TimeSpan left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero || conn.Closed)
                        return conn.Ids.Contains(id);
                    Monitor.Wait(sync, left);
                }
                return true;
            }
        }

        public bool WaitForClose(Connection conn, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            lock (sync)
            {
                while (!conn.Closed)
                {
                    TimeSpan left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero)
                        return false;
                    Monitor.Wait(sync, left);
                }
                return true;
            }
        }

        public void Dispose()
        {
            disposed = true;
            try { listener.Stop(); } catch (Exception) { }
        }
    }
}
