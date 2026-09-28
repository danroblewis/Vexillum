using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Vexillum.Port.Debugging
{
    /// <summary>
    /// A localhost debug console living inside the running game or server.
    ///
    /// Protocol: TCP on 127.0.0.1:port, newline-delimited JSON.
    ///   request   {"code": "<C# script>", "timeout": 15000}
    ///   response  {"ok": true|false, "result": "...", "log": "...", "error": "...", "ms": 12}
    /// Script state (variables, usings) persists across requests for the
    /// lifetime of the process. See <see cref="DebugGlobals"/> for what the
    /// scripts can reach: <c>Game</c>, <c>Server</c>, <c>Sync(...)</c>,
    /// <c>Get/Set/Call</c> for private members, <c>Dump</c>, <c>Log</c>.
    /// </summary>
    public static class DebugHost
    {
        private static readonly object evalLock = new object();
        private static ScriptOptions options;
        private static ScriptState<object> state;
        private static DebugGlobals globals;
        private static Func<object> gameGetter;
        private static Func<object> serverGetter;
        private static string role;

        public static bool Running { get; private set; }

        /// <summary>Starts listening; returns immediately. Safe to call once.</summary>
        public static void Start(int port, string processRole, Func<object> getGame, Func<object> getServer)
        {
            if (Running)
                return;
            role = processRole;
            gameGetter = getGame;
            serverGetter = getServer;
            globals = new DebugGlobals(processRole);
            Running = true;

            Thread t = new Thread(delegate()
            {
                TcpListener listener;
                try
                {
                    listener = new TcpListener(IPAddress.Loopback, port);
                    listener.Start();
                }
                catch (Exception ex)
                {
                    Say("DebugHost: could not listen on 127.0.0.1:" + port + ": " + ex.Message);
                    Running = false;
                    return;
                }
                Say("DebugHost: listening on 127.0.0.1:" + port + " (" + processRole + ")");
                while (true)
                {
                    TcpClient client;
                    try
                    {
                        client = listener.AcceptTcpClient();
                    }
                    catch (Exception)
                    {
                        break;
                    }
                    Thread h = new Thread(delegate() { Handle(client); });
                    h.Name = "DebugHostClient";
                    h.IsBackground = true;
                    h.Start();
                }
            });
            t.Name = "DebugHost";
            t.IsBackground = true;
            t.Start();
        }

        private static void Say(string message)
        {
            try
            {
                global::Vexillum.Util.Debug(message);
            }
            catch (Exception)
            {
                Console.WriteLine(message);
            }
        }

        private static void Handle(TcpClient client)
        {
            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                using (StreamReader reader = new StreamReader(stream, new UTF8Encoding(false)))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.AutoFlush = true;
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Trim().Length == 0)
                            continue;
                        string code = null;
                        int timeout = 15000;
                        try
                        {
                            using (JsonDocument doc = JsonDocument.Parse(line))
                            {
                                JsonElement root = doc.RootElement;
                                JsonElement v;
                                if (root.TryGetProperty("code", out v))
                                    code = v.GetString();
                                if (root.TryGetProperty("timeout", out v) && v.ValueKind == JsonValueKind.Number)
                                    timeout = v.GetInt32();
                            }
                        }
                        catch (Exception ex)
                        {
                            writer.WriteLine(JsonSerializer.Serialize(new { ok = false, error = "bad request: " + ex.Message }));
                            continue;
                        }
                        writer.WriteLine(JsonSerializer.Serialize(Evaluate(code ?? "", timeout)));
                    }
                }
            }
            catch (Exception)
            {
                // client went away
            }
        }

        private static object Evaluate(string code, int timeoutMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            lock (evalLock)
            {
                globals.LogBuffer.Clear();
                try
                {
                    globals.Game = gameGetter != null ? gameGetter() : null;
                    globals.Server = serverGetter != null ? serverGetter() : null;
                }
                catch (Exception ex)
                {
                    globals.Log("could not resolve Game/Server: " + ex.Message);
                }
                try
                {
                    if (options == null)
                        options = BuildOptions();
                    if (code == "!reset")
                    {
                        state = null;
                        return new { ok = true, result = "script state reset", log = "", ms = sw.ElapsedMilliseconds };
                    }
                    Task<ScriptState<object>> task = state == null
                        ? CSharpScript.RunAsync(code, options, globals, typeof(DebugGlobals))
                        : state.ContinueWithAsync(code);
                    if (!task.Wait(timeoutMs))
                    {
                        return new { ok = false, error = "timeout after " + timeoutMs + " ms (the script is still running; state not updated)", log = globals.LogBuffer.ToString(), ms = sw.ElapsedMilliseconds };
                    }
                    state = task.Result;
                    return new { ok = true, result = Formatter.Format(state.ReturnValue), log = globals.LogBuffer.ToString(), ms = sw.ElapsedMilliseconds };
                }
                catch (Exception ex)
                {
                    Exception inner = ex is AggregateException && ((AggregateException)ex).InnerExceptions.Count == 1
                        ? ((AggregateException)ex).InnerException : ex;
                    string error;
                    CompilationErrorException ce = inner as CompilationErrorException;
                    if (ce != null)
                        error = "compile error:\n" + string.Join("\n", ce.Diagnostics.Select(d => d.ToString()));
                    else
                        error = inner.ToString();
                    return new { ok = false, error = error, log = globals.LogBuffer.ToString(), ms = sw.ElapsedMilliseconds };
                }
            }
        }

        private static ScriptOptions BuildOptions()
        {
            List<Assembly> refs = new List<Assembly>();
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.IsDynamic)
                    continue;
                string loc = null;
                try { loc = a.Location; } catch (Exception) { }
                if (string.IsNullOrEmpty(loc))
                    continue;
                refs.Add(a);
            }
            refs.Add(typeof(Microsoft.CSharp.RuntimeBinder.Binder).Assembly);
            refs.Add(typeof(System.Linq.Enumerable).Assembly);
            List<string> imports = new List<string>
            {
                "System", "System.Linq", "System.Collections.Generic", "System.Reflection", "System.Text",
                "Vexillum", "Vexillum.Entities", "Vexillum.Entities.Weapons", "Vexillum.Game", "Vexillum.util",
                "Vexillum.view", "Vexillum.net", "Vexillum.Port.Debugging", "Microsoft.Xna.Framework",
            };
            if (role == "server" && refs.Any(a => a.GetName().Name == "VexillumServer"))
                imports.Add("Server");
            return ScriptOptions.Default
                .AddReferences(refs.Distinct())
                .AddImports(imports)
                .WithEmitDebugInformation(false);
        }
    }

    /// <summary>
    /// What scripts see. Members are accessible without qualification.
    /// </summary>
    public sealed class DebugGlobals
    {
        /// <summary>The running Vexillum.Vexillum game (client) or null. Public members via dynamic.</summary>
        public dynamic Game;
        /// <summary>The running Server.Server (server) or null. Public members via dynamic.</summary>
        public dynamic Server;
        /// <summary>"client" or "server".</summary>
        public readonly string Role;
        public readonly StringBuilder LogBuffer = new StringBuilder();

        private DebugPump pump;

        public DebugGlobals(string role)
        {
            Role = role;
        }

        /// <summary>Appends to the request's log output (returned alongside the result).</summary>
        public void Log(object message)
        {
            LogBuffer.Append(Formatter.Format(message)).Append('\n');
        }

        /// <summary>Reads a field or property (any visibility, instance or static).</summary>
        public object Get(object target, string name)
        {
            return Reflect.Get(target, name);
        }

        /// <summary>Reads a static field or property of a type by name, e.g. Static("Vexillum.Entities.Entity", "CurrentID").</summary>
        public object Static(string typeName, string name)
        {
            return Reflect.GetStatic(TypeOf(typeName), name);
        }

        /// <summary>Writes a field or property (any visibility).</summary>
        public void Set(object target, string name, object value)
        {
            Reflect.Set(target, name, value);
        }

        /// <summary>Invokes a method (any visibility) by name.</summary>
        public object Call(object target, string name, params object[] args)
        {
            return Reflect.Call(target, name, args);
        }

        /// <summary>Finds a type by full or simple name in the loaded assemblies.</summary>
        public Type TypeOf(string name)
        {
            return Reflect.FindType(name);
        }

        /// <summary>Public fields and properties of an object, one per line.</summary>
        public string Dump(object o)
        {
            return Formatter.Dump(o, true);
        }

        /// <summary>
        /// Runs the function on the game's mutator thread and returns its result:
        /// the MonoGame Update thread on the client (via a GameComponent), the
        /// "Server Main" step thread on the server (via Server.AddTask). Use it
        /// for anything that touches level or entity state.
        /// </summary>
        public object Sync(Func<object> fn)
        {
            return Sync(fn, 10000);
        }

        public object Sync(Func<object> fn, int timeoutMs)
        {
            object result = null;
            Exception error = null;
            ManualResetEventSlim done = new ManualResetEventSlim(false);
            Action wrapped = delegate()
            {
                try { result = fn(); }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            };
            if (!Dispatch(wrapped))
            {
                wrapped();
            }
            else if (!done.Wait(timeoutMs))
            {
                throw new TimeoutException("Sync: the game thread did not run the task within " + timeoutMs + " ms (is the game paused or stuck?)");
            }
            if (error != null)
                throw new Exception("Sync: task threw " + error.GetType().Name + ": " + error.Message, error);
            return result;
        }

        public void Sync(Action action)
        {
            Sync(delegate() { action(); return null; });
        }

        private bool Dispatch(Action action)
        {
            if (Role == "client")
            {
                object g = (object)Game;
                Microsoft.Xna.Framework.Game game = g as Microsoft.Xna.Framework.Game;
                if (game == null)
                    return false;
                if (pump == null)
                {
                    pump = new DebugPump(game);
                    game.Components.Add(pump);
                }
                pump.Enqueue(action);
                return true;
            }
            object s = (object)Server;
            if (s == null)
                return false;
            MethodInfo addTask = s.GetType().GetMethod("AddTask", new Type[] { typeof(global::Vexillum.util.TaskDelegate) });
            if (addTask == null)
                return false;
            addTask.Invoke(s, new object[] { new global::Vexillum.util.TaskDelegate(action) });
            return true;
        }
    }

    /// <summary>Drains queued actions on the MonoGame Update thread.</summary>
    public sealed class DebugPump : Microsoft.Xna.Framework.GameComponent
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> queue = new System.Collections.Concurrent.ConcurrentQueue<Action>();

        public DebugPump(Microsoft.Xna.Framework.Game game) : base(game)
        {
            UpdateOrder = int.MaxValue;
        }

        public void Enqueue(Action a)
        {
            queue.Enqueue(a);
        }

        public override void Update(Microsoft.Xna.Framework.GameTime gameTime)
        {
            Action a;
            while (queue.TryDequeue(out a))
                a();
        }
    }

    internal static class Reflect
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;

        public static Type FindType(string name)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = a.GetType(name, false); } catch (Exception) { }
                if (t != null)
                    return t;
            }
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = a.GetTypes(); } catch (Exception) { continue; }
                foreach (Type t in types)
                    if (t.Name == name || t.FullName == name)
                        return t;
            }
            throw new ArgumentException("type not found: " + name);
        }

        private static MemberInfo Find(Type t, string name)
        {
            for (Type cur = t; cur != null; cur = cur.BaseType)
            {
                FieldInfo f = cur.GetField(name, Any);
                if (f != null) return f;
                PropertyInfo p = cur.GetProperty(name, Any);
                if (p != null) return p;
            }
            throw new ArgumentException("no field or property '" + name + "' on " + t.FullName);
        }

        public static object Get(object target, string name)
        {
            if (target == null) throw new ArgumentNullException("target");
            Type t = target as Type ?? target.GetType();
            object instance = target is Type ? null : target;
            MemberInfo m = Find(t, name);
            FieldInfo f = m as FieldInfo;
            if (f != null) return f.GetValue(f.IsStatic ? null : instance);
            PropertyInfo p = (PropertyInfo)m;
            return p.GetValue(p.GetGetMethod(true).IsStatic ? null : instance);
        }

        public static object GetStatic(Type t, string name)
        {
            return Get(t, name);
        }

        public static void Set(object target, string name, object value)
        {
            Type t = target as Type ?? target.GetType();
            object instance = target is Type ? null : target;
            MemberInfo m = Find(t, name);
            FieldInfo f = m as FieldInfo;
            if (f != null) { f.SetValue(f.IsStatic ? null : instance, Convert(value, f.FieldType)); return; }
            PropertyInfo p = (PropertyInfo)m;
            p.SetValue(p.GetSetMethod(true).IsStatic ? null : instance, Convert(value, p.PropertyType));
        }

        private static object Convert(object value, Type to)
        {
            if (value == null || to.IsInstanceOfType(value)) return value;
            if (to.IsEnum) return Enum.Parse(to, value.ToString());
            return System.Convert.ChangeType(value, to);
        }

        public static object Call(object target, string name, object[] args)
        {
            Type t = target as Type ?? target.GetType();
            object instance = target is Type ? null : target;
            args = args ?? new object[0];
            for (Type cur = t; cur != null; cur = cur.BaseType)
            {
                foreach (MethodInfo mi in cur.GetMethods(Any))
                {
                    if (mi.Name != name || mi.GetParameters().Length != args.Length)
                        continue;
                    try
                    {
                        object[] conv = new object[args.Length];
                        ParameterInfo[] ps = mi.GetParameters();
                        for (int i = 0; i < args.Length; i++)
                            conv[i] = Convert(args[i], ps[i].ParameterType);
                        return mi.Invoke(mi.IsStatic ? null : instance, conv);
                    }
                    catch (TargetInvocationException ex)
                    {
                        throw ex.InnerException ?? ex;
                    }
                    catch (InvalidCastException) { }
                    catch (FormatException) { }
                }
            }
            throw new ArgumentException("no method '" + name + "' with " + args.Length + " parameters on " + t.FullName);
        }
    }

    internal static class Formatter
    {
        public static string Format(object o)
        {
            if (o == null) return "null";
            if (o is string) return (string)o;
            Type t = o.GetType();
            if (t.IsPrimitive || t.IsEnum || o is decimal || o is DateTime || o is TimeSpan || o is Guid)
                return o.ToString();
            if (o is Type) return ((Type)o).FullName;
            System.Collections.IEnumerable seq = o as System.Collections.IEnumerable;
            if (seq != null && !(o is System.Collections.IDictionary))
            {
                StringBuilder sb = new StringBuilder();
                int n = 0;
                foreach (object item in seq)
                {
                    if (n++ >= 200) { sb.Append("... (truncated at 200)\n"); break; }
                    sb.Append(ItemLine(item)).Append('\n');
                }
                return "[" + n + (n >= 200 ? "+" : "") + " items]\n" + sb.ToString().TrimEnd();
            }
            if (HasOwnToString(t))
                return o.ToString();
            return Dump(o, false);
        }

        /// <summary>One-line, non-recursive rendering of a member value (never calls Dump).</summary>
        private static string Line(object item)
        {
            if (item == null) return "null";
            Type t = item.GetType();
            if (t.IsPrimitive || t.IsEnum || item is string || HasOwnToString(t))
                return item.ToString();
            if (item is Type) return ((Type)item).FullName;
            System.Collections.ICollection col = item as System.Collections.ICollection;
            if (col != null) return "<" + t.Name + " x" + col.Count + ">";
            return "<" + t.Name + ">";
        }

        /// <summary>Shallow dump of one item on one line (members rendered with Line, so bounded depth).</summary>
        private static string ItemLine(object item)
        {
            if (item == null) return "null";
            Type t = item.GetType();
            if (t.IsPrimitive || t.IsEnum || item is string || HasOwnToString(t))
                return item.ToString();
            return Dump(item, false).Replace("\n", "; ");
        }

        private static bool HasOwnToString(Type t)
        {
            MethodInfo m = t.GetMethod("ToString", Type.EmptyTypes);
            return m != null && m.DeclaringType != typeof(object) && m.DeclaringType != typeof(ValueType);
        }

        public static string Dump(object o, bool includeNonPublic)
        {
            if (o == null) return "null";
            Type t = o.GetType();
            StringBuilder sb = new StringBuilder(t.FullName);
            BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
            if (includeNonPublic) flags |= BindingFlags.NonPublic;
            HashSet<string> seen = new HashSet<string>();
            for (Type cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                foreach (FieldInfo f in cur.GetFields(flags | BindingFlags.DeclaredOnly))
                {
                    if (!seen.Add(f.Name) || f.Name.Contains("<")) continue;
                    sb.Append("\n  ").Append(f.Name).Append(" = ").Append(Safe(delegate() { return Line(f.GetValue(f.IsStatic ? null : o)); }));
                }
                foreach (PropertyInfo p in cur.GetProperties(flags | BindingFlags.DeclaredOnly))
                {
                    if (p.GetIndexParameters().Length > 0 || !seen.Add(p.Name)) continue;
                    MethodInfo g = p.GetGetMethod(includeNonPublic);
                    if (g == null) continue;
                    sb.Append("\n  ").Append(p.Name).Append(" = ").Append(Safe(delegate() { return Line(g.Invoke(g.IsStatic ? null : o, null)); }));
                }
            }
            return sb.ToString();
        }

        private static string Safe(Func<string> f)
        {
            try { return f(); }
            catch (Exception ex) { return "<" + (ex.InnerException ?? ex).GetType().Name + ">"; }
        }
    }
}
