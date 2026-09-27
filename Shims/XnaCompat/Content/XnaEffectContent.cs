using System;
using System.IO;
using System.Text;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;

// Port glue (docs/PORTING.md step 8): lets the author's
// Content.Load<Effect>("Blur") succeed on MonoGame without editing GameView.cs
// and without a shader compiler.
//
// Test/Content/Blur.xnb is an XNA 4.0 XNB whose type reader string names the
// XNA EffectReader and whose payload is DirectX 9 bytecode. MonoGame maps that
// reader string onto its own EffectReader, which then rejects the payload
// ("This does not appear to be a MonoGame MGFX file!"). ContentTypeReaderManager
// looks up the *original* reader string in its type-creator table before it
// resolves the type, so registering a creator for the XNA string routes the
// asset to XnaEffectReader below. That reader skips the DX9 bytes and builds
// the same effect from a hand-assembled MGFX (version 10, OpenGL profile)
// blob: on DesktopGL an MGFX shader is plain GLSL source, so no compiler is
// needed. The GLSL is a transcription of ZombieSurvivalContent/Blur.fx:
//
//     out.rgb = (tex(uv) + tex(uv + d)) / 2 ;  out.a = tex(uv).a
//
// The pixel shader links against MonoGame's own SpriteEffect vertex shader
// (varyings vFrontColor / vTexCoord0, sampler ps_s0, uniforms in
// ps_uniforms_vec4[]), which is what GameView.DrawStuff relies on: it calls
// Passes[0].Apply() inside an Immediate-mode SpriteBatch, exactly the XNA idiom.
namespace Vexillum.Port
{
    public static class XnaEffectContent
    {
        /// <summary>The reader string stored in every XNA 4.0 effect XNB.</summary>
        public const string XnaEffectReaderTypeString =
            "Microsoft.Xna.Framework.Content.EffectReader, Microsoft.Xna.Framework.Graphics, Version=4.0.0.0, Culture=neutral, PublicKeyToken=842cf8be1de50553";

        private static bool registered;

        /// <summary>
        /// Call once before the game's ContentManager loads "Blur". Safe to call
        /// more than once.
        /// </summary>
        public static void Register()
        {
            if (registered)
                return;
            registered = true;
            ContentTypeReaderManager.AddTypeCreator(XnaEffectReaderTypeString, delegate() { return new XnaEffectReader(); });
        }
    }

    internal class XnaEffectReader : ContentTypeReader<Effect>
    {
        protected override Effect Read(ContentReader input, Effect existingInstance)
        {
            // XNA EffectReader payload: int32 length + DX9 bytecode. Skip it.
            int length = input.ReadInt32();
            input.BaseStream.Seek(length, SeekOrigin.Current);

            string name = input.AssetName ?? "";
            if (name.EndsWith("Blur", StringComparison.OrdinalIgnoreCase))
            {
                Effect effect = new Effect(input.GetGraphicsDevice(), BlurMgfx.Bytes);
                effect.Name = name;
                return effect;
            }
            throw new ContentLoadException("XNA effect '" + name + "' has no MonoGame replacement (only 'Blur' is known; see Shims/XnaCompat/Content/XnaEffectContent.cs).");
        }
    }

    /// <summary>
    /// Builds an MGFX version 10 / OpenGL-profile blob for the Blur effect. The
    /// layout follows Effect.ReadEffect and Shader(BinaryReader) in
    /// MonoGame.Framework 3.8.4 (DesktopGL).
    /// </summary>
    public static class BlurMgfx
    {
        // Any constant works; it only keys GraphicsDevice.EffectCache.
        private const int EffectKey = 0x56584C42; // 'VXLB'

        // GLSL fragment shader in the dialect MonoGame's own effects use on
        // DesktopGL (no #version line; GL 2.1 / GLSL 1.20 compatible).
        public const string PixelShaderGlsl =
            "#ifdef GL_ES\n" +
            "precision mediump float;\n" +
            "precision mediump int;\n" +
            "#endif\n" +
            "\n" +
            "uniform vec4 ps_uniforms_vec4[1];\n" +
            "#define ps_c0 ps_uniforms_vec4[0]\n" +
            "uniform sampler2D ps_s0;\n" +
            "varying vec4 vFrontColor;\n" +
            "varying vec4 vTexCoord0;\n" +
            "\n" +
            "void main()\n" +
            "{\n" +
            "\tvec4 tex = texture2D(ps_s0, vTexCoord0.xy);\n" +
            "\tvec4 color = tex + texture2D(ps_s0, vTexCoord0.xy + ps_c0.xy);\n" +
            "\tcolor = color / 2.0;\n" +
            "\ttex.rgb = color.rgb;\n" +
            "\tgl_FragColor = tex;\n" +
            "}\n";

        private static byte[] bytes;

        public static byte[] Bytes
        {
            get
            {
                if (bytes == null)
                    bytes = Build();
                return bytes;
            }
        }

        private static byte[] Build()
        {
            MemoryStream ms = new MemoryStream();
            BinaryWriter w = new BinaryWriter(ms, Encoding.UTF8);

            // Header (Effect.ReadHeader): "MGFX", version, profile, effect key.
            w.Write((byte)'M'); w.Write((byte)'G'); w.Write((byte)'F'); w.Write((byte)'X');
            w.Write((byte)10);          // MGFXHeader.MGFXVersion
            w.Write((byte)0);           // Shader.Profile: 0 = OpenGL
            w.Write(EffectKey);

            // Constant buffers: one, holding parameter 0 ("d") at offset 0.
            w.Write(1);                         // count
            w.Write("ps_uniforms_vec4");        // uniform name looked up in the linked program
            w.Write((short)16);                 // size in bytes (one vec4 register)
            w.Write(1);                         // parameter count
            w.Write(0);                         // parameter index
            w.Write((ushort)0);                 // parameter offset

            // Shaders: one pixel shader.
            w.Write(1);                         // count
            w.Write(false);                     // is vertex shader
            byte[] code = Encoding.ASCII.GetBytes(PixelShaderGlsl);
            w.Write(code.Length);
            w.Write(code);
            w.Write((byte)1);                   // sampler count
            w.Write((byte)0);                   //   SamplerType.Sampler2D
            w.Write((byte)0);                   //   texture slot
            w.Write((byte)0);                   //   sampler slot
            w.Write(false);                     //   no embedded sampler state (SpriteBatch's PointClamp stays)
            w.Write("ps_s0");                   //   GLSL uniform name
            w.Write((byte)1);                   //   effect parameter index (the Texture parameter below)
            w.Write((byte)1);                   // constant buffer count
            w.Write((byte)0);                   //   constant buffer index
            w.Write((byte)0);                   // vertex attribute count

            // Parameters (Effect.ReadParameters).
            w.Write(2);                         // count
            //   [0] float2 d
            w.Write((byte)1);                   // EffectParameterClass.Vector
            w.Write((byte)3);                   // EffectParameterType.Single
            w.Write("d");                       // name
            w.Write("");                        // semantic
            w.Write(0);                         // annotations
            w.Write((byte)1);                   // rows
            w.Write((byte)2);                   // columns
            w.Write(0);                         // elements
            w.Write(0);                         // struct members
            w.Write(0f); w.Write(0f);           // default value
            //   [1] texture bound to sampler s0 (Blur.fx: "sampler TextureSampler : register(s0)")
            w.Write((byte)3);                   // EffectParameterClass.Object
            w.Write((byte)7);                   // EffectParameterType.Texture2D
            w.Write("TextureSampler");
            w.Write("");
            w.Write(0);                         // annotations
            w.Write((byte)0);                   // rows
            w.Write((byte)0);                   // columns
            w.Write(0);                         // elements
            w.Write(0);                         // struct members

            // Techniques / passes, named as in Blur.fx.
            w.Write(1);                         // technique count
            w.Write("Desaturate");
            w.Write(0);                         // annotations
            w.Write(1);                         // pass count
            w.Write("Pass1");
            w.Write(0);                         // annotations
            w.Write(-1);                        // vertex shader index: none (SpriteBatch's stays bound)
            w.Write(0);                         // pixel shader index
            w.Write(false);                     // no blend state
            w.Write(false);                     // no depth-stencil state
            w.Write(false);                     // no rasterizer state

            // Trailer: the signature again (Effect ctor checks it).
            w.Write((byte)'M'); w.Write((byte)'G'); w.Write((byte)'F'); w.Write((byte)'X');

            w.Flush();
            return ms.ToArray();
        }
    }
}
