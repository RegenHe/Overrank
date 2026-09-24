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
            ulong ignoredLobbyId;
            return TryReadLobby(out ignoredLobbyId, out lobbyKey, out memberCount);
        }

        internal static bool TryReadLobby(out ulong lobbyId, out string lobbyKey, out int memberCount)
        {
            lobbyId = 0UL;
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
                lobbyId = Convert.ToUInt64(idField.GetValue(lobby));
                if (lobbyId == 0UL)
                {
                    return false;
                }
                IOnlineMultiplayerSessionUserId[] members = coordinator.Members();
                memberCount = members == null ? 1 : Math.Max(1, members.Length);
                lobbyKey = Hash("overrank-lobby:" + lobbyId);
                return true;
            }
            catch
            {
                lobbyId = 0UL;
                lobbyKey = string.Empty;
                memberCount = 1;
                return false;
            }
        }

        internal static bool RequestJoinLobby(ulong lobbyId, out string error)
        {
            error = string.Empty;
            if (lobbyId == 0UL)
            {
                error = "Invalid Steam lobby ID.";
                return false;
            }
            try
            {
                ulong currentLobbyId;
                string ignoredLobbyKey;
                int ignoredMemberCount;
                if (TryReadLobby(out currentLobbyId, out ignoredLobbyKey, out ignoredMemberCount)
                    && currentLobbyId == lobbyId)
                {
                    return true;
                }

                IOnlinePlatformManager platform = GameUtils.RequireManagerInterface<IOnlinePlatformManager>();
                object inviteCoordinator = platform == null
                    ? null
                    : platform.OnlineMultiplayerGameInviteCoordinator();
                if (inviteCoordinator == null)
                {
                    error = "The game's Steam invite coordinator is unavailable.";
                    return false;
                }

                FieldInfo acceptedField = inviteCoordinator.GetType().GetField(
                    "m_acceptedInvite",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (acceptedField == null)
                {
                    error = "The game's accepted-invite field was not found.";
                    return false;
                }
                if (acceptedField.GetValue(inviteCoordinator) != null)
                {
                    error = "The game is already processing another Steam invite.";
                    return false;
                }

                OnlineMultiplayerSessionInvite invite = new OnlineMultiplayerSessionInvite();
                FieldInfo lobbyField = invite.GetType().GetField(
                    "m_steamLobbyId",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (lobbyField == null)
                {
                    error = "The game's Steam lobby invite field was not found.";
                    return false;
                }
                object steamId = Activator.CreateInstance(lobbyField.FieldType, new object[] { lobbyId });
                lobbyField.SetValue(invite, steamId);
                acceptedField.SetValue(inviteCoordinator, invite);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
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
