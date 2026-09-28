// Offline implementation of the Steamworks.NET surface used by Vexillum
// (docs/PORTING.md step 5). Type and member names, signatures and enum values
// follow Steamworks.NET so the historical sources (Game/Game/steam/*,
// game/Identity.cs, net/Client.cs, ui/MainMenu.cs, Vexillum.cs,
// Server/ServerSteamAPI.cs, Server/ServerPlayer.cs) compile unchanged and a
// real Steamworks.NET can be dropped in later without touching them.
//
// Semantics: there is no Steam client. Init() succeeds so the game does not
// exit at startup, callbacks never fire on their own, auth tickets are empty
// (length 0, HAuthTicket.Invalid) and the server accepts every ticket. The
// local Steam id is a stable per-process pseudo id derived from the OS user
// name and the process id, so two clients on one machine get different ids
// (Client packet 5 identifies the local player by steamId).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Steamworks
{
    // ------------------------------------------------------------------
    // Handles and ids
    // ------------------------------------------------------------------

    public struct CSteamID : IEquatable<CSteamID>, IComparable<CSteamID>
    {
        public static readonly CSteamID Nil = new CSteamID(0);

        public ulong m_SteamID;

        public CSteamID(ulong id)
        {
            m_SteamID = id;
        }

        public bool IsValid() { return m_SteamID != 0; }

        public override string ToString() { return m_SteamID.ToString(); }
        public override bool Equals(object other) { return other is CSteamID && this == (CSteamID)other; }
        public override int GetHashCode() { return m_SteamID.GetHashCode(); }
        public bool Equals(CSteamID other) { return m_SteamID == other.m_SteamID; }
        public int CompareTo(CSteamID other) { return m_SteamID.CompareTo(other.m_SteamID); }

        public static bool operator ==(CSteamID x, CSteamID y) { return x.m_SteamID == y.m_SteamID; }
        public static bool operator !=(CSteamID x, CSteamID y) { return !(x == y); }
        public static explicit operator CSteamID(ulong value) { return new CSteamID(value); }
        public static explicit operator ulong(CSteamID that) { return that.m_SteamID; }
    }

    public struct HAuthTicket : IEquatable<HAuthTicket>, IComparable<HAuthTicket>
    {
        public static readonly HAuthTicket Invalid = new HAuthTicket(0);

        public uint m_HAuthTicket;

        public HAuthTicket(uint value)
        {
            m_HAuthTicket = value;
        }

        public override string ToString() { return m_HAuthTicket.ToString(); }
        public override bool Equals(object other) { return other is HAuthTicket && this == (HAuthTicket)other; }
        public override int GetHashCode() { return m_HAuthTicket.GetHashCode(); }
        public bool Equals(HAuthTicket other) { return m_HAuthTicket == other.m_HAuthTicket; }
        public int CompareTo(HAuthTicket other) { return m_HAuthTicket.CompareTo(other.m_HAuthTicket); }

        public static bool operator ==(HAuthTicket x, HAuthTicket y) { return x.m_HAuthTicket == y.m_HAuthTicket; }
        public static bool operator !=(HAuthTicket x, HAuthTicket y) { return !(x == y); }
        public static explicit operator HAuthTicket(uint value) { return new HAuthTicket(value); }
        public static explicit operator uint(HAuthTicket that) { return that.m_HAuthTicket; }
    }

    public struct AppId_t : IEquatable<AppId_t>, IComparable<AppId_t>
    {
        public static readonly AppId_t Invalid = new AppId_t(0x0);

        public uint m_AppId;

        public AppId_t(uint value)
        {
            m_AppId = value;
        }

        public override string ToString() { return m_AppId.ToString(); }
        public override bool Equals(object other) { return other is AppId_t && this == (AppId_t)other; }
        public override int GetHashCode() { return m_AppId.GetHashCode(); }
        public bool Equals(AppId_t other) { return m_AppId == other.m_AppId; }
        public int CompareTo(AppId_t other) { return m_AppId.CompareTo(other.m_AppId); }

        public static bool operator ==(AppId_t x, AppId_t y) { return x.m_AppId == y.m_AppId; }
        public static bool operator !=(AppId_t x, AppId_t y) { return !(x == y); }
        // SteamManager.Initialize writes "(AppId_t)349380" (an int literal).
        public static explicit operator AppId_t(int value) { return new AppId_t(unchecked((uint)value)); }
        public static explicit operator AppId_t(uint value) { return new AppId_t(value); }
        public static explicit operator uint(AppId_t that) { return that.m_AppId; }
    }

    // ------------------------------------------------------------------
    // Enums (values as in Steamworks.NET / steam_api.h)
    // ------------------------------------------------------------------

    public enum EAuthSessionResponse : int
    {
        k_EAuthSessionResponseOK = 0,
        k_EAuthSessionResponseUserNotConnectedToSteam = 1,
        k_EAuthSessionResponseNoLicenseOrExpired = 2,
        k_EAuthSessionResponseVACBanned = 3,
        k_EAuthSessionResponseLoggedInElseWhere = 4,
        k_EAuthSessionResponseVACCheckTimedOut = 5,
        k_EAuthSessionResponseAuthTicketCanceled = 6,
        k_EAuthSessionResponseAuthTicketInvalidAlreadyUsed = 7,
        k_EAuthSessionResponseAuthTicketInvalid = 8,
        k_EAuthSessionResponsePublisherIssuedBan = 9,
        k_EAuthSessionResponseAuthTicketNetworkIdentityFailure = 10,
    }

    public enum EBeginAuthSessionResult : int
    {
        k_EBeginAuthSessionResultOK = 0,
        k_EBeginAuthSessionResultInvalidTicket = 1,
        k_EBeginAuthSessionResultDuplicateRequest = 2,
        k_EBeginAuthSessionResultInvalidVersion = 3,
        k_EBeginAuthSessionResultGameMismatch = 4,
        k_EBeginAuthSessionResultExpiredTicket = 5,
    }

    // ------------------------------------------------------------------
    // Callback payloads
    // ------------------------------------------------------------------

    public struct GameOverlayActivated_t
    {
        public const int k_iCallback = 331;
        public byte m_bActive;
        public bool m_bUserInitiated;
        public AppId_t m_nAppID;
    }

    public struct ValidateAuthTicketResponse_t
    {
        public const int k_iCallback = 143;
        public CSteamID m_SteamID;
        public EAuthSessionResponse m_eAuthSessionResponse;
        public CSteamID m_OwnerSteamID;
    }

    public delegate void SteamAPIWarningMessageHook_t(int nSeverity, StringBuilder pchDebugText);

    // ------------------------------------------------------------------
    // Callback<T>
    // ------------------------------------------------------------------

    /// <summary>
    /// Registers a handler for a Steam callback struct. Without a Steam client
    /// nothing raises them on its own; a payload queued with
    /// CallbackDispatcher.Post is delivered by SteamAPI.RunCallbacks().
    /// </summary>
    public sealed class Callback<T> : IDisposable, CallbackDispatcher.IOfflineCallback
    {
        public delegate void DispatchDelegate(T param);

        private DispatchDelegate m_Func;
        private bool m_bDisposed;

        public static Callback<T> Create(DispatchDelegate func)
        {
            return new Callback<T>(func, false);
        }

        public static Callback<T> CreateGameServer(DispatchDelegate func)
        {
            return new Callback<T>(func, true);
        }

        public Callback(DispatchDelegate func, bool bGameServer = false)
        {
            Register(func);
        }

        ~Callback()
        {
            Dispose();
        }

        public void Dispose()
        {
            if (m_bDisposed) return;
            GC.SuppressFinalize(this);
            Unregister();
            m_bDisposed = true;
        }

        public void Register(DispatchDelegate func)
        {
            if (func == null) throw new ArgumentNullException("func");
            if (m_Func != null) Unregister();
            m_Func = func;
            CallbackDispatcher.Register(typeof(T), this);
        }

        public void Unregister()
        {
            CallbackDispatcher.Unregister(typeof(T), this);
            m_Func = null;
        }

        void CallbackDispatcher.IOfflineCallback.Invoke(object param)
        {
            DispatchDelegate f = m_Func;
            if (f != null) f((T)param);
        }
    }

    /// <summary>
    /// In-process replacement for the Steam callback queue. Payloads posted
    /// with Post are delivered on the next SteamAPI.RunCallbacks(), i.e. from
    /// the game's update loop, like the real thing. Nothing in the shim posts
    /// on its own.
    /// </summary>
    public static class CallbackDispatcher
    {
        internal interface IOfflineCallback { void Invoke(object param); }

        private static readonly object s_Lock = new object();
        private static readonly Dictionary<Type, List<IOfflineCallback>> s_Callbacks = new Dictionary<Type, List<IOfflineCallback>>();
        private static readonly Queue<KeyValuePair<Type, object>> s_Pending = new Queue<KeyValuePair<Type, object>>();

        internal static void Register(Type type, IOfflineCallback cb)
        {
            lock (s_Lock)
            {
                List<IOfflineCallback> list;
                if (!s_Callbacks.TryGetValue(type, out list))
                {
                    list = new List<IOfflineCallback>();
                    s_Callbacks[type] = list;
                }
                if (!list.Contains(cb)) list.Add(cb);
            }
        }

        internal static void Unregister(Type type, IOfflineCallback cb)
        {
            lock (s_Lock)
            {
                List<IOfflineCallback> list;
                if (s_Callbacks.TryGetValue(type, out list)) list.Remove(cb);
            }
        }

        /// <summary>Queue a callback payload for delivery on the next SteamAPI.RunCallbacks().</summary>
        public static void Post<T>(T param) where T : struct
        {
            lock (s_Lock) s_Pending.Enqueue(new KeyValuePair<Type, object>(typeof(T), param));
        }

        internal static void RunCallbacks()
        {
            while (true)
            {
                KeyValuePair<Type, object> item;
                IOfflineCallback[] targets;
                lock (s_Lock)
                {
                    if (s_Pending.Count == 0) return;
                    item = s_Pending.Dequeue();
                    List<IOfflineCallback> list;
                    targets = s_Callbacks.TryGetValue(item.Key, out list) ? list.ToArray() : new IOfflineCallback[0];
                }
                foreach (IOfflineCallback target in targets)
                    target.Invoke(item.Value);
            }
        }
    }

    // ------------------------------------------------------------------
    // Static API classes
    // ------------------------------------------------------------------

    public static class Packsize
    {
        public static bool Test() { return true; }
    }

    public static class DllCheck
    {
        public static bool Test() { return true; }
    }

    public static class SteamAPI
    {
        private static bool s_Initialized;

        /// <summary>Always succeeds offline so the game does not exit at startup.</summary>
        public static bool Init()
        {
            s_Initialized = true;
            return true;
        }

        public static void Shutdown()
        {
            s_Initialized = false;
        }

        public static bool IsSteamRunning() { return false; }

        /// <summary>Never restarts: there is no Steam client to launch through.</summary>
        public static bool RestartAppIfNecessary(AppId_t unOwnAppID) { return false; }

        public static void RunCallbacks()
        {
            if (s_Initialized) CallbackDispatcher.RunCallbacks();
        }
    }

    public static class SteamUser
    {
        private static readonly object s_Lock = new object();
        private static CSteamID s_LocalId;
        private static bool s_LocalIdComputed;

        /// <summary>
        /// Stable per-process pseudo id: derived from the OS user name and the
        /// process id so two clients on one machine differ, and the same
        /// process always answers the same id.
        /// </summary>
        public static CSteamID GetSteamID()
        {
            lock (s_Lock)
            {
                if (!s_LocalIdComputed)
                {
                    int pid;
                    try { pid = Process.GetCurrentProcess().Id; } catch { pid = Environment.TickCount; }
                    s_LocalId = ComputeOfflineSteamID(SteamFriends.GetPersonaName(), pid);
                    s_LocalIdComputed = true;
                }
                return s_LocalId;
            }
        }

        /// <summary>
        /// Deterministic derivation used by GetSteamID: SHA-256 of
        /// "userName\0processId", first 8 bytes, never 0 (0 is CSteamID.Nil).
        /// </summary>
        public static CSteamID ComputeOfflineSteamID(string userName, int processId)
        {
            byte[] seed = Encoding.UTF8.GetBytes((userName ?? "") + "\0" + processId.ToString());
            byte[] hash;
            using (SHA256 sha = SHA256.Create()) hash = sha.ComputeHash(seed);
            ulong id = BitConverter.ToUInt64(hash, 0);
            if (id == 0) id = 1;
            return new CSteamID(id);
        }

        /// <summary>Offline: no ticket. pcbTicket is 0 and the handle is HAuthTicket.Invalid.</summary>
        public static HAuthTicket GetAuthSessionTicket(byte[] pTicket, int cbMaxTicket, out uint pcbTicket)
        {
            pcbTicket = 0;
            return HAuthTicket.Invalid;
        }

        public static void CancelAuthTicket(HAuthTicket hAuthTicket) { }

        /// <summary>Offline: every ticket (including an empty one) is accepted.</summary>
        public static EBeginAuthSessionResult BeginAuthSession(byte[] pAuthTicket, int cbAuthTicket, CSteamID steamID)
        {
            return EBeginAuthSessionResult.k_EBeginAuthSessionResultOK;
        }

        public static void EndAuthSession(CSteamID steamID) { }

        public static bool BLoggedOn() { return false; }
    }

    public static class SteamFriends
    {
        public static string GetPersonaName()
        {
            string name = null;
            try { name = Environment.UserName; } catch { }
            return string.IsNullOrWhiteSpace(name) ? "Player" : name;
        }
    }

    public static class SteamUtils
    {
        private static SteamAPIWarningMessageHook_t s_Hook;

        public static void SetWarningMessageHook(SteamAPIWarningMessageHook_t pFunction)
        {
            s_Hook = pFunction;
        }

        public static AppId_t GetAppID() { return (AppId_t)349380; }
        public static bool IsOverlayEnabled() { return false; }
    }
}
