using System;

namespace Steamworks
{
    public struct CSteamID
    {
        public ulong m_SteamID;

        public CSteamID(ulong steamId)
        {
            m_SteamID = steamId;
        }
    }

    public struct LobbyDataUpdate_t
    {
        public ulong m_ulSteamIDLobby;
        public ulong m_ulSteamIDMember;
        public byte m_bSuccess;
    }

    public sealed class Callback<T>
    {
        public delegate void DispatchDelegate(T param);

        public static Callback<T> Create(DispatchDelegate action)
        {
            return new Callback<T>();
        }

        public void Dispose()
        {
        }
    }

    public static class SteamAPI
    {
        public static void RunCallbacks()
        {
        }
    }

    public static class SteamMatchmaking
    {
        public static int GetNumLobbyMembers(CSteamID steamId)
        {
            return 0;
        }

        public static int GetLobbyMemberLimit(CSteamID steamId)
        {
            return 0;
        }

        public static bool RequestLobbyData(CSteamID steamId)
        {
            return false;
        }
    }
}
