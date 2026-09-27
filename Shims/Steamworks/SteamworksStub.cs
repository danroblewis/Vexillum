// Steamworks stub for compilation without Steamworks.NET
// This allows the code to compile but Steam features will not work

namespace Steamworks
{
    public struct CSteamID
    {
        public ulong m_SteamID;
        
        public CSteamID(ulong id)
        {
            m_SteamID = id;
        }
    }
    
    public struct HAuthTicket
    {
        public uint m_HAuthTicket;
        public static HAuthTicket Invalid = new HAuthTicket { m_HAuthTicket = 0 };
    }
    
    public struct AppId_t
    {
        public uint m_AppId;
        public AppId_t(uint value) { m_AppId = value; }
    }
    
    public struct GameOverlayActivated_t
    {
        public byte m_bActive;
    }
    
    public delegate void SteamAPIWarningMessageHook_t(int nSeverity, System.Text.StringBuilder pchDebugText);
    
    public class Callback<T>
    {
        public delegate void DispatchDelegate(T param);
        
        public Callback(System.Action<T> callback) { }
        public Callback(DispatchDelegate callback) { }
        public static Callback<T> Create(DispatchDelegate callback) { return new Callback<T>(callback); }
        public void Dispose() { }
    }
    
    public static class DllCheck
    {
        public static uint Packsize = 0;
    }
    
    public static class Packsize
    {
        public static uint Test = 0;
    }
    
    public static class SteamAPI
    {
        public static bool Init() { return false; }
        public static void Shutdown() { }
        public static void RunCallbacks() { }
        public static bool RestartAppIfNecessary(AppId_t appId) { return false; }
    }
    
    public static class SteamUser
    {
        public static CSteamID GetSteamID() { return new CSteamID(0); }
        public static HAuthTicket GetAuthSessionTicket(byte[] pTicket, int cbMaxTicket, out uint pcbTicket)
        {
            pcbTicket = 0;
            return new HAuthTicket();
        }
        public static void CancelAuthTicket(HAuthTicket ticket) { }
    }
    
    public static class SteamFriends
    {
        public static string GetPersonaName() { return "Player"; }
    }
    
    public static class SteamUtils
    {
        public static void SetWarningMessageHook(SteamAPIWarningMessageHook_t hook) { }
    }
}

