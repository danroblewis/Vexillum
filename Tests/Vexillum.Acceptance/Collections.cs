using Xunit;

// Parallelism policy of this assembly (see docs/TESTING.md):
//
// * xunit runs test classes in parallel and the tests inside one class
//   serially. A class that starts its own ServerProcess in its own
//   ScratchRuntime on its own ports is therefore safe to run next to any
//   other class; no collection attribute is needed for it. Prefer one
//   server per class (IClassFixture<ServerFixture>) over one per test.
//
// * Tests that touch process-wide game state must NOT run in parallel with
//   each other: Util.IsServer, Entity.CurrentID / Entity.ResetID, the static
//   LevelLoader level list, HumanoidTypes' definitions, Identity.*, and the
//   current directory (LevelLoader reads Maps/ relative to it). Put those in
//   [Collection(GameStateCollection.Name)]: the collection is one serial
//   queue and is also excluded from running alongside other collections.
//   HeadlessLevel tests belong here.
//
// * MaxParallelThreads bounds how many server processes can be alive at once
//   on a laptop; raise it if the machine has cores and ports to spare.
[assembly: CollectionBehavior(MaxParallelThreads = 4)]

namespace Vexillum.Acceptance
{
    /// <summary>
    /// Serial collection for tests that use static game state (HeadlessLevel,
    /// LevelLoader, Entity ids, Util.IsServer, current directory).
    /// Use <c>[Collection(GameStateCollection.Name)]</c>.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public class GameStateCollection
    {
        public const string Name = "GameState";
    }

    /// <summary>
    /// Serial collection for tests that need a display or the single shared
    /// mouse/keyboard (real client windows). Use <c>[Collection(DisplayCollection.Name)]</c>.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public class DisplayCollection
    {
        public const string Name = "Display";
    }

    /// <summary>
    /// Class fixture: one scratch runtime and one running server for all the
    /// tests of a class. Subclass or use directly through
    /// <c>IClassFixture&lt;ServerFixture&gt;</c>. Override <see cref="Configure"/>
    /// in a subclass to edit settings before the server starts.
    /// </summary>
    public class ServerFixture : System.IDisposable
    {
        public ScratchRuntime Runtime { get; private set; }
        public ServerProcess Server { get; private set; }

        public ServerFixture()
        {
            Runtime = new ScratchRuntime();
            ServerProcess.Options o = new ServerProcess.Options();
            Configure(Runtime, o);
            Server = new ServerProcess(Runtime, o);
        }

        /// <summary>Edit Server/settings.txt or the start options before the process starts.</summary>
        protected virtual void Configure(ScratchRuntime runtime, ServerProcess.Options options)
        {
        }

        public void Dispose()
        {
            if (Server != null) Server.Dispose();
            if (Runtime != null) Runtime.Dispose();
        }
    }
}
