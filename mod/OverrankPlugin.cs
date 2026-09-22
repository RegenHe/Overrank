using System;
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
        private ConfigEntry<string> _apiKey;
        private ConfigEntry<int> _timeoutSeconds;
        private ConfigEntry<string> _installId;
        private ConfigEntry<string> _lastLevelKey;
        private ConfigEntry<string> _lastLevelName;
        private ConfigEntry<int> _lastPlayerCount;

        private Texture2D _iconBackground;
        private Texture2D _icon;
        private Texture2D _panelBackground;
        private bool _showPanel;
        private bool _showLevels;
        private bool _requestRunning;
        private string _requestError;
        private string _responseSummary;
        private string _playerId;
        private string _playerName = "Player";
        private string _selectedLevelKey;
        private string _selectedLevelName;
        private string _metric = "score";
        private int _players = 1;
        private int _levelsPage;
        private float _nextIdentityRefresh;
        private float _lastCaptureTime = -100f;
        private string _lastCaptureFingerprint;
        private LeaderboardResponse _leaderboard;
        private PlayedLevelsResponse _playedLevels;

        private void Awake()
        {
            _log = Logger;
            _serverUrl = BuildConfig.DefaultServerUrl;
            _apiKey = Config.Bind("Server", "ApiKey", BuildConfig.DefaultApiKey, "Optional server API key.");
            _timeoutSeconds = Config.Bind(
                "Server",
                "TimeoutSeconds",
                8,
                new ConfigDescription("Network timeout in seconds.", new AcceptableValueRange<int>(2, 60)));
            _installId = Config.Bind("Identity", "InstallId", string.Empty, "Fallback anonymous installation identifier.");
            _lastLevelKey = Config.Bind("State", "LastLevelKey", string.Empty, "Last uploaded level.");
            _lastLevelName = Config.Bind("State", "LastLevelName", string.Empty, "Last uploaded level display name.");
            _lastPlayerCount = Config.Bind(
                "State",
                "LastPlayerCount",
                1,
                new ConfigDescription("Last uploaded player count.", new AcceptableValueRange<int>(1, 4)));

            if (string.IsNullOrEmpty(_installId.Value))
            {
                _installId.Value = Guid.NewGuid().ToString("N");
            }
            _selectedLevelKey = _lastLevelKey.Value ?? string.Empty;
            _selectedLevelName = _lastLevelName.Value ?? string.Empty;
            _players = Mathf.Clamp(_lastPlayerCount.Value, 1, 4);
            _playerId = HashIdentity("install:" + _installId.Value);

            CreateIconTextures();
            RefreshIdentity();
            _client = new LeaderboardClient(this, _log, _serverUrl, _apiKey, _timeoutSeconds);
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
        }

        private void OnDestroy()
        {
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
                    completed_at = DateTime.UtcNow.ToString("o")
                };

                _selectedLevelKey = level.Uid;
                _selectedLevelName = level.DisplayName;
                _players = playerCount;
                _lastLevelKey.Value = _selectedLevelKey;
                _lastLevelName.Value = _selectedLevelName;
                _lastPlayerCount.Value = _players;
                _client.Enqueue(submission);
                if (_showPanel && !_showLevels)
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
                    return;
                }
                _playerId = HashIdentity(GetStableUserIdentity(user.UID));
                if (!string.IsNullOrEmpty(user.DisplayName))
                {
                    _playerName = user.DisplayName;
                }
            }
            catch
            {
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
            if (_showPanel)
            {
                if (_showLevels)
                {
                    RefreshLevels();
                }
                else
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
            GUI.DrawTexture(iconRect, _iconBackground, ScaleMode.StretchToFill, true);
            GUI.DrawTexture(new Rect(left + 5f, 17f, 22f, 22f), _icon, ScaleMode.ScaleToFit, true);
            Event current = Event.current;
            if (current != null
                && current.type == EventType.MouseDown
                && current.button == 0
                && iconRect.Contains(current.mousePosition))
            {
                _showPanel = !_showPanel;
                if (_showPanel)
                {
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

                _selectedLevelKey = level.Uid;
                _selectedLevelName = level.DisplayName;
                int count = ClientUserSystem.m_Users == null ? 1 : ClientUserSystem.m_Users.Count;
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
            const float width = 600f;
            const float height = 452f;
            float left = Mathf.Max(8f, Screen.width - width - 12f);
            Rect panel = new Rect(left, 52f, width, height);
            GUI.DrawTexture(panel, _panelBackground, ScaleMode.StretchToFill, true);
            GUI.Box(panel, "Overrank");

            if (GUI.Button(new Rect(panel.x + panel.width - 34f, panel.y + 5f, 24f, 22f), "X"))
            {
                _showPanel = false;
                return;
            }
            if (GUI.Button(new Rect(panel.x + 14f, panel.y + 32f, 112f, 26f), "Leaderboard"))
            {
                OpenLeaderboard();
            }
            if (GUI.Button(new Rect(panel.x + 132f, panel.y + 32f, 112f, 26f), "Played levels"))
            {
                _showLevels = true;
                RefreshLevels();
            }

            if (_showLevels)
            {
                DrawLevels(panel);
            }
            else
            {
                DrawLeaderboard(panel);
            }

            string networkStatus = _requestRunning
                ? "Loading..."
                : (!string.IsNullOrEmpty(_requestError)
                    ? _requestError
                    : (!string.IsNullOrEmpty(_responseSummary)
                        ? _responseSummary
                        : (_client == null ? string.Empty : _client.Status)));
            GUI.Label(new Rect(panel.x + 14f, panel.y + panel.height - 25f, panel.width - 28f, 20f), networkStatus ?? string.Empty);
        }

        private void DrawLeaderboard(Rect panel)
        {
            string title = string.IsNullOrEmpty(_selectedLevelName) ? "No uploaded level yet" : _selectedLevelName;
            GUI.Label(new Rect(panel.x + 14f, panel.y + 68f, panel.width - 150f, 24f), title);
            if (GUI.Button(new Rect(panel.x + panel.width - 94f, panel.y + 66f, 80f, 24f), "Refresh"))
            {
                RefreshBoard();
            }

            GUI.Label(new Rect(panel.x + 14f, panel.y + 98f, 52f, 22f), "Rank by");
            if (GUI.Toggle(new Rect(panel.x + 70f, panel.y + 98f, 70f, 22f), _metric == "score", "Score") && _metric != "score")
            {
                _metric = "score";
                RefreshBoard();
            }
            if (GUI.Toggle(new Rect(panel.x + 144f, panel.y + 98f, 78f, 22f), _metric == "dishes", "Dishes") && _metric != "dishes")
            {
                _metric = "dishes";
                RefreshBoard();
            }

            GUI.Label(new Rect(panel.x + 250f, panel.y + 98f, 58f, 22f), "Players");
            for (int count = 1; count <= 4; count++)
            {
                if (GUI.Toggle(
                    new Rect(panel.x + 308f + (count - 1) * 48f, panel.y + 98f, 45f, 22f),
                    _players == count,
                    count.ToString())
                    && _players != count)
                {
                    _players = count;
                    _lastPlayerCount.Value = count;
                    RefreshBoard();
                }
            }

            float bodyTop = panel.y + 130f;
            float columnWidth = (panel.width - 42f) * 0.5f;
            GUI.Box(new Rect(panel.x + 14f, bodyTop, columnWidth, 278f), "Top players");
            GUI.Box(new Rect(panel.x + 28f + columnWidth, bodyTop, columnWidth, 278f), "Around me");
            LeaderboardEntry[] top = _leaderboard == null || _leaderboard.entries == null
                ? new LeaderboardEntry[0]
                : _leaderboard.entries;
            LeaderboardEntry[] nearby = _leaderboard == null || _leaderboard.nearby == null
                ? new LeaderboardEntry[0]
                : _leaderboard.nearby;
            DrawEntries(new Rect(panel.x + 22f, bodyTop + 28f, columnWidth - 16f, 240f), top);
            DrawEntries(new Rect(panel.x + 36f + columnWidth, bodyTop + 28f, columnWidth - 16f, 240f), nearby);
        }

        private void DrawEntries(Rect area, LeaderboardEntry[] entries)
        {
            if (entries.Length == 0)
            {
                GUI.Label(new Rect(area.x, area.y, area.width, 22f), "No scores for this selection");
                return;
            }
            int count = Mathf.Min(entries.Length, 10);
            for (int index = 0; index < count; index++)
            {
                LeaderboardEntry entry = entries[index];
                bool self = string.Equals(entry.player_id, _playerId, StringComparison.Ordinal);
                Color previous = GUI.color;
                if (self)
                {
                    GUI.color = new Color(0.55f, 1f, 0.75f, 1f);
                }
                string value = "Score:" + entry.score + "  Dishes:" + entry.dishes;
                GUI.Label(
                    new Rect(area.x, area.y + index * 22f, area.width, 21f),
                    "#" + entry.rank + "  " + Truncate(entry.player_name, 10) + "    " + value);
                GUI.color = previous;
            }
        }

        private void DrawLevels(Rect panel)
        {
            if (GUI.Button(new Rect(panel.x + panel.width - 94f, panel.y + 66f, 80f, 24f), "Refresh"))
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
                "Levels uploaded by " + Truncate(_playerName, 28));

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
                    _showLevels = false;
                    RefreshBoard();
                }
                GUI.Label(new Rect(panel.x + 22f, y + 2f, panel.width - 230f, 20f), Truncate(displayLabel, 42));
                GUI.Label(
                    new Rect(panel.x + panel.width - 205f, y + 2f, 185f, 20f),
                    "Score: " + level.best_score + "  Dishes: " + level.best_dishes);
            }
            if (levels.Length == 0 && !_requestRunning)
            {
                GUI.Label(new Rect(panel.x + 14f, panel.y + 105f, panel.width - 28f, 22f), "No uploaded levels yet");
            }
            GUI.enabled = _levelsPage > 0;
            if (GUI.Button(new Rect(panel.x + 14f, panel.y + 407f, 70f, 24f), "Previous"))
            {
                _levelsPage--;
            }
            GUI.enabled = _levelsPage < maxPage;
            if (GUI.Button(new Rect(panel.x + 90f, panel.y + 407f, 70f, 24f), "Next"))
            {
                _levelsPage++;
            }
            GUI.enabled = true;
            GUI.Label(new Rect(panel.x + 170f, panel.y + 410f, 100f, 20f), (_levelsPage + 1) + " / " + (maxPage + 1));
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
                return "unknown";
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
            _client.RequestLeaderboard(
                _selectedLevelKey,
                _players,
                _metric,
                _playerId,
                delegate(LeaderboardResponse response, string error)
                {
                    _requestRunning = false;
                    _requestError = error;
                    if (response != null)
                    {
                        _leaderboard = response;
                        int topCount = response.entries == null ? 0 : response.entries.Length;
                        int nearbyCount = response.nearby == null ? 0 : response.nearby.Length;
                        _responseSummary = "Loaded " + topCount + " score(s); your rank: "
                            + (response.self_rank > 0 ? "#" + response.self_rank : "not ranked");
                        _log.LogInfo(
                            "Leaderboard loaded: level=" + response.level_key
                            + ", players=" + response.players
                            + ", total=" + response.total_players
                            + ", top=" + topCount
                            + ", nearby=" + nearbyCount
                            + ", selfRank=" + response.self_rank + ".");
                    }
                });
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
                        _responseSummary = "Loaded " + count + " played level(s)";
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
        }

        private static string Truncate(string value, int length)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "Unknown";
            }
            return value.Length <= length ? value : value.Substring(0, Math.Max(1, length - 1)) + "…";
        }
    }
}
