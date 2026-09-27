using System;
using System.Drawing;
using System.IO;
using Xunit;

namespace Vexillum.Acceptance.toolsconfig
{
    /// <summary>
    /// Texture and bitmap loading on the server side (Game/Game/Util.cs
    /// loadTexture/loadBitmap, Game/Game/view/AssetManager.cs): no graphics
    /// device is ever touched, and Content/ resolves against the current
    /// directory. Util.IsServer and the cwd are process-wide, hence the
    /// GameState collection.
    /// </summary>
    [Collection(GameStateCollection.Name)]
    public class AssetLoadingTests
    {
        // TOOLS-23
        [Fact]
        public void AssetManager_loadTexture_is_a_no_op_on_the_server_without_reading_disk()
        {
            bool wasServer = Util.IsServer;
            using (ScratchRuntime rt = new ScratchRuntime())
            using (new CwdScope(rt.Root))
            {
                Util.IsServer = true;
                try
                {
                    Assert.Null(AssetManager.loadTexture("border.png"));
                    // A file that does not exist would throw from loadBitmap; the server path never gets there.
                    Assert.Null(AssetManager.loadTexture("definitely-missing-" + Guid.NewGuid().ToString("N") + ".png"));
                }
                finally
                {
                    Util.IsServer = wasServer;
                }
            }
        }

        // TOOLS-23
        [Fact]
        public void Util_loadTexture_still_decodes_the_bitmap_on_the_server_and_returns_null()
        {
            bool wasServer = Util.IsServer;
            using (ScratchRuntime rt = new ScratchRuntime())
            using (new CwdScope(rt.Root))
            {
                Util.IsServer = true;
                try
                {
                    Assert.True(File.Exists(Path.Combine(rt.ContentDir, "border.png")));
                    Assert.Null(Util.loadTexture("border.png"));
                    string missing = "missing-" + Guid.NewGuid().ToString("N") + ".png";
                    FileNotFoundException ex = Assert.Throws<FileNotFoundException>(() => Util.loadTexture(missing));
                    Assert.Contains(Path.Combine(rt.Root, "Content", missing), ex.Message);
                    using (Bitmap b = new Bitmap(2, 2))
                        Assert.Null(Util.loadTexture(b));
                }
                finally
                {
                    Util.IsServer = wasServer;
                }
            }
        }

        // TOOLS-23
        [Fact]
        public void Util_loadBitmap_resolves_Content_against_the_cwd()
        {
            using (ScratchRuntime rt = new ScratchRuntime())
            using (new CwdScope(rt.Root))
            {
                Bitmap b = Util.loadBitmap("border.png");
                Assert.NotNull(b);
                Assert.True(b.Width > 0 && b.Height > 0);
                using (Bitmap direct = new Bitmap(Path.Combine(rt.ContentDir, "border.png")))
                {
                    Assert.Equal(direct.Width, b.Width);
                    Assert.Equal(direct.Height, b.Height);
                    Assert.Equal(direct.GetPixel(0, 0), b.GetPixel(0, 0));
                }

                string missing = "missing-" + Guid.NewGuid().ToString("N") + ".png";
                FileNotFoundException ex = Assert.Throws<FileNotFoundException>(() => Util.loadBitmap(missing));
                Assert.Contains(Path.Combine(rt.Root, "Content", missing), ex.Message);

                // The same name from another cwd is another file.
                using (TempDir other = new TempDir())
                using (new CwdScope(other.Path))
                    Assert.Throws<FileNotFoundException>(() => Util.loadBitmap("border.png"));
            }
        }
    }
}
