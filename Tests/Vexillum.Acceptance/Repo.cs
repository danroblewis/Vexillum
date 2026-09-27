using System;
using System.IO;

namespace Vexillum.Acceptance
{
    /// <summary>
    /// Locates the repository and its build outputs from inside a test run.
    /// The root is found by walking up from <see cref="AppContext.BaseDirectory"/>
    /// until a directory containing <c>Vexillum.sln</c> is met; everything else
    /// (the shipped runtime directory <c>Test/</c>, the server and client
    /// executables) is derived from it.
    /// </summary>
    public static class Repo
    {
        private static string root;

        /// <summary>Absolute path of the directory that holds Vexillum.sln.</summary>
        public static string Root
        {
            get
            {
                if (root == null)
                {
                    string dir = AppContext.BaseDirectory;
                    while (dir != null && !File.Exists(Path.Combine(dir, "Vexillum.sln")))
                        dir = Path.GetDirectoryName(dir);
                    if (dir == null)
                        throw new DirectoryNotFoundException("Vexillum.sln not found above " + AppContext.BaseDirectory);
                    root = dir;
                }
                return root;
            }
        }

        /// <summary>The shipped runtime directory (Test/): Content, Maps, Server config. Read-only for tests.</summary>
        public static string RuntimeDir { get { return Path.Combine(Root, "Test"); } }

        /// <summary>
        /// "Debug" or "Release": the configuration this test assembly was built
        /// with, read from its own output path so the executables it starts
        /// come from the same build.
        /// </summary>
        public static string Configuration
        {
            get
            {
                string p = AppContext.BaseDirectory.Replace('\\', '/');
                return p.Contains("/bin/Release/") ? "Release" : "Debug";
            }
        }

        /// <summary>Target framework folder name of the build outputs (net9.0).</summary>
        public static string Tfm
        {
            get
            {
                string p = AppContext.BaseDirectory.Replace('\\', '/').TrimEnd('/');
                return Path.GetFileName(p);
            }
        }

        private static string Exe(string project, string assembly)
        {
            string dir = Path.Combine(Root, project, "bin", Configuration, Tfm);
            string exe = Path.Combine(dir, assembly);
            if (OperatingSystem.IsWindows())
                exe += ".exe";
            return exe;
        }

        /// <summary>Server/bin/&lt;Config&gt;/net9.0/VexillumServer (apphost).</summary>
        public static string ServerExe { get { return Exe("Server", "VexillumServer"); } }

        /// <summary>ZombieSurvival/bin/&lt;Config&gt;/net9.0/VexillumGame (apphost).</summary>
        public static string ClientExe { get { return Exe("ZombieSurvival", "VexillumGame"); } }

        /// <summary>The dev tool script (.claude/mcp/vexillum_dev.py), for tests that want its oracles.</summary>
        public static string DevTool { get { return Path.Combine(Root, ".claude", "mcp", "vexillum_dev.py"); } }
    }
}
