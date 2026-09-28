using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace Vexillum.Acceptance
{
    /// <summary>
    /// Allocates free TCP and UDP ports for tests, so every server process
    /// listens on its own port and several test classes can run in parallel.
    /// A port is found by binding port 0, and ports already handed out in this
    /// process are never returned twice (there is still a small window between
    /// allocation and the server binding it; the harness retries on EADDRINUSE).
    /// </summary>
    public static class FreePort
    {
        private static readonly object sync = new object();
        private static readonly HashSet<int> used = new HashSet<int>();

        /// <summary>A free TCP port on all interfaces (the server binds IPAddress.Any).</summary>
        public static int Tcp()
        {
            lock (sync)
            {
                for (int attempt = 0; attempt < 50; attempt++)
                {
                    TcpListener l = new TcpListener(IPAddress.Any, 0);
                    l.Start();
                    int port = ((IPEndPoint)l.LocalEndpoint).Port;
                    l.Stop();
                    if (used.Add(port))
                        return port;
                }
                throw new InvalidOperationException("could not find a free TCP port");
            }
        }

        /// <summary>A free UDP port (for the LAN beacon, VEXILLUM_LAN_PORT).</summary>
        public static int Udp()
        {
            lock (sync)
            {
                for (int attempt = 0; attempt < 50; attempt++)
                {
                    using (UdpClient u = new UdpClient(new IPEndPoint(IPAddress.Any, 0)))
                    {
                        int port = ((IPEndPoint)u.Client.LocalEndPoint).Port;
                        if (used.Add(port))
                            return port;
                    }
                }
                throw new InvalidOperationException("could not find a free UDP port");
            }
        }

        /// <summary>True when nothing listens on 127.0.0.1:port (TCP).</summary>
        public static bool IsTcpFree(int port)
        {
            try
            {
                TcpListener l = new TcpListener(IPAddress.Any, port);
                l.Start();
                l.Stop();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }
    }
}
