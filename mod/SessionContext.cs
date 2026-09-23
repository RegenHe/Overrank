using System;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Team17.Online;

namespace Overrank
{
    internal static class SessionContext
    {
        internal static bool TryReadLobby(out string lobbyKey, out int memberCount)
        {
            lobbyKey = string.Empty;
            memberCount = 1;
            try
            {
                IOnlinePlatformManager platform = GameUtils.RequireManagerInterface<IOnlinePlatformManager>();
                IOnlineMultiplayerSessionCoordinator coordinator = platform == null
                    ? null
                    : platform.OnlineMultiplayerSessionCoordinator();
                if (coordinator == null)
                {
                    return false;
                }

                IOnlineMultiplayerSessionUserId[] members = coordinator.Members();
                if (members == null || members.Length == 0)
                {
                    return false;
                }
                memberCount = Math.Max(1, members.Length);

                FieldInfo lobbyField = coordinator.GetType().GetField(
                    "m_lobbyId",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                object lobby = lobbyField == null ? null : lobbyField.GetValue(coordinator);
                if (lobby == null)
                {
                    return false;
                }
                FieldInfo idField = lobby.GetType().GetField(
                    "m_SteamID",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (idField == null)
                {
                    return false;
                }
                ulong lobbyId = Convert.ToUInt64(idField.GetValue(lobby));
                if (lobbyId == 0UL)
                {
                    return false;
                }
                lobbyKey = Hash("overrank-lobby:" + lobbyId);
                return true;
            }
            catch
            {
                lobbyKey = string.Empty;
                memberCount = 1;
                return false;
            }
        }

        private static string Hash(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
                StringBuilder builder = new StringBuilder(64);
                for (int index = 0; index < bytes.Length; index++)
                {
                    builder.Append(bytes[index].ToString("x2"));
                }
                return builder.ToString();
            }
        }
    }
}
