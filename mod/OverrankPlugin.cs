using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Team17.Online;
using Team17.Online.Multiplayer.Messaging;
using UnityEngine;

namespace Overrank
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class OverrankPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "local.overcooked2.overrank";
        public const string PluginName = "Overrank";
        public const string PluginVersion = BuildInfo.Version;

        private ManualLogSource _log;
        private Harmony _harmony;
        private LeaderboardClient _client;
        private string _serverUrl;
        private ConfigEntry<string> _installId;
        private ConfigEntry<string> _lastLevelKey;
        private ConfigEntry<string> _lastLevelName;
        private ConfigEntry<int> _lastPlayerCount;
        private ConfigEntry<string> _savedRoomTitle;
        private ConfigEntry<string> _savedRoomDescription;
        private ConfigEntry<bool> _savedRoomLocked;

        private Texture2D _iconBackground;
        private Texture2D _icon;
        private Texture2D _overwashedIcon;
        private Texture2D _lockIcon;
        private Texture2D _separatorTexture;
        private Texture2D _panelBackground;
        private bool _showPanel;
        private bool _showLevels;
        private int _roomPage;
        private bool _requestRunning;
        private string _requestError;
        private string _responseSummary;
        private string _presenceSummary;
        private string _lastPresenceError;
        private bool _roomMessageUnread;
        private string _clientId;
        private string _playerId;
        private string _playerName = "Player";
        private bool _hasEngagedPlayer;
        private string _selectedLevelKey;
        private string _selectedLevelName;
        private string _metric = "score";
        private int _players = 1;
        private int _levelsPage;
        private int _nearbyMode;
        private bool _nearbySelectedOverwashed;
        private bool _centerNearbyOnSelf;
        private Vector2 _topScroll;
        private Vector2 _nearbyScroll;
        private float _nextIdentityRefresh;
        private float _nextContextRefresh;
        private float _nextPresenceHeartbeat;
        private string _lastPresenceFingerprint;
        private bool _attemptInLevel;
        private string _attemptLevelUid;
        private string _attemptNonce;
        private int _attemptSessionToken;
        private string _roundId;
        private bool _roundAssistanceKnown;
        private float _lastCaptureTime = -100f;
        private string _lastCaptureFingerprint;
        private LeaderboardResponse _leaderboard;
        private string _leaderboardCacheKey;
        private LeaderboardResponse _overallLeaderboard;
        private LeaderboardResponse _unassistedLeaderboard;
        private LeaderboardResponse _assistedLeaderboard;
        private PlayedLevelsResponse _playedLevels;
        private RoomListResponse _roomList;
        private RoomInfo _currentRoom;
        private string _currentRoomId;
        private string _roomHostToken;
        private bool _isRoomHost;
        private float _roomLobbyMissingSince = -1f;
        private string _lastObservedRoomStatus;
        private float _nextRoomStatusCheck;
        private float _nextRoomMembershipCheck;
        private bool _roomRequestRunning;
        private bool _roomRefreshRunning;
        private bool _roomHeartbeatRunning;
        private bool _creatingGameLobby;
        private float _gameLobbyCreateDeadline;
        private float _nextGameLobbyCheck;
        private RoomInfo _pendingJoinedRoom;
        private ulong _pendingSteamLobbyId;
        private float _steamLobbyJoinDeadline;
        private float _nextSteamLobbyJoinCheck;
        private float _nextPendingJoinHeartbeat;
        private string _roomError;
        private string _createRoomTitle;
        private string _createRoomDescription;
        private string _createRoomPassword;
        private bool _createRoomLocked;
        private string _joinRoomPassword;
        private string _directRoomId;
        private string _chatInput;
        private string _lobbyChatInput;
        private bool _focusRoomChatInput;
        private bool _focusLobbyChatInput;
        private int _lastRoomMessageId;
        private int _lastLobbyMessageId;
        private int _roomListRevision;
        private float _nextRoomRefresh;
        private float _nextManualRoomRefresh;
        private float _nextAllowedRoomListRequest;
        private float _nextManualLeaderboardRefresh;
        private Vector2 _roomListScroll;
        private Vector2 _roomMemberScroll;
        private Vector2 _roomChatScroll;
        private Vector2 _lobbyChatScroll;

        private void Awake()
        {
            _log = Logger;
            _serverUrl = BuildConfig.DefaultServerUrl;
            _installId = Config.Bind("Identity", "InstallId", string.Empty, "Fallback anonymous installation identifier.");
            _lastLevelKey = Config.Bind("State", "LastLevelKey", string.Empty, "Last played level shown in Overrank.");
            _lastLevelName = Config.Bind("State", "LastLevelName", string.Empty, "Last played level display name.");
            _lastPlayerCount = Config.Bind(
                "State",
                "LastPlayerCount",
                1,
                new ConfigDescription("Last played player count.", new AcceptableValueRange<int>(1, 4)));
            _savedRoomTitle = Config.Bind(
                "Room",
                "Title",
                string.Empty,
                "Last room title, used to fill the create-room form.");
            _savedRoomDescription = Config.Bind(
                "Room",
                "Description",
                string.Empty,
                "Last room description, used to fill the create-room form.");
            _savedRoomLocked = Config.Bind(
                "Room",
                "UsePassword",
                false,
                "Whether password protection is enabled by default. The password itself is never saved.");

            if (string.IsNullOrEmpty(_installId.Value))
            {
                _installId.Value = Guid.NewGuid().ToString("N");
            }
            _selectedLevelKey = _lastLevelKey.Value ?? string.Empty;
            _selectedLevelName = _lastLevelName.Value ?? string.Empty;
            _players = Mathf.Clamp(_lastPlayerCount.Value, 1, 4);
            _createRoomTitle = _savedRoomTitle.Value ?? string.Empty;
            _createRoomDescription = _savedRoomDescription.Value ?? string.Empty;
            _createRoomLocked = _savedRoomLocked.Value;
            _clientId = HashIdentity("install:" + _installId.Value);
            _playerId = _clientId;

            CreateIconTextures();
            RefreshIdentity();
            _client = new LeaderboardClient(this, _log, _serverUrl, BuildConfig.DefaultApiKey, 8);
            _client.SubmissionUploaded += OnSubmissionUploaded;
            _client.Start();

            ScoreCapturePatch.LevelFinished = OnLevelFinished;
            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(ScoreCapturePatch).Assembly);
            _log.LogInfo("Overrank " + PluginVersion + " loaded. Server endpoint is embedded in the DLL.");
        }

        private void Update()
        {
            if (Time.unscaledTime >= _nextIdentityRefresh)
            {
                _nextIdentityRefresh = Time.unscaledTime + 5f;
                RefreshIdentity();
            }
            if (Time.unscaledTime >= _nextContextRefresh)
            {
                _nextContextRefresh = Time.unscaledTime + 1f;
                UpdateAttemptAndPresence();
            }
            UpdatePendingGameLobbyCreation();
            UpdatePendingSteamLobbyJoin();
            UpdateGameLobbyMembership();
            UpdateRoomStatusTransition();
            UpdateRoomState();
        }

        private void OnDestroy()
        {
            SaveRoomPreferences();
            ScoreCapturePatch.LevelFinished = null;
            if (_client != null)
            {
                _client.SubmissionUploaded -= OnSubmissionUploaded;
            }
            if (_harmony != null)
            {
                _harmony.UnpatchSelf();
                _harmony = null;
            }
            if (_iconBackground != null)
            {
                Destroy(_iconBackground);
            }
            if (_icon != null)
            {
                Destroy(_icon);
            }
            if (_overwashedIcon != null)
            {
                Destroy(_overwashedIcon);
            }
            if (_lockIcon != null)
            {
                Destroy(_lockIcon);
            }
            if (_separatorTexture != null)
            {
                Destroy(_separatorTexture);
            }
            if (_panelBackground != null)
            {
                Destroy(_panelBackground);
            }
        }

        private void OnLevelFinished(ClientCampaignFlowController flow)
        {
            try
            {
                if (flow == null)
                {
                    return;
                }
                GameSession session = GameUtils.GetGameSession();
                if (session == null || session.LevelSettings == null)
                {
                    return;
                }
                if (session.GameModeKind != GameModes.Kind.Campaign)
                {
                    _log.LogInfo("Ignoring completed level in " + session.GameModeKind + " mode; only Campaign scores are ranked.");
                    return;
                }
                ClientTeamMonitor monitor = flow.GetMonitorForTeam(TeamID.One);
                if (monitor == null || monitor.Score == null)
                {
                    return;
                }

                TeamMonitor.TeamScoreStats score = monitor.Score;
                int playerCount = ClientUserSystem.m_Users == null ? 1 : ClientUserSystem.m_Users.Count;
                playerCount = Mathf.Clamp(playerCount, 1, 4);
                LevelIdentity level = LevelIdentity.Read(session);
                if (level == null || string.IsNullOrEmpty(level.Uid))
                {
                    return;
                }

                string fingerprint = level.Uid + "|" + playerCount + "|" + score.GetTotalScore() + "|" + score.TotalSuccessfulDeliveries;
                if (string.Equals(fingerprint, _lastCaptureFingerprint, StringComparison.Ordinal)
                    && Time.realtimeSinceStartup - _lastCaptureTime < 8f)
                {
                    return;
                }
                _lastCaptureFingerprint = fingerprint;
                _lastCaptureTime = Time.realtimeSinceStartup;
                RefreshIdentity();

                int stars = 0;
                SceneDirectoryData.PerPlayerCountDirectoryEntry variant = session.LevelSettings.SceneDirectoryVarientEntry;
                if (variant != null)
                {
                    stars = Mathf.Clamp(variant.GetStarForPoints(score.GetTotalScore(), false), 0, 4);
                }
                bool overwashedUsed;
                string overwashedVersion;
                OverwashedIntegration.ReadCurrentAssistance(out overwashedUsed, out overwashedVersion);
                EnsureAttempt(level.Uid, session.GetHashCode(), false);
                string lobbyKey;
                int ignoredMemberCount;
                SessionContext.TryReadLobby(out lobbyKey, out ignoredMemberCount);
                ScoreSubmission submission = new ScoreSubmission
                {
                    submission_id = Guid.NewGuid().ToString(),
                    player_id = _playerId,
                    player_name = _playerName,
                    level_uid = level.Uid,
                    dlc_id = level.DlcId,
                    level_id = level.LevelId,
                    level_name = level.SceneName,
                    level_label = level.DisplayName,
                    player_count = playerCount,
                    score = score.GetTotalScore(),
                    dishes = score.TotalSuccessfulDeliveries,
                    stars = stars,
                    mod_version = PluginVersion,
                    overwashed_used = overwashedUsed,
                    overwashed_version = overwashedVersion,
                    completed_at = DateTime.UtcNow.ToString("o"),
                    client_id = _clientId,
                    lobby_key = lobbyKey,
                    attempt_nonce = _attemptNonce,
                    round_id = _roundId
                };

                _selectedLevelKey = level.Uid;
                _selectedLevelName = level.DisplayName;
                _players = playerCount;
                _nearbyMode = 0;
                _lastLevelKey.Value = _selectedLevelKey;
                _lastLevelName.Value = _selectedLevelName;
                _lastPlayerCount.Value = _players;
                _client.Enqueue(submission);
                if (_showPanel && _roomPage == 0 && !_showLevels)
                {
                    RefreshBoard();
                }
            }
            catch (Exception exception)
            {
                _log.LogError("Could not capture the completed level result: " + exception);
            }
        }

        private void RefreshIdentity()
        {
            try
            {
                IPlayerManager manager = GameUtils.RequestManagerInterface<IPlayerManager>();
                GamepadUser user = manager == null ? null : manager.GetUser(EngagementSlot.One);
                if (user == null || string.IsNullOrEmpty(user.UID))
                {
                    _hasEngagedPlayer = false;
                    return;
                }
                _hasEngagedPlayer = true;
                _playerId = HashIdentity(GetStableUserIdentity(user.UID));
                if (!string.IsNullOrEmpty(user.DisplayName))
                {
                    _playerName = user.DisplayName;
                }
            }
            catch
            {
                _hasEngagedPlayer = false;
            }
        }

        private void EnsureAttempt(string levelUid, int sessionToken, bool newIfEntering)
        {
            if ((newIfEntering && !_attemptInLevel)
                || string.IsNullOrEmpty(_attemptNonce)
                || !string.Equals(_attemptLevelUid, levelUid, StringComparison.Ordinal)
                || _attemptSessionToken != sessionToken)
            {
                _attemptNonce = Guid.NewGuid().ToString();
                _attemptLevelUid = levelUid ?? string.Empty;
                _attemptSessionToken = sessionToken;
                _roundId = string.Empty;
                _roundAssistanceKnown = false;
                _nextPresenceHeartbeat = 0f;
            }
            _attemptInLevel = true;
        }

        private void UpdateAttemptAndPresence()
        {
            LevelIdentity activeLevel = null;
            bool inLevel = TryReadActiveLevel(out activeLevel);
            if (inLevel)
            {
                EnsureAttempt(
                    activeLevel.Uid,
                    GameUtils.GetGameSession().GetHashCode(),
                    true);
            }
            else if (_attemptInLevel)
            {
                _attemptInLevel = false;
            }

            string lobbyKey;
            int ignoredMemberCount;
            SessionContext.TryReadLobby(out lobbyKey, out ignoredMemberCount);
            int localPlayers = 1;
            try
            {
                localPlayers = Mathf.Clamp(
                    (int)UserSystemUtils.LocalUserCount(ClientUserSystem.m_Users, true),
                    1,
                    4);
            }
            catch
            {
            }

            string levelUid = inLevel && activeLevel != null ? activeLevel.Uid : string.Empty;
            string nonce = inLevel ? _attemptNonce : string.Empty;
            int playerCount = 1;
            try
            {
                playerCount = Mathf.Clamp(ClientUserSystem.m_Users == null ? 1 : ClientUserSystem.m_Users.Count, 1, 4);
            }
            catch
            {
            }

            bool overwashedUsed = false;
            string overwashedVersion = string.Empty;
            if (inLevel)
            {
                OverwashedIntegration.ReadCurrentAssistance(out overwashedUsed, out overwashedVersion);
            }

            if (inLevel && !string.IsNullOrEmpty(lobbyKey) && string.IsNullOrEmpty(_roundId))
            {
                string joiningNonce = _attemptNonce;
                _client.JoinRound(
                    new RoundJoinRequest
                    {
                        client_id = _clientId,
                        player_id = _playerId,
                        lobby_key = lobbyKey,
                        level_uid = levelUid,
                        player_count = playerCount,
                        attempt_nonce = joiningNonce,
                        overwashed_used = overwashedUsed,
                        overwashed_version = overwashedVersion
                    },
                    delegate(RoundResponse response, string error)
                    {
                        if (response != null
                            && string.IsNullOrEmpty(error)
                            && string.Equals(_attemptNonce, joiningNonce, StringComparison.Ordinal))
                        {
                            _roundId = response.round_id ?? string.Empty;
                            _roundAssistanceKnown = response.overwashed_used;
                            _nextPresenceHeartbeat = 0f;
                        }
                    });
            }

            if (inLevel
                && overwashedUsed
                && !_roundAssistanceKnown
                && !string.IsNullOrEmpty(_roundId))
            {
                string reportingRound = _roundId;
                string reportingNonce = _attemptNonce;
                _client.ReportAssistance(
                    reportingRound,
                    new RoundAssistanceRequest
                    {
                        client_id = _clientId,
                        attempt_nonce = reportingNonce,
                        overwashed_version = overwashedVersion
                    },
                    delegate(RoundResponse response, string error)
                    {
                        if (!string.Equals(_attemptNonce, reportingNonce, StringComparison.Ordinal)
                            || !string.Equals(_roundId, reportingRound, StringComparison.Ordinal))
                        {
                            return;
                        }
                        if (response != null && string.IsNullOrEmpty(error))
                        {
                            _roundAssistanceKnown = response.overwashed_used;
                        }
                        else if (!string.IsNullOrEmpty(error) && error.IndexOf("HTTP 404", StringComparison.Ordinal) >= 0)
                        {
                            _roundId = string.Empty;
                            _roundAssistanceKnown = false;
                        }
                    });
            }

            string heartbeatRound = inLevel ? _roundId : string.Empty;
            string fingerprint = lobbyKey + "|" + inLevel + "|" + levelUid + "|" + nonce + "|" + heartbeatRound + "|" + localPlayers;
            if (Time.unscaledTime < _nextPresenceHeartbeat
                && string.Equals(fingerprint, _lastPresenceFingerprint, StringComparison.Ordinal))
            {
                return;
            }

            bool started = _client.SendPresence(
                new PresenceHeartbeat
                {
                    client_id = _clientId,
                    player_id = _playerId,
                    lobby_key = lobbyKey,
                    level_uid = levelUid,
                    attempt_nonce = nonce,
                    round_id = heartbeatRound,
                    local_players = localPlayers,
                    in_level = inLevel,
                    mod_version = PluginVersion
                },
                delegate(PresenceResponse response, string error)
                {
                    if (response != null && string.IsNullOrEmpty(error))
                    {
                        _lastPresenceError = null;
                        _presenceSummary = OverrankText.IsSimplifiedChinese
                            ? "在线 " + response.online_players + " · 游玩中 " + response.playing_players
                            : "Online " + response.online_players + " · Playing " + response.playing_players;
                        if (!string.IsNullOrEmpty(heartbeatRound)
                            && string.Equals(_attemptNonce, nonce, StringComparison.Ordinal)
                            && string.Equals(_roundId, heartbeatRound, StringComparison.Ordinal))
                        {
                            if (!response.round_valid)
                            {
                                _roundId = string.Empty;
                                _roundAssistanceKnown = false;
                                _nextPresenceHeartbeat = 0f;
                            }
                            else if (response.overwashed_used)
                            {
                                _roundAssistanceKnown = true;
                            }
                        }
                    }
                    else if (!string.IsNullOrEmpty(error))
                    {
                        _presenceSummary = OverrankText.Get(
                            "Online status unavailable",
                            "在线状态暂不可用");
                        _lastPresenceFingerprint = string.Empty;
                        _nextPresenceHeartbeat = Time.unscaledTime + 5f;
                        if (!string.Equals(_lastPresenceError, error, StringComparison.Ordinal))
                        {
                            _lastPresenceError = error;
                            _log.LogWarning("Overrank presence heartbeat failed: " + error);
                        }
                    }
                });
            if (started)
            {
                _lastPresenceFingerprint = fingerprint;
                _nextPresenceHeartbeat = Time.unscaledTime + 60f;
            }
        }

        private static bool TryReadActiveLevel(out LevelIdentity level)
        {
            level = null;
            try
            {
                GameSession session = GameUtils.GetGameSession();
                if (session == null || session.LevelSettings == null)
                {
                    return false;
                }
                SceneDirectoryData.PerPlayerCountDirectoryEntry variant = session.LevelSettings.SceneDirectoryVarientEntry;
                string activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                if (variant == null
                    || string.IsNullOrEmpty(variant.SceneName)
                    || !string.Equals(activeScene, variant.SceneName, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                level = LevelIdentity.Read(session);
                return level != null && !string.IsNullOrEmpty(level.Uid);
            }
            catch
            {
                level = null;
                return false;
            }
        }

        private static string GetStableUserIdentity(string uid)
        {
            // Steam's GamepadUser UID appends the current input device name to the
            // Steam ID.  Ranking identity must not change when the same person
            // switches between keyboard and controller.
            int digitCount = 0;
            while (digitCount < uid.Length && char.IsDigit(uid[digitCount]))
            {
                digitCount++;
            }

            if (digitCount >= 10)
            {
                return "steam:" + uid.Substring(0, digitCount);
            }

            return "game-user:" + uid;
        }

        private static string HashIdentity(string value)
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

        private void OnSubmissionUploaded()
        {
            ClearLeaderboardCache();
            if (_showPanel)
            {
                if (_roomPage == 0 && _showLevels)
                {
                    RefreshLevels();
                }
                else if (_roomPage == 0)
                {
                    RefreshBoard();
                }
            }
        }

        private void OnGUI()
        {
            int previousDepth = GUI.depth;
            Color previousColor = GUI.color;
            GUISkin skin = GUI.skin;
            int previousLabelFontSize = skin == null ? 0 : skin.label.fontSize;
            int previousButtonFontSize = skin == null ? 0 : skin.button.fontSize;
            int previousToggleFontSize = skin == null ? 0 : skin.toggle.fontSize;
            int previousBoxFontSize = skin == null ? 0 : skin.box.fontSize;
            if (skin != null)
            {
                skin.label.fontSize = Mathf.Max(previousLabelFontSize, 14);
                skin.button.fontSize = Mathf.Max(previousButtonFontSize, 14);
                skin.toggle.fontSize = Mathf.Max(previousToggleFontSize, 14);
                skin.box.fontSize = Mathf.Max(previousBoxFontSize, 14);
            }
            GUI.depth = -900;
            GUI.color = Color.white;

            float left = Screen.width - 82f;
            Rect iconRect = new Rect(left, 12f, 32f, 32f);
            if (_roomMessageUnread
                && (Mathf.FloorToInt(Time.unscaledTime * 2f) & 1) == 0)
            {
                GUI.color = new Color(1f, 0.58f, 0.18f, 1f);
            }
            GUI.DrawTexture(iconRect, _iconBackground, ScaleMode.StretchToFill, true);
            GUI.DrawTexture(new Rect(left + 5f, 17f, 22f, 22f), _icon, ScaleMode.ScaleToFit, true);
            GUI.color = Color.white;
            Event current = Event.current;
            if (current != null
                && current.type == EventType.MouseDown
                && current.button == 0
                && iconRect.Contains(current.mousePosition))
            {
                _showPanel = !_showPanel;
                if (_showPanel)
                {
                    _nextRoomRefresh = 0f;
                    OpenLeaderboard();
                }
                current.Use();
            }

            if (_showPanel)
            {
                DrawPanel();
            }
            GUI.color = previousColor;
            GUI.depth = previousDepth;
            if (skin != null)
            {
                skin.label.fontSize = previousLabelFontSize;
                skin.button.fontSize = previousButtonFontSize;
                skin.toggle.fontSize = previousToggleFontSize;
                skin.box.fontSize = previousBoxFontSize;
            }
        }

        private void OpenLeaderboard()
        {
            _roomPage = 0;
            _showLevels = false;
            TrySelectCurrentLevel();
            if (string.IsNullOrEmpty(_selectedLevelKey))
            {
                RefreshLevels(true);
            }
            else
            {
                RefreshBoard();
            }
        }

        private bool TrySelectCurrentLevel()
        {
            try
            {
                GameSession session = GameUtils.GetGameSession();
                if (session == null || session.LevelSettings == null)
                {
                    return false;
                }

                SceneDirectoryData.PerPlayerCountDirectoryEntry variant = session.LevelSettings.SceneDirectoryVarientEntry;
                string activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                if (variant == null
                    || string.IsNullOrEmpty(variant.SceneName)
                    || !string.Equals(activeScene, variant.SceneName, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                LevelIdentity level = LevelIdentity.Read(session);
                if (level == null || string.IsNullOrEmpty(level.Uid))
                {
                    return false;
                }

                int count = ClientUserSystem.m_Users == null ? 1 : ClientUserSystem.m_Users.Count;
                if (!string.Equals(_selectedLevelKey, level.Uid, StringComparison.Ordinal)
                    || _players != Mathf.Clamp(count, 1, 4))
                {
                    _nearbyMode = 0;
                }
                _selectedLevelKey = level.Uid;
                _selectedLevelName = level.DisplayName;
                _players = Mathf.Clamp(count, 1, 4);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void DrawPanel()
        {
            float width = Mathf.Min(900f, Screen.width - 24f);
            const float height = 452f;
            float left = Mathf.Max(8f, Screen.width - width - 12f);
            Rect panel = new Rect(left, 52f, width, height);
            GUI.DrawTexture(panel, _panelBackground, ScaleMode.StretchToFill, true);
            GUIStyle titleBoxStyle = new GUIStyle(GUI.skin.box);
            titleBoxStyle.normal.textColor = new Color(1f, 0.82f, 0.28f, 1f);
            GUI.Box(
                panel,
                OverrankText.Get(
                    "Overrank - Rankings are just for fun",
                    "Overrank - 排名仅供娱乐"),
                titleBoxStyle);

            if (GUI.Button(new Rect(panel.x + panel.width - 34f, panel.y + 5f, 24f, 22f), "X"))
            {
                _showPanel = false;
                return;
            }
            if (GUI.Button(
                new Rect(panel.x + 14f, panel.y + 32f, 112f, 26f),
                OverrankText.Get("Leaderboard", "排行榜")))
            {
                OpenLeaderboard();
            }
            if (GUI.Button(
                new Rect(panel.x + 132f, panel.y + 32f, 112f, 26f),
                OverrankText.Get("Played levels", "已玩关卡")))
            {
                _showLevels = true;
                _roomPage = 0;
                RefreshLevels();
            }
            if (_separatorTexture != null)
            {
                GUI.DrawTexture(
                    new Rect(panel.x + 257f, panel.y + 35f, 1f, 20f),
                    _separatorTexture,
                    ScaleMode.StretchToFill,
                    true);
            }
            if (GUI.Button(
                new Rect(panel.x + 270f, panel.y + 32f, 112f, 26f),
                OverrankText.Get("Current room", "当前房间")))
            {
                _showLevels = false;
                _roomPage = 1;
                _roomMessageUnread = false;
                _nextRoomRefresh = 0f;
            }
            if (GUI.Button(
                new Rect(panel.x + 388f, panel.y + 32f, 112f, 26f),
                OverrankText.Get("Room lobby", "房间大厅")))
            {
                _showLevels = false;
                _roomPage = 2;
                RefreshRooms();
            }
            bool tabsEnabled = GUI.enabled;
            GUI.enabled = tabsEnabled && string.IsNullOrEmpty(_currentRoomId);
            if (GUI.Button(
                new Rect(panel.x + 506f, panel.y + 32f, 112f, 26f),
                OverrankText.Get("Create room", "创建房间")))
            {
                _showLevels = false;
                _roomPage = 3;
                if (string.IsNullOrEmpty(_createRoomTitle))
                {
                    _createRoomTitle = _playerName;
                }
            }
            GUI.enabled = tabsEnabled;
            if (_roomPage == 1)
            {
                DrawCurrentRoom(panel);
            }
            else if (_roomPage == 2)
            {
                DrawRoomLobby(panel);
            }
            else if (_roomPage == 3)
            {
                DrawCreateRoom(panel);
            }
            else if (_showLevels)
            {
                DrawLevels(panel);
            }
            else
            {
                DrawLeaderboard(panel);
            }

            string networkStatus = _roomPage != 0 && !string.IsNullOrEmpty(_roomError)
                ? _roomError
                : (_requestRunning || _roomRequestRunning || _creatingGameLobby)
                ? OverrankText.Get("Loading...", "加载中……")
                : (!string.IsNullOrEmpty(_requestError)
                    ? _requestError
                    : (!string.IsNullOrEmpty(_responseSummary)
                        ? _responseSummary
                        : (_client == null ? string.Empty : _client.Status)));
            float statusTop = panel.y + panel.height - 25f;
            float presenceWidth = string.IsNullOrEmpty(_presenceSummary) ? 0f : 270f;
            GUI.Label(
                new Rect(panel.x + 14f, statusTop, panel.width - 28f - presenceWidth, 20f),
                networkStatus ?? string.Empty);
            if (presenceWidth > 0f)
            {
                float textWidth = Mathf.Min(
                    presenceWidth,
                    GUI.skin.label.CalcSize(new GUIContent(_presenceSummary)).x + 4f);
                GUI.Label(
                    new Rect(panel.x + panel.width - textWidth - 14f, statusTop, textWidth, 20f),
                    _presenceSummary);
            }
        }

        private void DrawLeaderboard(Rect panel)
        {
            string title = string.IsNullOrEmpty(_selectedLevelName)
                ? OverrankText.Get("No played level yet", "尚无玩过的关卡")
                : _selectedLevelName;
            GUI.Label(new Rect(panel.x + 14f, panel.y + 68f, panel.width - 150f, 24f), title);
            bool leaderboardControlsEnabled = GUI.enabled;
            GUI.enabled = leaderboardControlsEnabled && CanManuallyRefreshLeaderboard();
            if (GUI.Button(
                new Rect(panel.x + panel.width - 94f, panel.y + 66f, 80f, 24f),
                OverrankText.Get("Refresh", "刷新")))
            {
                if (BeginManualLeaderboardRefresh())
                {
                    ClearLeaderboardCache();
                    RefreshBoard();
                }
            }
            GUI.enabled = leaderboardControlsEnabled;

            GUI.Label(
                new Rect(panel.x + 14f, panel.y + 98f, 52f, 22f),
                OverrankText.Get("Rank by", "排序"));
            if (GUI.Toggle(
                new Rect(panel.x + 70f, panel.y + 98f, 70f, 22f),
                _metric == "score",
                OverrankText.Get("Score", "分数"))
                && _metric != "score"
                && BeginManualLeaderboardRefresh())
            {
                _metric = "score";
                _nearbyMode = 0;
                RefreshBoard();
            }
            if (GUI.Toggle(
                new Rect(panel.x + 144f, panel.y + 98f, 78f, 22f),
                _metric == "dishes",
                OverrankText.Get("Dishes", "菜数"))
                && _metric != "dishes"
                && BeginManualLeaderboardRefresh())
            {
                _metric = "dishes";
                _nearbyMode = 0;
                RefreshBoard();
            }

            GUI.Label(
                new Rect(panel.x + 250f, panel.y + 98f, 58f, 22f),
                OverrankText.Get("Players", "人数"));
            for (int count = 1; count <= 4; count++)
            {
                if (GUI.Toggle(
                    new Rect(panel.x + 308f + (count - 1) * 48f, panel.y + 98f, 45f, 22f),
                    _players == count,
                    count.ToString())
                    && _players != count
                    && BeginManualLeaderboardRefresh())
                {
                    _players = count;
                    _lastPlayerCount.Value = count;
                    _nearbyMode = 0;
                    RefreshBoard();
                }
            }
            DrawNearbyModeButton(
                new Rect(panel.x + panel.width - 204f, panel.y + 98f, 190f, 22f));

            float bodyTop = panel.y + 130f;
            float columnWidth = (panel.width - 42f) * 0.5f;
            int nearbyPercentile = _leaderboard == null
                || _leaderboard.self_rank <= 0
                || _leaderboard.total_players <= 0
                ? 0
                : (_leaderboard.self_percentile > 0
                    ? Mathf.Clamp(_leaderboard.self_percentile, 1, 100)
                    : Mathf.Clamp(
                        Mathf.CeilToInt(_leaderboard.self_rank * 100f / _leaderboard.total_players),
                        1,
                        100));
            string nearbyTitle = OverrankText.Get("Around me", "我的附近");
            if (nearbyPercentile > 0)
            {
                nearbyTitle += OverrankText.IsSimplifiedChinese
                    ? "（前 " + nearbyPercentile + "%）"
                    : " (Top " + nearbyPercentile + "%)";
            }
            GUI.Box(
                new Rect(panel.x + 14f, bodyTop, columnWidth, 278f),
                OverrankText.Get("Top players", "最高排名"));
            GUI.Box(
                new Rect(panel.x + 28f + columnWidth, bodyTop, columnWidth, 278f),
                nearbyTitle);
            LeaderboardEntry[] top = _leaderboard == null || _leaderboard.entries == null
                ? new LeaderboardEntry[0]
                : _leaderboard.entries;
            LeaderboardEntry[] nearby = _leaderboard == null || _leaderboard.nearby == null
                ? new LeaderboardEntry[0]
                : _leaderboard.nearby;
            DrawEntries(
                new Rect(panel.x + 22f, bodyTop + 28f, columnWidth - 16f, 240f),
                top,
                ref _topScroll);
            Rect nearbyArea = new Rect(
                panel.x + 36f + columnWidth,
                bodyTop + 28f,
                columnWidth - 16f,
                240f);
            if (_centerNearbyOnSelf)
            {
                CenterNearbyOnSelectedEntry(nearbyArea, nearby);
            }
            DrawEntries(
                nearbyArea,
                nearby,
                ref _nearbyScroll);
        }

        private LeaderboardEntry FindSelfEntry(bool overwashedUsed)
        {
            if (_leaderboard == null || _leaderboard.self_entries == null)
            {
                return null;
            }
            for (int index = 0; index < _leaderboard.self_entries.Length; index++)
            {
                LeaderboardEntry entry = _leaderboard.self_entries[index];
                if (entry != null && entry.overwashed_used == overwashedUsed)
                {
                    return entry;
                }
            }
            return null;
        }

        private void DrawNearbyModeButton(Rect area)
        {
            LeaderboardEntry unassistedSelf = FindSelfEntry(false);
            LeaderboardEntry assistedSelf = FindSelfEntry(true);
            LeaderboardEntry selectedSelf = null;
            string label;
            if (_nearbyMode == 1)
            {
                selectedSelf = unassistedSelf;
                label = OverrankText.Get("No bot", "无机器人");
            }
            else if (_nearbyMode == 2)
            {
                selectedSelf = assistedSelf;
                label = OverrankText.Get("Bot", "机器人");
            }
            else
            {
                if (_leaderboard != null
                    && _leaderboard.self_entries != null
                    && _leaderboard.self_entries.Length > 0)
                {
                    selectedSelf = _leaderboard.self_entries[0];
                }
                label = OverrankText.Get("Overall", "总表");
            }
            int selectedRank = selectedSelf == null ? 0 : selectedSelf.rank;
            if (selectedRank <= 0
                && _leaderboard != null
                && _leaderboard.self_rank > 0
                && (_nearbyMode == 0
                    || (_nearbyMode == 1 && !_nearbySelectedOverwashed)
                    || (_nearbyMode == 2 && _nearbySelectedOverwashed)))
            {
                selectedRank = _leaderboard.self_rank;
            }
            bool canSwitch = CanManuallyRefreshLeaderboard() && HasAnyPersonalRank();
            bool previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && canSwitch;
            if (GUI.Button(area, label + " " + (selectedRank <= 0 ? "--" : "#" + selectedRank)))
            {
                if (BeginManualLeaderboardRefresh())
                {
                    SwitchNearbyMode();
                }
            }
            GUI.enabled = previousEnabled;
        }

        private bool HasAnyPersonalRank()
        {
            return (_leaderboard != null && _leaderboard.self_rank > 0)
                || (_overallLeaderboard != null && _overallLeaderboard.self_rank > 0)
                || (_unassistedLeaderboard != null && _unassistedLeaderboard.self_rank > 0)
                || (_assistedLeaderboard != null && _assistedLeaderboard.self_rank > 0);
        }

        private void CenterNearbyOnSelectedEntry(Rect area, LeaderboardEntry[] entries)
        {
            _centerNearbyOnSelf = false;
            if (entries == null || entries.Length == 0)
            {
                return;
            }
            int selectedIndex = -1;
            for (int index = 0; index < entries.Length; index++)
            {
                LeaderboardEntry entry = entries[index];
                if (entry == null || !string.Equals(entry.player_id, _playerId, StringComparison.Ordinal))
                {
                    continue;
                }
                if (entry.overwashed_used == _nearbySelectedOverwashed)
                {
                    selectedIndex = index;
                    break;
                }
                if (selectedIndex < 0)
                {
                    selectedIndex = index;
                }
            }
            if (selectedIndex < 0)
            {
                return;
            }
            const float rowHeight = 22f;
            float contentHeight = Mathf.Max(area.height, entries.Length * rowHeight);
            float centered = selectedIndex * rowHeight + rowHeight * 0.5f - area.height * 0.5f;
            _nearbyScroll = new Vector2(0f, Mathf.Clamp(centered, 0f, contentHeight - area.height));
        }

        private void DrawEntries(
            Rect area,
            LeaderboardEntry[] entries,
            ref Vector2 scrollPosition)
        {
            if (entries.Length == 0)
            {
                GUI.Label(
                    new Rect(area.x, area.y, area.width, 22f),
                    OverrankText.Get("No scores for this selection", "当前条件下暂无成绩"));
                return;
            }
            int count = Mathf.Min(entries.Length, 100);
            const float rowHeight = 22f;
            Rect content = new Rect(0f, 0f, area.width - 18f, Mathf.Max(area.height, count * rowHeight));
            scrollPosition = GUI.BeginScrollView(area, scrollPosition, content, false, true);
            for (int index = 0; index < count; index++)
            {
                LeaderboardEntry entry = entries[index];
                bool self = string.Equals(entry.player_id, _playerId, StringComparison.Ordinal);
                Color previous = GUI.color;
                if (self)
                {
                    GUI.color = new Color(0.55f, 1f, 0.75f, 1f);
                }
                string value = OverrankText.IsSimplifiedChinese
                    ? "分:" + entry.score + " 菜:" + entry.dishes
                    : "S:" + entry.score + " D:" + entry.dishes;
                float rowY = index * rowHeight;
                float rankWidth = 46f;
                float nameWidth = Mathf.Clamp(content.width * 0.38f, 110f, 170f);
                GUI.Label(
                    new Rect(0f, rowY, rankWidth, rowHeight - 1f),
                    "#" + entry.rank);
                GUI.Label(
                    new Rect(rankWidth, rowY, nameWidth, rowHeight - 1f),
                    Truncate(entry.player_name, Mathf.Max(7, Mathf.FloorToInt(nameWidth / 8f))));
                float valueX = rankWidth + nameWidth + 4f;
                if (entry.overwashed_used && _overwashedIcon != null)
                {
                    GUI.DrawTexture(
                        new Rect(valueX, rowY + 4f, 12f, 12f),
                        _overwashedIcon,
                        ScaleMode.ScaleToFit,
                        true);
                    valueX += 15f;
                }
                GUI.Label(
                    new Rect(valueX, rowY, content.width - valueX, rowHeight - 1f),
                    value);
                GUI.color = previous;
            }
            GUI.EndScrollView();
        }

        private void DrawLevels(Rect panel)
        {
            if (GUI.Button(
                new Rect(panel.x + panel.width - 94f, panel.y + 66f, 80f, 24f),
                OverrankText.Get("Refresh", "刷新")))
            {
                RefreshLevels();
            }
            PlayedLevel[] levels = _playedLevels == null || _playedLevels.levels == null
                ? new PlayedLevel[0]
                : _playedLevels.levels;
            const int pageSize = 12;
            int maxPage = levels.Length == 0 ? 0 : (levels.Length - 1) / pageSize;
            _levelsPage = Mathf.Clamp(_levelsPage, 0, maxPage);
            GUI.Label(
                new Rect(panel.x + 14f, panel.y + 70f, panel.width - 120f, 22f),
                OverrankText.IsSimplifiedChinese
                    ? Truncate(_playerName, 28) + " 玩过的关卡"
                    : "Levels played by " + Truncate(_playerName, 28));

            int first = _levelsPage * pageSize;
            int last = Mathf.Min(first + pageSize, levels.Length);
            for (int index = first; index < last; index++)
            {
                PlayedLevel level = levels[index];
                float y = panel.y + 99f + (index - first) * 25f;
                string label = string.IsNullOrEmpty(level.level_label) ? level.level_name : level.level_label;
                label = LevelIdentity.ResolveDisplayName(level.level_key, label);
                string displayLabel = HasDuplicateLabel(levels, index, label)
                    ? label + " [" + ShortLevelKey(level.level_key) + "]"
                    : label;
                if (GUI.Button(new Rect(panel.x + 14f, y, panel.width - 28f, 23f), string.Empty))
                {
                    _selectedLevelKey = level.level_key;
                    _selectedLevelName = displayLabel;
                    _lastLevelKey.Value = _selectedLevelKey;
                    _lastLevelName.Value = _selectedLevelName;
                    _nearbyMode = 0;
                    _showLevels = false;
                    RefreshBoard();
                }
                GUI.Label(new Rect(panel.x + 22f, y + 2f, panel.width - 230f, 20f), Truncate(displayLabel, 42));
                GUI.Label(
                    new Rect(panel.x + panel.width - 205f, y + 2f, 185f, 20f),
                    OverrankText.IsSimplifiedChinese
                        ? "分数: " + level.best_score + "  菜数: " + level.best_dishes
                        : "Score: " + level.best_score + "  Dishes: " + level.best_dishes);
            }
            if (levels.Length == 0 && !_requestRunning)
            {
                GUI.Label(
                    new Rect(panel.x + 14f, panel.y + 105f, panel.width - 28f, 22f),
                OverrankText.Get("No played levels yet", "尚无玩过的关卡"));
            }
            GUI.enabled = _levelsPage > 0;
            if (GUI.Button(
                new Rect(panel.x + 14f, panel.y + 407f, 70f, 24f),
                OverrankText.Get("Previous", "上一页")))
            {
                _levelsPage--;
            }
            GUI.enabled = _levelsPage < maxPage;
            if (GUI.Button(
                new Rect(panel.x + 90f, panel.y + 407f, 70f, 24f),
                OverrankText.Get("Next", "下一页")))
            {
                _levelsPage++;
            }
            GUI.enabled = true;
            GUI.Label(new Rect(panel.x + 170f, panel.y + 410f, 100f, 20f), (_levelsPage + 1) + " / " + (maxPage + 1));
        }

        private void DrawRoomLobby(Rect panel)
        {
            bool enterPressed = IsEnterPressed();
            float availableWidth = panel.width - 42f;
            float roomsWidth = Mathf.Floor(availableWidth * 0.66f);
            float chatWidth = availableWidth - roomsWidth;
            float roomsX = panel.x + 14f;
            float chatX = roomsX + roomsWidth + 14f;
            GUI.Label(
                new Rect(roomsX, panel.y + 68f, 62f, 24f),
                OverrankText.Get("Room ID", "房间号"));
            _directRoomId = DigitsOnly(GUI.TextField(
                new Rect(roomsX + 62f, panel.y + 66f, 72f, 25f),
                _directRoomId ?? string.Empty,
                6));
            bool lobbyControlsEnabled = GUI.enabled;
            GUI.enabled = lobbyControlsEnabled
                && !_roomRequestRunning
                && string.IsNullOrEmpty(_currentRoomId)
                && IsSixDigitRoomId(_directRoomId);
            if (GUI.Button(
                new Rect(roomsX + 140f, panel.y + 66f, 58f, 25f),
                OverrankText.Get("Join", "加入")))
            {
                EnterRoomById();
            }
            GUI.enabled = lobbyControlsEnabled;
            GUI.enabled = lobbyControlsEnabled
                && !_roomRefreshRunning
                && Time.unscaledTime >= _nextManualRoomRefresh;
            if (GUI.Button(
                new Rect(roomsX + roomsWidth - 80f, panel.y + 66f, 80f, 24f),
                OverrankText.Get("Refresh", "刷新")))
            {
                RefreshRooms(true);
            }
            GUI.enabled = lobbyControlsEnabled;
            GUI.Label(
                new Rect(roomsX + 208f, panel.y + 68f, 70f, 24f),
                OverrankText.Get("Password", "密码"));
            _joinRoomPassword = GUI.PasswordField(
                new Rect(
                    roomsX + 272f,
                    panel.y + 66f,
                    Mathf.Max(80f, roomsWidth - 358f),
                    25f),
                _joinRoomPassword ?? string.Empty,
                '*',
                32);

            RoomInfo[] rooms = _roomList == null || _roomList.rooms == null
                ? new RoomInfo[0]
                : _roomList.rooms;
            Rect area = new Rect(roomsX, panel.y + 99f, roomsWidth, 316f);
            if (rooms.Length == 0)
            {
                GUI.Label(
                    new Rect(area.x, area.y, area.width, 24f),
                    OverrankText.Get("No active rooms", "当前没有可用房间"));
            }
            else
            {
                const float rowHeight = 66f;
                Rect content = new Rect(0f, 0f, area.width - 18f, Mathf.Max(area.height, rooms.Length * rowHeight));
                _roomListScroll = GUI.BeginScrollView(area, _roomListScroll, content, false, true);
                for (int index = 0; index < rooms.Length; index++)
                {
                    RoomInfo room = rooms[index];
                    float y = index * rowHeight;
                    GUI.Box(new Rect(0f, y, content.width, 60f), string.Empty);
                    if (room.locked && _lockIcon != null)
                    {
                        GUI.DrawTexture(
                            new Rect(9f, y + 9f, 14f, 16f),
                            _lockIcon,
                            ScaleMode.ScaleToFit,
                            true);
                    }
                    float buttonWidth = 98f;
                    float statusWidth = 122f;
                    float statusX = content.width - buttonWidth - statusWidth - 14f;
                    GUI.Label(
                        new Rect(28f, y + 7f, Mathf.Max(70f, statusX - 32f), 22f),
                        "#" + room.room_id + "  " + Truncate(room.title, 22));
                    int playerLimit = Mathf.Clamp(room.game_player_limit, 1, 4);
                    int playerCount = Mathf.Clamp(room.game_player_count, 0, playerLimit);
                    bool playing = string.Equals(room.status, "playing", StringComparison.Ordinal);
                    bool full = playerCount >= playerLimit;
                    GUI.Label(
                        new Rect(statusX, y + 7f, 37f, 22f),
                        playerCount + "/" + playerLimit);
                    float statusLabelX = statusX + 39f;
                    if (playing)
                    {
                        Color previousColor = GUI.color;
                        GUI.color = new Color(1f, 0.56f, 0.2f, 1f);
                        GUI.DrawTexture(
                            new Rect(statusLabelX, y + 14f, 7f, 7f),
                            Texture2D.whiteTexture,
                            ScaleMode.StretchToFill,
                            true);
                        GUI.color = previousColor;
                        statusLabelX += 11f;
                    }
                    GUI.Label(
                        new Rect(statusLabelX, y + 7f, statusWidth - (statusLabelX - statusX), 22f),
                        RoomStatusLabel(room.status));
                    bool currentRoom = string.Equals(
                        room.room_id,
                        _currentRoomId,
                        StringComparison.Ordinal) || room.is_owner;
                    bool previousEnabled = GUI.enabled;
                    GUI.enabled = previousEnabled
                        && !_roomRequestRunning
                        && string.IsNullOrEmpty(_currentRoomId)
                        && !room.is_owner
                        && !full
                        && !playing;
                    if (GUI.Button(
                        new Rect(content.width - buttonWidth - 6f, y + 5f, buttonWidth, 26f),
                        currentRoom
                            ? OverrankText.Get("Current", "当前房间")
                            : playing
                            ? OverrankText.Get("In game", "游戏中")
                            : full
                            ? OverrankText.Get("Full", "已满")
                            : OverrankText.Get("Enter", "进入房间")))
                    {
                        EnterRoom(room);
                    }
                    GUI.enabled = previousEnabled;
                    bool copyEnabled = GUI.enabled;
                    GUI.enabled = copyEnabled && !string.IsNullOrEmpty(room.room_id);
                    if (GUI.Button(
                        new Rect(content.width - 92f, y + 34f, 86f, 21f),
                        OverrankText.Get("Copy ID", "复制房间号")))
                    {
                        GUIUtility.systemCopyBuffer = room.room_id;
                        _directRoomId = room.room_id;
                    }
                    GUI.enabled = copyEnabled;
                    GUI.Label(
                        new Rect(28f, y + 34f, content.width - 94f, 20f),
                        Truncate(room.description, 45));
                }
                GUI.EndScrollView();
            }

            GUI.Box(
                new Rect(chatX, panel.y + 66f, chatWidth, 349f),
                OverrankText.Get("Lobby chat", "大厅聊天"));
            GUI.Label(
                new Rect(chatX + 9f, panel.y + 91f, chatWidth - 18f, 29f),
                LobbyChatNoticeText(),
                CreateChatNoticeStyle());
            RoomMessage[] messages = _roomList == null || _roomList.messages == null
                ? new RoomMessage[0]
                : _roomList.messages;
            Rect chatArea = new Rect(chatX + 8f, panel.y + 122f, chatWidth - 16f, 248f);
            GUIStyle chatStyle = CreateSelectableChatStyle();
            string transcript = BuildChatTranscript(messages);
            float transcriptHeight = Mathf.Max(
                chatArea.height,
                chatStyle.CalcHeight(
                    new GUIContent(string.IsNullOrEmpty(transcript) ? " " : transcript),
                    chatArea.width - 18f));
            Rect chatContent = new Rect(
                0f,
                0f,
                chatArea.width - 18f,
                transcriptHeight);
            _lobbyChatScroll = GUI.BeginScrollView(
                chatArea,
                _lobbyChatScroll,
                chatContent,
                false,
                true);
            GUI.TextArea(
                new Rect(0f, 0f, chatContent.width, transcriptHeight),
                transcript,
                chatStyle);
            GUI.EndScrollView();
            bool inputEnabled = GUI.enabled;
            GUI.enabled = inputEnabled && _hasEngagedPlayer && !_roomRequestRunning;
            GUI.SetNextControlName("OverrankLobbyChatInput");
            _lobbyChatInput = GUI.TextField(
                new Rect(chatX + 8f, panel.y + 378f, chatWidth - 83f, 27f),
                _lobbyChatInput ?? string.Empty,
                240);
            if (_hasEngagedPlayer)
            {
                RestoreChatInputFocus("OverrankLobbyChatInput", ref _focusLobbyChatInput);
            }
            GUI.enabled = inputEnabled;
            bool canSend = _hasEngagedPlayer
                && !_roomRequestRunning
                && !string.IsNullOrEmpty((_lobbyChatInput ?? string.Empty).Trim());
            bool enabled = GUI.enabled;
            GUI.enabled = enabled && canSend;
            bool submitLobbyMessage = false;
            if (GUI.Button(
                new Rect(chatX + chatWidth - 69f, panel.y + 378f, 61f, 27f),
                OverrankText.Get("Send", "发送")))
            {
                submitLobbyMessage = true;
            }
            GUI.enabled = enabled;
            if (canSend && enterPressed)
            {
                submitLobbyMessage = true;
                if (Event.current.type != EventType.Used)
                {
                    Event.current.Use();
                }
            }
            if (submitLobbyMessage)
            {
                SendLobbyMessage();
            }
        }

        private void DrawCurrentRoom(Rect panel)
        {
            bool enterPressed = IsEnterPressed();
            _roomMessageUnread = false;
            if (_currentRoom == null || string.IsNullOrEmpty(_currentRoomId))
            {
                GUI.Label(
                    new Rect(panel.x + 14f, panel.y + 76f, panel.width - 28f, 24f),
                    OverrankText.Get(
                        "You are not in an Overrank room. Enter one from the room lobby or create one.",
                        "你尚未进入 Overrank 房间，请从房间大厅进入或创建房间。"));
                return;
            }

            GUI.Label(
                new Rect(panel.x + 14f, panel.y + 68f, panel.width - 310f, 24f),
                "#" + _currentRoomId + "  " + Truncate(_currentRoom.title, 42));
            GUI.Label(
                new Rect(panel.x + panel.width - 286f, panel.y + 68f, 160f, 24f),
                Mathf.Clamp(_currentRoom.game_player_count, 0, 4)
                    + "/" + Mathf.Clamp(_currentRoom.game_player_limit, 1, 4)
                    + "  " + RoomStatusLabel(_currentRoom.status));
            if (GUI.Button(
                new Rect(panel.x + panel.width - 116f, panel.y + 66f, 102f, 25f),
                OverrankText.Get("Leave room", "离开房间")))
            {
                LeaveCurrentRoom();
                return;
            }

            float bodyTop = panel.y + 99f;
            float memberWidth = 238f;
            GUI.Box(
                new Rect(panel.x + 14f, bodyTop, memberWidth, 316f),
                OverrankText.Get("Room members", "房间成员"));
            RoomMember[] members = _currentRoom.members ?? new RoomMember[0];
            Rect memberArea = new Rect(panel.x + 22f, bodyTop + 28f, memberWidth - 16f, 278f);
            Rect memberContent = new Rect(
                0f,
                0f,
                memberArea.width - 18f,
                Mathf.Max(memberArea.height, members.Length * 25f));
            _roomMemberScroll = GUI.BeginScrollView(
                memberArea,
                _roomMemberScroll,
                memberContent,
                false,
                true);
            for (int index = 0; index < members.Length; index++)
            {
                RoomMember member = members[index];
                string suffix = member.is_host
                    ? OverrankText.Get(" (Host)", "（房主）")
                    : string.Empty;
                GUI.Label(
                    new Rect(
                        0f,
                        index * 25f,
                        memberContent.width - (_isRoomHost && !member.is_host ? 58f : 0f),
                        23f),
                    Truncate(member.player_name, 22) + suffix);
                if (_isRoomHost && !member.is_host)
                {
                    bool kickEnabled = GUI.enabled;
                    GUI.enabled = kickEnabled
                        && !_roomRequestRunning
                        && !string.IsNullOrEmpty(member.client_id);
                    if (GUI.Button(
                        new Rect(memberContent.width - 54f, index * 25f, 52f, 22f),
                        OverrankText.Get("Kick", "踢出")))
                    {
                        KickRoomMember(member);
                    }
                    GUI.enabled = kickEnabled;
                }
            }
            GUI.EndScrollView();

            float chatX = panel.x + 266f;
            float chatWidth = panel.width - 280f;
            GUI.Box(
                new Rect(chatX, bodyTop, chatWidth, 316f),
                OverrankText.Get("Chat", "聊天"));
            GUI.Label(
                new Rect(chatX + 9f, bodyTop + 25f, chatWidth - 18f, 22f),
                RoomChatNoticeText(),
                CreateChatNoticeStyle());
            RoomMessage[] messages = _currentRoom.messages ?? new RoomMessage[0];
            Rect chatArea = new Rect(chatX + 8f, bodyTop + 50f, chatWidth - 16f, 217f);
            GUIStyle chatStyle = CreateSelectableChatStyle();
            string transcript = BuildChatTranscript(messages);
            float transcriptHeight = Mathf.Max(
                chatArea.height,
                chatStyle.CalcHeight(
                    new GUIContent(string.IsNullOrEmpty(transcript) ? " " : transcript),
                    chatArea.width - 18f));
            Rect chatContent = new Rect(
                0f,
                0f,
                chatArea.width - 18f,
                transcriptHeight);
            _roomChatScroll = GUI.BeginScrollView(chatArea, _roomChatScroll, chatContent, false, true);
            GUI.TextArea(
                new Rect(0f, 0f, chatContent.width, transcriptHeight),
                transcript,
                chatStyle);
            GUI.EndScrollView();
            bool inputEnabled = GUI.enabled;
            GUI.enabled = inputEnabled && _hasEngagedPlayer && !_roomRequestRunning;
            GUI.SetNextControlName("OverrankRoomChatInput");
            _chatInput = GUI.TextField(
                new Rect(chatX + 8f, bodyTop + 276f, chatWidth - 106f, 27f),
                _chatInput ?? string.Empty,
                240);
            if (_hasEngagedPlayer)
            {
                RestoreChatInputFocus("OverrankRoomChatInput", ref _focusRoomChatInput);
            }
            GUI.enabled = inputEnabled;
            bool canSend = _hasEngagedPlayer
                && !_roomRequestRunning
                && !string.IsNullOrEmpty((_chatInput ?? string.Empty).Trim());
            bool enabled = GUI.enabled;
            GUI.enabled = enabled && canSend;
            bool submitRoomMessage = false;
            if (GUI.Button(
                new Rect(chatX + chatWidth - 90f, bodyTop + 276f, 82f, 27f),
                OverrankText.Get("Send", "发送")))
            {
                submitRoomMessage = true;
            }
            GUI.enabled = enabled;
            if (canSend && enterPressed)
            {
                submitRoomMessage = true;
                if (Event.current.type != EventType.Used)
                {
                    Event.current.Use();
                }
            }
            if (submitRoomMessage)
            {
                SendRoomMessage();
            }
        }

        private void DrawCreateRoom(Rect panel)
        {
            ulong lobbyId;
            string ignoredLobbyKey;
            int ignoredMemberCount;
            bool hasLobby = SessionContext.TryReadLobby(out lobbyId, out ignoredLobbyKey, out ignoredMemberCount);
            bool canCreateGameLobby = hasLobby || FindFrontendPlayerLobby() != null;
            GUI.Label(
                new Rect(panel.x + 14f, panel.y + 72f, 110f, 24f),
                OverrankText.Get("Title", "标题"));
            _createRoomTitle = GUI.TextField(
                new Rect(panel.x + 126f, panel.y + 69f, panel.width - 140f, 27f),
                _createRoomTitle ?? string.Empty,
                48);
            GUI.Label(
                new Rect(panel.x + 14f, panel.y + 109f, 110f, 24f),
                OverrankText.Get("Description", "描述"));
            _createRoomDescription = GUI.TextArea(
                new Rect(panel.x + 126f, panel.y + 106f, panel.width - 140f, 94f),
                _createRoomDescription ?? string.Empty,
                160);
            _createRoomLocked = GUI.Toggle(
                new Rect(panel.x + 14f, panel.y + 214f, 110f, 24f),
                _createRoomLocked,
                OverrankText.Get("Password", "启用密码"));
            bool passwordEnabled = GUI.enabled;
            GUI.enabled = passwordEnabled && _createRoomLocked;
            _createRoomPassword = GUI.PasswordField(
                new Rect(panel.x + 126f, panel.y + 211f, 260f, 27f),
                _createRoomPassword ?? string.Empty,
                '*',
                32);
            GUI.enabled = passwordEnabled;
            GUI.Label(
                new Rect(panel.x + 126f, panel.y + 247f, panel.width - 140f, 42f),
                !string.IsNullOrEmpty(_currentRoomId)
                    ? OverrankText.Get(
                        "Leave the current room before creating another.",
                        "请先离开当前房间，再创建新房间。")
                    : hasLobby
                    ? OverrankText.Get(
                        "This room publishes your current Steam game lobby.",
                        "此房间会发布你当前的 Steam 游戏大厅。")
                    : canCreateGameLobby
                    ? OverrankText.Get(
                        "An online Steam game lobby will be created automatically.",
                        "创建房间时会自动创建 Steam 在线战局。")
                    : OverrankText.Get(
                        "Open the game's player lobby before creating a room.",
                        "请先进入游戏的玩家大厅，再创建房间。"));

            bool previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled
                && canCreateGameLobby
                && string.IsNullOrEmpty(_currentRoomId)
                && !_roomRequestRunning
                && !_creatingGameLobby
                && !string.IsNullOrEmpty((_createRoomTitle ?? string.Empty).Trim())
                && (!_createRoomLocked || !string.IsNullOrEmpty((_createRoomPassword ?? string.Empty).Trim()));
            if (GUI.Button(
                new Rect(panel.x + 126f, panel.y + 300f, 150f, 30f),
                OverrankText.Get("Create room", "创建房间")))
            {
                SaveRoomPreferences();
                BeginCreateRoom(lobbyId);
            }
            GUI.enabled = previousEnabled;
        }

        private void BeginCreateRoom(ulong lobbyId)
        {
            if (lobbyId != 0UL)
            {
                CreateRoom(lobbyId);
                return;
            }
            FrontendPlayerLobby playerLobby = FindFrontendPlayerLobby();
            if (playerLobby == null)
            {
                _roomError = OverrankText.Get(
                    "Open the game's player lobby before creating a room.",
                    "请先进入游戏的玩家大厅，再创建房间。");
                return;
            }
            try
            {
                _roomError = null;
                _creatingGameLobby = true;
                _gameLobbyCreateDeadline = Time.unscaledTime + 45f;
                _nextGameLobbyCheck = 0f;
                playerLobby.OnInvite();
            }
            catch (Exception exception)
            {
                _creatingGameLobby = false;
                _roomError = OverrankText.Get(
                    "Could not create the Steam game lobby: ",
                    "无法创建 Steam 在线战局：") + exception.Message;
            }
        }

        private void SaveRoomPreferences()
        {
            if (_savedRoomTitle == null || _savedRoomDescription == null || _savedRoomLocked == null)
            {
                return;
            }
            _savedRoomTitle.Value = (_createRoomTitle ?? string.Empty).Trim();
            _savedRoomDescription.Value = (_createRoomDescription ?? string.Empty).Trim();
            _savedRoomLocked.Value = _createRoomLocked;
            Config.Save();
        }

        private void UpdatePendingGameLobbyCreation()
        {
            if (!_creatingGameLobby || Time.unscaledTime < _nextGameLobbyCheck)
            {
                return;
            }
            _nextGameLobbyCheck = Time.unscaledTime + 0.25f;
            ulong lobbyId;
            string ignoredLobbyKey;
            int ignoredMemberCount;
            if (SessionContext.TryReadLobby(out lobbyId, out ignoredLobbyKey, out ignoredMemberCount))
            {
                _creatingGameLobby = false;
                CreateRoom(lobbyId);
                return;
            }
            if (Time.unscaledTime >= _gameLobbyCreateDeadline)
            {
                _creatingGameLobby = false;
                _roomError = OverrankText.Get(
                    "The Steam game lobby was not created. Try the game's Invite button once.",
                    "Steam 在线战局创建失败，请尝试使用一次游戏内的邀请按钮。");
            }
        }

        private static FrontendPlayerLobby FindFrontendPlayerLobby()
        {
            try
            {
                return UnityEngine.Object.FindObjectOfType(typeof(FrontendPlayerLobby)) as FrontendPlayerLobby;
            }
            catch
            {
                return null;
            }
        }

        private void UpdateRoomState()
        {
            if (_client == null || Time.unscaledTime < _nextRoomRefresh)
            {
                return;
            }
            if (!string.IsNullOrEmpty(_currentRoomId))
            {
                if (_roomRequestRunning || _roomHeartbeatRunning)
                {
                    return;
                }
                SendRoomHeartbeat();
                _nextRoomRefresh = Time.unscaledTime + (_showPanel && _roomPage == 1 ? 3f : 10f);
            }
            else if (_showPanel && _roomPage == 2)
            {
                if (_roomRequestRunning || _roomRefreshRunning)
                {
                    return;
                }
                RefreshRooms();
                _nextRoomRefresh = Time.unscaledTime + 4f;
            }
        }

        private void UpdateRoomStatusTransition()
        {
            if (!_isRoomHost
                || string.IsNullOrEmpty(_currentRoomId)
                || Time.unscaledTime < _nextRoomStatusCheck)
            {
                return;
            }
            _nextRoomStatusCheck = Time.unscaledTime + 1f;
            string status = CurrentRoomStatus();
            if (string.Equals(status, _lastObservedRoomStatus, StringComparison.Ordinal))
            {
                return;
            }
            _lastObservedRoomStatus = status;
            _nextRoomRefresh = 0f;
        }

        private void UpdateGameLobbyMembership()
        {
            if (string.IsNullOrEmpty(_currentRoomId)
                || _roomRequestRunning
                || Time.unscaledTime < _nextRoomMembershipCheck)
            {
                return;
            }
            _nextRoomMembershipCheck = Time.unscaledTime + 1f;
            ulong expectedLobbyId = 0UL;
            if (_currentRoom != null)
            {
                ulong.TryParse(_currentRoom.lobby_id, out expectedLobbyId);
            }
            ulong observedLobbyId;
            string ignoredLobbyKey;
            int ignoredMemberCount;
            bool hasLobby = SessionContext.TryReadLobby(
                out observedLobbyId,
                out ignoredLobbyKey,
                out ignoredMemberCount);
            if (hasLobby)
            {
                _roomLobbyMissingSince = -1f;
                if (expectedLobbyId != 0UL && observedLobbyId != expectedLobbyId)
                {
                    _roomError = OverrankText.Get(
                        "Left the Overrank room after switching Steam game lobbies.",
                        "Steam 游戏战局已切换，已离开 Overrank 房间");
                    LeaveCurrentRoom(false);
                }
                return;
            }
            if (_roomLobbyMissingSince < 0f)
            {
                _roomLobbyMissingSince = Time.unscaledTime;
                return;
            }
            if (Time.unscaledTime - _roomLobbyMissingSince >= 5f)
            {
                _roomError = OverrankText.Get(
                    "Left the Overrank room after disconnecting from the Steam game lobby.",
                    "与 Steam 游戏战局断开，已离开 Overrank 房间");
                LeaveCurrentRoom(false);
            }
        }

        private void RefreshRooms()
        {
            RefreshRooms(false);
        }

        private void RefreshRooms(bool manual)
        {
            if (_client == null || _roomRequestRunning || _roomRefreshRunning)
            {
                return;
            }
            if (Time.unscaledTime < _nextAllowedRoomListRequest)
            {
                return;
            }
            if (manual && Time.unscaledTime < _nextManualRoomRefresh)
            {
                return;
            }
            _nextAllowedRoomListRequest = Time.unscaledTime + 1f;
            if (manual)
            {
                _nextManualRoomRefresh = Time.unscaledTime + 1f;
            }
            _roomRefreshRunning = true;
            _roomError = null;
            _client.RequestRooms(
                _clientId,
                _lastLobbyMessageId,
                _roomListRevision,
                delegate(RoomListResponse response, string error)
                {
                    _roomRefreshRunning = false;
                    if (!string.IsNullOrEmpty(error) || response == null)
                    {
                        _roomError = RoomErrorLabel(error);
                        RefreshLobbyMessages();
                        return;
                    }
                    ApplyRoomList(response);
                    _nextRoomRefresh = Time.unscaledTime + 4f;
                });
        }

        private void RefreshLobbyMessages()
        {
            if (_client == null || _roomRequestRunning || _roomRefreshRunning)
            {
                return;
            }
            _roomRefreshRunning = true;
            _client.RequestLobbyMessages(_lastLobbyMessageId, delegate(LobbyChatResponse response, string error)
            {
                _roomRefreshRunning = false;
                if (!string.IsNullOrEmpty(error) || response == null)
                {
                    _roomError = LobbyChatErrorLabel(error);
                    return;
                }
                ApplyLobbyChatResponse(response);
                _roomError = null;
            });
        }

        private void CreateRoom(ulong lobbyId)
        {
            if (_client == null
                || _roomRequestRunning
                || lobbyId == 0UL
                || !string.IsNullOrEmpty(_currentRoomId))
            {
                return;
            }
            _roomRequestRunning = true;
            _roomError = null;
            _client.CreateRoom(
                new RoomCreateRequest
                {
                    client_id = _clientId,
                    player_id = _playerId,
                    player_name = _playerName,
                    title = (_createRoomTitle ?? string.Empty).Trim(),
                    description = (_createRoomDescription ?? string.Empty).Trim(),
                    password = _createRoomLocked ? (_createRoomPassword ?? string.Empty) : string.Empty,
                    lobby_id = lobbyId.ToString(),
                    game_player_count = CurrentGamePlayerCount(),
                    game_player_limit = 4,
                    status = CurrentRoomStatus()
                },
                delegate(RoomInfo room, string error)
                {
                    _roomRequestRunning = false;
                    if (!string.IsNullOrEmpty(error) || room == null)
                    {
                        if (!string.IsNullOrEmpty(error))
                        {
                            _log.LogWarning(
                                "Overrank room creation failed: " + error
                                + " [lobbyId=" + lobbyId
                                + ", playerNameLength=" + ((_playerName ?? string.Empty).Length)
                                + ", titleLength=" + ((_createRoomTitle ?? string.Empty).Trim().Length)
                                + ", descriptionLength=" + ((_createRoomDescription ?? string.Empty).Trim().Length)
                                + ", playerCount=" + CurrentGamePlayerCount()
                                + ", status=" + CurrentRoomStatus() + "]");
                        }
                        _roomError = RoomErrorLabel(error);
                        return;
                    }
                    SetCurrentRoom(room, true, room.host_token);
                    _roomPage = 1;
                    _nextRoomRefresh = Time.unscaledTime + 2.5f;
                });
        }

        private void EnterRoom(RoomInfo listedRoom)
        {
            if (_client == null
                || _roomRequestRunning
                || listedRoom == null
                || listedRoom.is_owner
                || !string.IsNullOrEmpty(_currentRoomId))
            {
                return;
            }
            _roomRequestRunning = true;
            _roomError = null;
            string roomId = listedRoom.room_id;
            _client.JoinRoom(
                roomId,
                new RoomJoinRequest
                {
                    client_id = _clientId,
                    player_id = _playerId,
                    player_name = _playerName,
                    password = _joinRoomPassword ?? string.Empty
                },
                delegate(RoomInfo room, string error)
                {
                    if (!string.IsNullOrEmpty(error) || room == null)
                    {
                        _roomRequestRunning = false;
                        _roomError = RoomErrorLabel(error);
                        return;
                    }
                    ulong lobbyId;
                    string joinError = OverrankText.Get(
                        "Invalid Steam lobby ID.",
                        "Steam 大厅 ID 无效。");
                    if (!ulong.TryParse(room.lobby_id, out lobbyId)
                        || !SessionContext.RequestJoinLobby(lobbyId, out joinError))
                    {
                        _roomRequestRunning = false;
                        _roomError = OverrankText.Get(
                            "Could not join the Steam game: ",
                            "无法加入 Steam 游戏：") + joinError;
                        _client.LeaveRoom(
                            room.room_id,
                            new RoomLeaveRequest { client_id = _clientId, host_token = string.Empty },
                            null);
                        return;
                    }
                    _pendingJoinedRoom = room;
                    _pendingSteamLobbyId = lobbyId;
                    _steamLobbyJoinDeadline = Time.unscaledTime + 60f;
                    _nextSteamLobbyJoinCheck = 0f;
                    _nextPendingJoinHeartbeat = 0f;
                });
        }

        private void EnterRoomById()
        {
            string roomId = (_directRoomId ?? string.Empty).Trim();
            if (!IsSixDigitRoomId(roomId))
            {
                _roomError = OverrankText.Get(
                    "Enter a six-digit room ID.",
                    "请输入六位房间号");
                return;
            }
            if (string.Equals(roomId, _currentRoomId, StringComparison.Ordinal))
            {
                _showLevels = false;
                _roomPage = 1;
                _roomError = null;
                return;
            }
            RoomInfo listedRoom = FindListedRoom(roomId);
            if (listedRoom != null && listedRoom.is_owner)
            {
                _roomError = OverrankText.Get(
                    "This room is already hosted by you. Open Current room instead.",
                    "这是你创建的房间，请打开“当前房间”");
                return;
            }
            if (!string.IsNullOrEmpty(_currentRoomId))
            {
                _roomError = OverrankText.Get(
                    "Leave your current room before joining another.",
                    "请先离开当前房间，再加入其他房间");
                return;
            }
            EnterRoom(listedRoom ?? new RoomInfo { room_id = roomId });
        }

        private RoomInfo FindListedRoom(string roomId)
        {
            RoomInfo[] rooms = _roomList == null ? null : _roomList.rooms;
            if (rooms == null)
            {
                return null;
            }
            for (int index = 0; index < rooms.Length; index++)
            {
                RoomInfo room = rooms[index];
                if (room != null
                    && string.Equals(room.room_id, roomId, StringComparison.Ordinal))
                {
                    return room;
                }
            }
            return null;
        }

        private void UpdatePendingSteamLobbyJoin()
        {
            if (_pendingJoinedRoom == null || Time.unscaledTime < _nextSteamLobbyJoinCheck)
            {
                return;
            }
            _nextSteamLobbyJoinCheck = Time.unscaledTime + 0.25f;
            ulong currentLobbyId;
            string ignoredLobbyKey;
            int ignoredMemberCount;
            if (SessionContext.TryReadLobby(out currentLobbyId, out ignoredLobbyKey, out ignoredMemberCount)
                && currentLobbyId == _pendingSteamLobbyId)
            {
                RoomInfo joinedRoom = _pendingJoinedRoom;
                ClearPendingSteamLobbyJoin();
                _roomRequestRunning = false;
                SetCurrentRoom(joinedRoom, false, string.Empty);
                _roomPage = 1;
                _joinRoomPassword = string.Empty;
                _nextRoomRefresh = Time.unscaledTime + 2.5f;
                _roomError = null;
                return;
            }
            if (Time.unscaledTime < _steamLobbyJoinDeadline)
            {
                SendPendingJoinHeartbeat();
                return;
            }

            string roomId = _pendingJoinedRoom.room_id;
            ClearPendingSteamLobbyJoin();
            _roomError = OverrankText.Get(
                "Could not enter the game lobby. Please try again.",
                "未能进入游戏战局，请重试");
            _client.LeaveRoom(
                roomId,
                new RoomLeaveRequest { client_id = _clientId, host_token = string.Empty },
                delegate(RoomLeaveResponse response, string error)
                {
                    _roomRequestRunning = false;
                });
        }

        private void SendPendingJoinHeartbeat()
        {
            if (_client == null
                || _pendingJoinedRoom == null
                || _roomHeartbeatRunning
                || Time.unscaledTime < _nextPendingJoinHeartbeat)
            {
                return;
            }
            _nextPendingJoinHeartbeat = Time.unscaledTime + 20f;
            string roomId = _pendingJoinedRoom.room_id;
            _roomHeartbeatRunning = true;
            _client.SendRoomHeartbeat(
                roomId,
                new RoomHeartbeatRequest
                {
                    client_id = _clientId,
                    player_id = _playerId,
                    player_name = _playerName,
                    host_token = string.Empty,
                    lobby_id = string.Empty,
                    game_player_count = CurrentGamePlayerCount(),
                    game_player_limit = 4,
                    status = CurrentRoomStatus(),
                    last_message_id = NewestMessageId(_pendingJoinedRoom.messages)
                },
                delegate(RoomInfo room, string error)
                {
                    _roomHeartbeatRunning = false;
                    if (_pendingJoinedRoom == null
                        || !string.Equals(roomId, _pendingJoinedRoom.room_id, StringComparison.Ordinal))
                    {
                        return;
                    }
                    if (!string.IsNullOrEmpty(error) || room == null)
                    {
                        if (!string.IsNullOrEmpty(error)
                            && (error.IndexOf("HTTP 404", StringComparison.OrdinalIgnoreCase) >= 0
                                || error.IndexOf("HTTP 409", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            ClearPendingSteamLobbyJoin();
                            _roomRequestRunning = false;
                            _roomError = RoomErrorLabel(error);
                        }
                        return;
                    }
                    room.messages = MergeMessages(_pendingJoinedRoom.messages, room.messages);
                    _pendingJoinedRoom = room;
                });
        }

        private void ClearPendingSteamLobbyJoin()
        {
            _pendingJoinedRoom = null;
            _pendingSteamLobbyId = 0UL;
            _steamLobbyJoinDeadline = 0f;
            _nextSteamLobbyJoinCheck = 0f;
            _nextPendingJoinHeartbeat = 0f;
            _roomHeartbeatRunning = false;
        }

        private void SendRoomHeartbeat()
        {
            if (_client == null
                || _roomRequestRunning
                || _roomHeartbeatRunning
                || string.IsNullOrEmpty(_currentRoomId))
            {
                return;
            }
            ulong expectedLobbyId = 0UL;
            if (_currentRoom != null)
            {
                ulong.TryParse(_currentRoom.lobby_id, out expectedLobbyId);
            }
            ulong observedLobbyId;
            string ignoredLobbyKey;
            int ignoredMemberCount;
            bool hasLobby = SessionContext.TryReadLobby(
                out observedLobbyId,
                out ignoredLobbyKey,
                out ignoredMemberCount);
            if (hasLobby && expectedLobbyId != 0UL && observedLobbyId != expectedLobbyId)
            {
                _roomError = OverrankText.Get(
                    "Left the Overrank room after switching Steam game lobbies.",
                    "Steam 游戏战局已切换，已离开 Overrank 房间");
                LeaveCurrentRoom(false);
                return;
            }
            if (hasLobby)
            {
                _roomLobbyMissingSince = -1f;
            }
            else
            {
                if (_roomLobbyMissingSince < 0f)
                {
                    _roomLobbyMissingSince = Time.unscaledTime;
                }
                else if (Time.unscaledTime - _roomLobbyMissingSince >= 5f)
                {
                    _roomError = OverrankText.Get(
                        "Left the Overrank room after disconnecting from the Steam game lobby.",
                        "与 Steam 游戏战局断开，已离开 Overrank 房间");
                    LeaveCurrentRoom(false);
                    return;
                }
            }
            string roomId = _currentRoomId;
            _roomHeartbeatRunning = true;
            _client.SendRoomHeartbeat(
                roomId,
                new RoomHeartbeatRequest
                {
                    client_id = _clientId,
                    player_id = _playerId,
                    player_name = _playerName,
                    host_token = _isRoomHost ? _roomHostToken : string.Empty,
                    lobby_id = !_isRoomHost || !hasLobby
                        ? string.Empty
                        : observedLobbyId.ToString(),
                    game_player_count = CurrentGamePlayerCount(),
                    game_player_limit = 4,
                    status = CurrentRoomStatus(),
                    last_message_id = _lastRoomMessageId
                },
                delegate(RoomInfo room, string error)
                {
                    _roomHeartbeatRunning = false;
                    if (!string.Equals(roomId, _currentRoomId, StringComparison.Ordinal))
                    {
                        return;
                    }
                    if (!string.IsNullOrEmpty(error) || room == null)
                    {
                        _roomError = RoomErrorLabel(error);
                        if (!string.IsNullOrEmpty(error)
                            && (error.IndexOf("HTTP 403", StringComparison.Ordinal) >= 0
                                || error.IndexOf("HTTP 404", StringComparison.Ordinal) >= 0
                                || error.IndexOf("HTTP 409", StringComparison.Ordinal) >= 0))
                        {
                            RequestLeaveGameLobby();
                            ClearCurrentRoom();
                        }
                        return;
                    }
                    ApplyCurrentRoomUpdate(room);
                    _roomError = null;
                });
        }

        private void SendRoomMessage()
        {
            string message = (_chatInput ?? string.Empty).Trim();
            if (_client == null
                || !_hasEngagedPlayer
                || _roomRequestRunning
                || string.IsNullOrEmpty(message))
            {
                return;
            }
            string roomId = _currentRoomId;
            _roomRequestRunning = true;
            _client.SendRoomMessage(
                roomId,
                new RoomMessageRequest
                {
                    client_id = _clientId,
                    player_id = _playerId,
                    player_name = _playerName,
                    text = message
                },
                delegate(RoomInfo room, string error)
                {
                    _roomRequestRunning = false;
                    if (!string.Equals(roomId, _currentRoomId, StringComparison.Ordinal))
                    {
                        return;
                    }
                    if (!string.IsNullOrEmpty(error) || room == null)
                    {
                        _roomError = RoomErrorLabel(error);
                        _focusRoomChatInput = true;
                        return;
                    }
                    _chatInput = string.Empty;
                    _focusRoomChatInput = true;
                    ApplyCurrentRoomUpdate(room);
                    _roomError = null;
                });
        }

        private void SendLobbyMessage()
        {
            string message = (_lobbyChatInput ?? string.Empty).Trim();
            if (_client == null
                || !_hasEngagedPlayer
                || _roomRequestRunning
                || string.IsNullOrEmpty(message))
            {
                return;
            }
            _roomRequestRunning = true;
            _client.SendLobbyMessage(
                new RoomMessageRequest
                {
                    client_id = _clientId,
                    player_id = _playerId,
                    player_name = _playerName,
                    text = message
                },
                delegate(LobbyChatResponse response, string error)
                {
                    _roomRequestRunning = false;
                    if (!string.IsNullOrEmpty(error) || response == null)
                    {
                        _roomError = LobbyChatErrorLabel(error);
                        _focusLobbyChatInput = true;
                        return;
                    }
                    _lobbyChatInput = string.Empty;
                    _focusLobbyChatInput = true;
                    ApplyLobbyChatResponse(response);
                    _roomError = null;
                });
        }

        private void LeaveCurrentRoom()
        {
            LeaveCurrentRoom(true);
        }

        private void LeaveCurrentRoom(bool leaveGameLobby)
        {
            if (_client == null || string.IsNullOrEmpty(_currentRoomId))
            {
                ClearCurrentRoom();
                if (leaveGameLobby)
                {
                    RequestLeaveGameLobby();
                }
                return;
            }
            string roomId = _currentRoomId;
            string hostToken = _isRoomHost ? _roomHostToken : string.Empty;
            _roomRequestRunning = true;
            _client.LeaveRoom(
                roomId,
                new RoomLeaveRequest { client_id = _clientId, host_token = hostToken },
                delegate(RoomLeaveResponse response, string error)
                {
                    _roomRequestRunning = false;
                    if (!string.IsNullOrEmpty(error))
                    {
                        _roomError = RoomErrorLabel(error);
                    }
                    ClearCurrentRoom();
                    _roomPage = 2;
                    RefreshRooms();
                });
            if (leaveGameLobby)
            {
                RequestLeaveGameLobby();
            }
        }

        private void KickRoomMember(RoomMember member)
        {
            if (_client == null
                || !_isRoomHost
                || _roomRequestRunning
                || member == null
                || member.is_host
                || string.IsNullOrEmpty(member.client_id)
                || string.IsNullOrEmpty(_currentRoomId))
            {
                return;
            }
            string roomId = _currentRoomId;
            _roomRequestRunning = true;
            _client.KickRoomMember(
                roomId,
                new RoomKickRequest
                {
                    client_id = _clientId,
                    host_token = _roomHostToken,
                    target_client_id = member.client_id
                },
                delegate(RoomInfo room, string error)
                {
                    _roomRequestRunning = false;
                    if (!string.Equals(roomId, _currentRoomId, StringComparison.Ordinal))
                    {
                        return;
                    }
                    if (!string.IsNullOrEmpty(error) || room == null)
                    {
                        _roomError = RoomErrorLabel(error);
                        return;
                    }
                    ApplyCurrentRoomUpdate(room);
                    _roomError = null;
                });
        }

        private void RequestLeaveGameLobby()
        {
            string error;
            if (!SessionContext.RequestLeaveLobby(out error) && !string.IsNullOrEmpty(error))
            {
                _roomError = OverrankText.Get(
                    "Could not leave the Steam game lobby: ",
                    "无法退出 Steam 游戏战局：") + error;
                _log.LogWarning("Could not leave the Steam game lobby: " + error);
            }
        }

        private void SetCurrentRoom(RoomInfo room, bool isHost, string hostToken)
        {
            _currentRoomId = room.room_id ?? string.Empty;
            _isRoomHost = isHost;
            _roomLobbyMissingSince = -1f;
            _lastObservedRoomStatus = isHost ? CurrentRoomStatus() : string.Empty;
            _nextRoomStatusCheck = 0f;
            _nextRoomMembershipCheck = 0f;
            _roomHostToken = hostToken ?? string.Empty;
            ApplyCurrentRoom(room);
        }

        private void ApplyCurrentRoom(RoomInfo room)
        {
            _currentRoom = room;
            RoomMessage[] messages = room == null || room.messages == null
                ? new RoomMessage[0]
                : room.messages;
            int newest = messages.Length == 0 ? 0 : messages[messages.Length - 1].message_id;
            if (newest != _lastRoomMessageId)
            {
                _lastRoomMessageId = newest;
                _roomChatScroll = new Vector2(0f, 100000f);
            }
        }

        private void ApplyCurrentRoomUpdate(RoomInfo room)
        {
            if (room != null
                && HasUnreadRoomMessage(room.messages)
                && !(_showPanel && _roomPage == 1))
            {
                _roomMessageUnread = true;
            }
            if (room != null && _currentRoom != null)
            {
                room.messages = MergeMessages(_currentRoom.messages, room.messages);
            }
            ApplyCurrentRoom(room);
        }

        private bool HasUnreadRoomMessage(RoomMessage[] messages)
        {
            if (messages == null)
            {
                return false;
            }
            for (int index = 0; index < messages.Length; index++)
            {
                RoomMessage message = messages[index];
                if (message != null
                    && message.message_id > _lastRoomMessageId
                    && !string.Equals(message.player_id, _playerId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private void ApplyRoomList(RoomListResponse response)
        {
            RoomInfo[] cachedRooms = _roomList == null
                ? new RoomInfo[0]
                : _roomList.rooms;
            RoomMessage[] cachedMessages = _roomList == null
                ? new RoomMessage[0]
                : _roomList.messages;
            if (response.reset)
            {
                cachedMessages = response.messages ?? new RoomMessage[0];
            }
            else
            {
                cachedMessages = RemoveMessagesBefore(
                    cachedMessages,
                    response.oldest_message_id);
                cachedMessages = MergeMessages(cachedMessages, response.messages);
            }
            _roomList = response;
            _roomList.rooms = response.rooms_changed
                ? (response.rooms ?? new RoomInfo[0])
                : (cachedRooms ?? new RoomInfo[0]);
            _roomList.messages = cachedMessages ?? new RoomMessage[0];
            _roomListRevision = Math.Max(0, response.room_revision);
            if (_roomList.messages.Length == 0 && response.latest_message_id == 0)
            {
                _lastLobbyMessageId = 0;
            }
            UpdateLobbyChatScroll();
        }

        private void ApplyLobbyChatResponse(LobbyChatResponse response)
        {
            if (_roomList == null)
            {
                _roomList = new RoomListResponse();
            }
            if (response.reset)
            {
                _roomList.messages = response.messages ?? new RoomMessage[0];
            }
            else
            {
                _roomList.messages = RemoveMessagesBefore(
                    _roomList.messages,
                    response.oldest_message_id);
                _roomList.messages = MergeMessages(_roomList.messages, response.messages);
            }
            if ((_roomList.messages == null || _roomList.messages.Length == 0)
                && response.latest_message_id == 0)
            {
                _lastLobbyMessageId = 0;
            }
            UpdateLobbyChatScroll();
        }

        private static RoomMessage[] MergeMessages(RoomMessage[] existing, RoomMessage[] incoming)
        {
            if (incoming == null || incoming.Length == 0)
            {
                return existing ?? new RoomMessage[0];
            }
            if (existing == null || existing.Length == 0)
            {
                return incoming;
            }
            SortedDictionary<int, RoomMessage> merged = new SortedDictionary<int, RoomMessage>();
            AddMessages(merged, existing);
            AddMessages(merged, incoming);
            int skip = Math.Max(0, merged.Count - 100);
            List<RoomMessage> result = new List<RoomMessage>(Math.Min(100, merged.Count));
            int index = 0;
            foreach (KeyValuePair<int, RoomMessage> item in merged)
            {
                if (index++ >= skip)
                {
                    result.Add(item.Value);
                }
            }
            return result.ToArray();
        }

        private static RoomMessage[] RemoveMessagesBefore(RoomMessage[] messages, int oldestMessageId)
        {
            if (messages == null || messages.Length == 0 || oldestMessageId <= 0)
            {
                return messages ?? new RoomMessage[0];
            }
            int first = 0;
            while (first < messages.Length
                && (messages[first] == null || messages[first].message_id < oldestMessageId))
            {
                first++;
            }
            if (first == 0)
            {
                return messages;
            }
            int count = messages.Length - first;
            RoomMessage[] retained = new RoomMessage[count];
            if (count > 0)
            {
                Array.Copy(messages, first, retained, 0, count);
            }
            return retained;
        }

        private static void AddMessages(
            SortedDictionary<int, RoomMessage> destination,
            RoomMessage[] messages)
        {
            if (messages == null)
            {
                return;
            }
            for (int index = 0; index < messages.Length; index++)
            {
                RoomMessage message = messages[index];
                if (message != null && message.message_id > 0)
                {
                    destination[message.message_id] = message;
                }
            }
        }

        private static int NewestMessageId(RoomMessage[] messages)
        {
            int newest = 0;
            if (messages == null)
            {
                return newest;
            }
            for (int index = 0; index < messages.Length; index++)
            {
                if (messages[index] != null && messages[index].message_id > newest)
                {
                    newest = messages[index].message_id;
                }
            }
            return newest;
        }

        private void UpdateLobbyChatScroll()
        {
            RoomMessage[] messages = _roomList == null || _roomList.messages == null
                ? new RoomMessage[0]
                : _roomList.messages;
            int newest = messages.Length == 0 ? 0 : messages[messages.Length - 1].message_id;
            if (newest != _lastLobbyMessageId)
            {
                _lastLobbyMessageId = newest;
                _lobbyChatScroll = new Vector2(0f, 100000f);
            }
        }

        private void ClearCurrentRoom()
        {
            _currentRoom = null;
            _currentRoomId = string.Empty;
            _roomHostToken = string.Empty;
            _isRoomHost = false;
            _roomLobbyMissingSince = -1f;
            _lastObservedRoomStatus = string.Empty;
            _nextRoomStatusCheck = 0f;
            _nextRoomMembershipCheck = 0f;
            _lastRoomMessageId = 0;
            _roomMessageUnread = false;
            _nextRoomRefresh = 0f;
            _roomHeartbeatRunning = false;
        }

        private static int CurrentGamePlayerCount()
        {
            try
            {
                return Mathf.Clamp(ClientUserSystem.m_Users == null ? 1 : ClientUserSystem.m_Users.Count, 1, 4);
            }
            catch
            {
                return 1;
            }
        }

        private static string CurrentRoomStatus()
        {
            FrontendPlayerLobby playerLobby = FindFrontendPlayerLobby();
            return playerLobby != null && playerLobby.isActiveAndEnabled ? "lobby" : "playing";
        }

        private static string RoomStatusLabel(string status)
        {
            return string.Equals(status, "playing", StringComparison.Ordinal)
                ? OverrankText.Get("Playing", "游戏中")
                : OverrankText.Get("Lobby", "大厅中");
        }

        private static string LobbyChatNoticeText()
        {
            return OverrankText.Get(
                "Chat is not saved; only the latest 100 messages are shown.\nDo not trust links from strangers or reveal private information.",
                "服务器不会保存聊天数据，仅显示最近 100 条记录。\n请勿轻信陌生人的链接，也不要暴露任何隐私信息。");
        }

        private static string RoomChatNoticeText()
        {
            return OverrankText.Get(
                "Chat is not saved; only the latest 100 messages are shown. Do not trust links from strangers or reveal private information.",
                "服务器不会保存聊天数据，仅显示最近 100 条记录。请勿轻信陌生人的链接，也不要暴露任何隐私信息。");
        }

        private static GUIStyle CreateChatNoticeStyle()
        {
            GUIStyle style = new GUIStyle(GUI.skin.label);
            style.fontSize = 10;
            style.wordWrap = true;
            style.normal.textColor = new Color(0.7f, 0.7f, 0.74f, 1f);
            return style;
        }

        private static GUIStyle CreateSelectableChatStyle()
        {
            GUIStyle style = new GUIStyle(GUI.skin.textArea);
            style.fontSize = 12;
            style.wordWrap = true;
            return style;
        }

        private static string BuildChatTranscript(RoomMessage[] messages)
        {
            if (messages == null || messages.Length == 0)
            {
                return string.Empty;
            }
            StringBuilder builder = new StringBuilder(messages.Length * 64);
            for (int index = 0; index < messages.Length; index++)
            {
                RoomMessage message = messages[index];
                if (message == null)
                {
                    continue;
                }
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                    builder.AppendLine();
                }
                builder.Append(message.player_name ?? string.Empty);
                if (message.sent_at > 0L)
                {
                    builder.Append(" · ");
                    builder.Append(FormatChatTime(message.sent_at));
                }
                builder.AppendLine();
                builder.Append(message.text ?? string.Empty);
            }
            return builder.ToString();
        }

        private static string FormatChatTime(long unixSeconds)
        {
            try
            {
                DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                DateTime local = epoch.AddSeconds(unixSeconds).ToLocalTime();
                DateTime now = DateTime.Now;
                if (local.Date == now.Date)
                {
                    return local.ToString("HH:mm");
                }
                return local.Year == now.Year
                    ? local.ToString("MM-dd HH:mm")
                    : local.ToString("yyyy-MM-dd HH:mm");
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void RestoreChatInputFocus(string controlName, ref bool requested)
        {
            if (!requested)
            {
                return;
            }
            GUI.FocusControl(controlName);
            Event current = Event.current;
            if (current != null && current.type == EventType.Repaint)
            {
                requested = false;
            }
        }

        private static bool IsEnterPressed()
        {
            Event current = Event.current;
            return current != null
                && current.type == EventType.KeyDown
                && (current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter);
        }

        private static string DigitsOnly(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            StringBuilder builder = new StringBuilder(Math.Min(6, value.Length));
            for (int index = 0; index < value.Length && builder.Length < 6; index++)
            {
                char character = value[index];
                if (character >= '0' && character <= '9')
                {
                    builder.Append(character);
                }
            }
            return builder.ToString();
        }

        private static bool IsSixDigitRoomId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 6)
            {
                return false;
            }
            for (int index = 0; index < value.Length; index++)
            {
                if (value[index] < '0' || value[index] > '9')
                {
                    return false;
                }
            }
            return true;
        }

        private static string RoomErrorLabel(string error)
        {
            if (string.IsNullOrEmpty(error))
            {
                return string.Empty;
            }
            if (error.IndexOf("Incorrect room password", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return OverrankText.Get("Incorrect password", "密码错误");
            }
            if (error.IndexOf("game lobby is full", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return OverrankText.Get("The game lobby is full", "游戏战局已满");
            }
            if (error.IndexOf("game has already started", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return OverrankText.Get(
                    "The game has already started and cannot be joined.",
                    "游戏已经开始，无法加入");
            }
            if (error.IndexOf("kicked from the room", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return OverrankText.Get(
                    "You were removed from the room.",
                    "你已被房主移出房间");
            }
            if (error.IndexOf("Room list refresh rate limit exceeded", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return OverrankText.Get(
                    "Refreshing too quickly. Please wait a moment.",
                    "刷新过快，请稍后再试");
            }
            if (error.IndexOf("Lobby chat rate limit exceeded", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return OverrankText.Get(
                    "Sending too quickly. Please wait a moment.",
                    "发送过快，请稍后再试");
            }
            if (error.IndexOf("Room not found", StringComparison.OrdinalIgnoreCase) >= 0
                || error.IndexOf("HTTP 404", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return OverrankText.Get("The room is no longer available", "房间已关闭或不存在");
            }
            if (error.IndexOf("already own this room", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return OverrankText.Get(
                    "This room is already hosted by you.",
                    "这是你创建的房间");
            }
            if (error.IndexOf("already hosting", StringComparison.OrdinalIgnoreCase) >= 0
                || error.IndexOf("hosted room", StringComparison.OrdinalIgnoreCase) >= 0
                || error.IndexOf("Leave the current room", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return OverrankText.Get("Leave your current room first", "请先离开当前房间");
            }
            return error;
        }

        private static string LobbyChatErrorLabel(string error)
        {
            if (string.IsNullOrEmpty(error))
            {
                return string.Empty;
            }
            if (error.IndexOf("Lobby chat rate limit exceeded", StringComparison.OrdinalIgnoreCase) >= 0
                || error.IndexOf("HTTP 429", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return OverrankText.Get(
                    "Sending too quickly. Please wait a moment.",
                    "发送过快，请稍后再试");
            }
            if (error.IndexOf("HTTP 404", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return OverrankText.Get(
                    "Lobby chat is temporarily unavailable. Please try again later.",
                    "大厅聊天暂时不可用，请稍后再试");
            }
            return error;
        }

        private static bool HasDuplicateLabel(PlayedLevel[] levels, int currentIndex, string label)
        {
            for (int index = 0; index < levels.Length; index++)
            {
                if (index == currentIndex)
                {
                    continue;
                }
                PlayedLevel other = levels[index];
                string otherLabel = string.IsNullOrEmpty(other.level_label) ? other.level_name : other.level_label;
                if (string.Equals(label, otherLabel, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(levels[currentIndex].level_key, other.level_key, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static string ShortLevelKey(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return OverrankText.Get("unknown", "未知");
            }
            int dash = key.LastIndexOf('-');
            string suffix = dash >= 0 && dash + 1 < key.Length ? key.Substring(dash + 1) : key;
            return suffix.Length <= 8 ? suffix : suffix.Substring(0, 8);
        }

        private void RefreshBoard()
        {
            if (_requestRunning || string.IsNullOrEmpty(_selectedLevelKey) || _client == null)
            {
                return;
            }
            _requestRunning = true;
            _requestError = null;
            _responseSummary = null;
            string requestKey = CurrentLeaderboardCacheKey();
            int requestMode = _nearbyMode;
            string assistance = requestMode == 1
                ? "unassisted"
                : (requestMode == 2 ? "assisted" : "all");
            _topScroll = Vector2.zero;
            _nearbyScroll = Vector2.zero;
            _client.RequestLeaderboard(
                _selectedLevelKey,
                _players,
                _metric,
                _playerId,
                assistance,
                delegate(LeaderboardResponse response, string error)
                {
                    _requestRunning = false;
                    _requestError = error;
                    if (response != null)
                    {
                        if (!string.Equals(requestKey, CurrentLeaderboardCacheKey(), StringComparison.Ordinal))
                        {
                            RefreshBoard();
                            return;
                        }
                        CacheLeaderboard(requestKey, requestMode, response);
                        ApplyLeaderboard(response, true);
                    }
                });
        }

        private bool CanManuallyRefreshLeaderboard()
        {
            return !_requestRunning && Time.unscaledTime >= _nextManualLeaderboardRefresh;
        }

        private bool BeginManualLeaderboardRefresh()
        {
            if (!CanManuallyRefreshLeaderboard())
            {
                return false;
            }
            _nextManualLeaderboardRefresh = Time.unscaledTime + 1f;
            return true;
        }

        private string CurrentLeaderboardCacheKey()
        {
            return (_selectedLevelKey ?? string.Empty)
                + "|" + _players
                + "|" + (_metric ?? string.Empty)
                + "|" + (_playerId ?? string.Empty);
        }

        private void ClearLeaderboardCache()
        {
            _leaderboardCacheKey = null;
            _overallLeaderboard = null;
            _unassistedLeaderboard = null;
            _assistedLeaderboard = null;
        }

        private LeaderboardResponse GetCachedLeaderboard(int mode)
        {
            if (!string.Equals(
                    _leaderboardCacheKey,
                    CurrentLeaderboardCacheKey(),
                    StringComparison.Ordinal))
            {
                return null;
            }
            if (mode == 1)
            {
                return _unassistedLeaderboard;
            }
            if (mode == 2)
            {
                return _assistedLeaderboard;
            }
            return _overallLeaderboard;
        }

        private void CacheLeaderboard(string key, int mode, LeaderboardResponse response)
        {
            if (!string.Equals(_leaderboardCacheKey, key, StringComparison.Ordinal))
            {
                ClearLeaderboardCache();
                _leaderboardCacheKey = key;
            }
            if (mode == 1)
            {
                _unassistedLeaderboard = response;
            }
            else if (mode == 2)
            {
                _assistedLeaderboard = response;
            }
            else
            {
                _overallLeaderboard = response;
            }
        }

        private void SwitchNearbyMode()
        {
            _nearbyMode = (_nearbyMode + 1) % 3;
            LeaderboardResponse cached = GetCachedLeaderboard(_nearbyMode);
            if (cached != null)
            {
                _nearbyScroll = Vector2.zero;
                ApplyLeaderboard(cached, false);
                return;
            }
            RefreshBoard();
        }

        private void ApplyLeaderboard(LeaderboardResponse response, bool fromNetwork)
        {
            _leaderboard = response;
            _nearbySelectedOverwashed = response.nearby_overwashed_used;
            _centerNearbyOnSelf = true;
            int topCount = response.entries == null ? 0 : response.entries.Length;
            int nearbyCount = response.nearby == null ? 0 : response.nearby.Length;
            int selfEntryCount = response.self_entries == null ? 0 : response.self_entries.Length;
            _responseSummary = OverrankText.IsSimplifiedChinese
                ? "已加载 " + topCount + " 条成绩；你的排名："
                    + (response.self_rank > 0 ? "#" + response.self_rank : "未上榜")
                : "Loaded " + topCount + " score(s); your rank: "
                    + (response.self_rank > 0 ? "#" + response.self_rank : "not ranked");
            if (fromNetwork)
            {
                _log.LogInfo(
                    "Leaderboard loaded: level=" + response.level_key
                    + ", players=" + response.players
                    + ", total=" + response.total_players
                    + ", top=" + topCount
                    + ", nearby=" + nearbyCount
                    + ", selfEntries=" + selfEntryCount
                    + ", selfRank=" + response.self_rank + ".");
            }
        }

        private void RefreshLevels()
        {
            RefreshLevels(false);
        }

        private void RefreshLevels(bool selectLatestForLeaderboard)
        {
            if (_requestRunning || _client == null)
            {
                return;
            }
            _requestRunning = true;
            _requestError = null;
            _responseSummary = null;
            _client.RequestPlayedLevels(
                _playerId,
                delegate(PlayedLevelsResponse response, string error)
                {
                    _requestRunning = false;
                    _requestError = error;
                    if (response != null)
                    {
                        _playedLevels = response;
                        int count = response.levels == null ? 0 : response.levels.Length;
                        _responseSummary = OverrankText.IsSimplifiedChinese
                            ? "已加载 " + count + " 个已玩关卡"
                            : "Loaded " + count + " played level(s)";
                        _log.LogInfo("Played-level list loaded: count=" + count + ".");
                        if (selectLatestForLeaderboard && count > 0)
                        {
                            PlayedLevel latest = response.levels[0];
                            string label = string.IsNullOrEmpty(latest.level_label)
                                ? latest.level_name
                                : latest.level_label;
                            _selectedLevelKey = latest.level_key;
                            _selectedLevelName = LevelIdentity.ResolveDisplayName(latest.level_key, label);
                            _lastLevelKey.Value = _selectedLevelKey;
                            _lastLevelName.Value = _selectedLevelName;
                            _nearbyMode = 0;
                            _roomPage = 0;
                            _showLevels = false;
                            RefreshBoard();
                        }
                    }
                });
        }

        private void CreateIconTextures()
        {
            _iconBackground = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            _iconBackground.hideFlags = HideFlags.HideAndDontSave;
            _iconBackground.SetPixel(0, 0, new Color(0.06f, 0.045f, 0.09f, 0.88f));
            _iconBackground.Apply(false, true);

            _panelBackground = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            _panelBackground.hideFlags = HideFlags.HideAndDontSave;
            _panelBackground.SetPixel(0, 0, new Color(0.025f, 0.02f, 0.045f, 0.62f));
            _panelBackground.Apply(false, true);

            const int size = 22;
            _icon = new Texture2D(size, size, TextureFormat.ARGB32, false);
            _icon.hideFlags = HideFlags.HideAndDontSave;
            Color32[] pixels = new Color32[size * size];
            Color32 gold = new Color32(255, 202, 73, 255);
            Color32 pale = new Color32(255, 240, 183, 255);
            for (int x = 3; x <= 7; x++)
            {
                for (int y = 3; y <= 9; y++) pixels[y * size + x] = gold;
            }
            for (int x = 9; x <= 13; x++)
            {
                for (int y = 3; y <= 14; y++) pixels[y * size + x] = gold;
            }
            for (int x = 15; x <= 19; x++)
            {
                for (int y = 3; y <= 19; y++) pixels[y * size + x] = gold;
            }
            for (int x = 2; x <= 20; x++) pixels[2 * size + x] = pale;
            _icon.SetPixels32(pixels);
            _icon.Apply(false, true);

            const int assistantSize = 12;
            _overwashedIcon = new Texture2D(assistantSize, assistantSize, TextureFormat.ARGB32, false);
            _overwashedIcon.hideFlags = HideFlags.HideAndDontSave;
            Color32[] assistantPixels = new Color32[assistantSize * assistantSize];
            Color32 cyan = new Color32(91, 224, 255, 255);
            Color32 dark = new Color32(20, 48, 72, 255);
            for (int x = 2; x <= 9; x++)
            {
                for (int y = 2; y <= 9; y++) assistantPixels[y * assistantSize + x] = cyan;
            }
            assistantPixels[10 * assistantSize + 5] = cyan;
            assistantPixels[11 * assistantSize + 5] = cyan;
            assistantPixels[6 * assistantSize + 4] = dark;
            assistantPixels[6 * assistantSize + 7] = dark;
            for (int x = 4; x <= 7; x++) assistantPixels[3 * assistantSize + x] = dark;
            _overwashedIcon.SetPixels32(assistantPixels);
            _overwashedIcon.Apply(false, true);

            const int lockWidth = 14;
            const int lockHeight = 16;
            _lockIcon = new Texture2D(lockWidth, lockHeight, TextureFormat.ARGB32, false);
            _lockIcon.hideFlags = HideFlags.HideAndDontSave;
            Color32[] lockPixels = new Color32[lockWidth * lockHeight];
            Color32 lockGold = new Color32(255, 211, 92, 255);
            for (int x = 2; x <= 11; x++)
            {
                for (int y = 1; y <= 8; y++)
                {
                    lockPixels[y * lockWidth + x] = lockGold;
                }
            }
            for (int y = 8; y <= 13; y++)
            {
                lockPixels[y * lockWidth + 4] = lockGold;
                lockPixels[y * lockWidth + 9] = lockGold;
            }
            for (int x = 5; x <= 8; x++)
            {
                lockPixels[14 * lockWidth + x] = lockGold;
            }
            _lockIcon.SetPixels32(lockPixels);
            _lockIcon.Apply(false, true);

            _separatorTexture = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            _separatorTexture.hideFlags = HideFlags.HideAndDontSave;
            _separatorTexture.SetPixel(0, 0, new Color(0.62f, 0.62f, 0.68f, 0.72f));
            _separatorTexture.Apply(false, true);
        }

        private static string Truncate(string value, int length)
        {
            if (string.IsNullOrEmpty(value))
            {
                return OverrankText.Get("Unknown", "未知");
            }
            return value.Length <= length ? value : value.Substring(0, Math.Max(1, length - 1)) + "…";
        }
    }
}
