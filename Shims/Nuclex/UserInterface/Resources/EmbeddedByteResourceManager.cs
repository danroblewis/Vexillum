// MonoGame port of the Nuclex Framework (Vexillum, 2026). New file, CPL 1.0
// like the rest of the library (see LICENSE-CPL.txt).
//
// The original Nuclex.UserInterface.dll carried the "Suave" default skin in
// a .resources blob generated from a .resx with ResXFileRef entries, which
// needs the Windows-only ResX tooling to build. The four skin files are
// embedded as plain manifest resources instead and served through this
// ResourceManager so that FlatGuiVisualizer.FromResource() and MonoGame's
// ResourceContentManager keep working unchanged.

using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Resources;

namespace Nuclex.UserInterface.Resources {

  /// <summary>Resource manager serving embedded manifest resources as byte arrays</summary>
  internal sealed class EmbeddedByteResourceManager : ResourceManager {

    /// <summary>Initializes a new embedded byte resource manager</summary>
    /// <param name="assembly">Assembly the resources are embedded in</param>
    /// <param name="resourcePrefix">
    ///   Prefix (including the trailing dot) put in front of the resource name
    ///   to form the manifest resource name
    /// </param>
    public EmbeddedByteResourceManager(Assembly assembly, string resourcePrefix) {
      this.assembly = assembly;
      this.resourcePrefix = resourcePrefix;
    }

    /// <summary>Returns the resource with the specified name as a byte array</summary>
    /// <param name="name">Name of the resource that will be returned</param>
    /// <returns>The contents of the resource or null if it does not exist</returns>
    public override object GetObject(string name) {
      return GetObject(name, null);
    }

    /// <summary>Returns the resource with the specified name as a byte array</summary>
    /// <param name="name">Name of the resource that will be returned</param>
    /// <param name="culture">Ignored, the resources are culture-neutral</param>
    /// <returns>The contents of the resource or null if it does not exist</returns>
    public override object GetObject(string name, CultureInfo culture) {
      using(Stream stream = this.assembly.GetManifestResourceStream(this.resourcePrefix + name)) {
        if(stream == null) {
          return null;
        }

        byte[] contents = new byte[stream.Length];
        int offset = 0;
        while(offset < contents.Length) {
          int read = stream.Read(contents, offset, contents.Length - offset);
          if(read <= 0) {
            break;
          }
          offset += read;
        }
        return contents;
      }
    }

    /// <summary>Assembly the resources are embedded in</summary>
    private Assembly assembly;
    /// <summary>Prefix of the manifest resource names</summary>
    private string resourcePrefix;

  }

} // namespace Nuclex.UserInterface.Resources
