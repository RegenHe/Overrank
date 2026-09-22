using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Networking;

namespace Overrank
{
    internal sealed class LeaderboardClient
    {
        private readonly MonoBehaviour _host;
        private readonly ManualLogSource _log;
        private readonly string _serverUrl;
        private readonly ConfigEntry<string> _apiKey;
        private readonly ConfigEntry<int> _timeoutSeconds;
        private readonly string _pendingPath;
        private readonly List<ScoreSubmission> _pending = new List<ScoreSubmission>();
        private bool _queueRunning;
        private string _lastReportedError;

        internal string Status { get; private set; }

        internal event Action SubmissionUploaded;

        internal LeaderboardClient(
            MonoBehaviour host,
            ManualLogSource log,
            string serverUrl,
            ConfigEntry<string> apiKey,
            ConfigEntry<int> timeoutSeconds)
        {
            _host = host;
            _log = log;
            _serverUrl = serverUrl;
            _apiKey = apiKey;
            _timeoutSeconds = timeoutSeconds;
            _pendingPath = Path.Combine(Paths.ConfigPath, "Overrank.pending.json");
            LoadPending();
        }

        internal void Start()
        {
            if (!_queueRunning)
            {
                _host.StartCoroutine(ProcessQueue());
            }
        }

        internal void Enqueue(ScoreSubmission submission)
        {
            _pending.Add(submission);
            SavePending();
            Status = "Score queued for upload";
            Start();
        }

        internal void RequestLeaderboard(
            string levelKey,
            int players,
            string metric,
            string playerId,
            Action<LeaderboardResponse, string> callback)
        {
            string url = BaseUrl
                + "/api/v1/leaderboards/" + UnityWebRequest.EscapeURL(levelKey)
                + "?players=" + players
                + "&metric=" + UnityWebRequest.EscapeURL(metric)
                + "&player_id=" + UnityWebRequest.EscapeURL(playerId)
                + "&limit=10&around=3";
            _host.StartCoroutine(GetJson(url, callback));
        }

        internal void RequestPlayedLevels(string playerId, Action<PlayedLevelsResponse, string> callback)
        {
            string url = BaseUrl + "/api/v1/players/" + UnityWebRequest.EscapeURL(playerId) + "/levels";
            _host.StartCoroutine(GetJson(url, callback));
        }

        private string BaseUrl
        {
            get { return (_serverUrl ?? string.Empty).Trim().TrimEnd('/'); }
        }

        private IEnumerator ProcessQueue()
        {
            _queueRunning = true;
            while (true)
            {
                if (_pending.Count == 0)
                {
                    Status = "Connected";
                    yield return new WaitForSecondsRealtime(10f);
                    continue;
                }

                bool completed = false;
                bool succeeded = false;
                string error = null;
                ScoreSubmission current = _pending[0];
                yield return PostJson(
                    BaseUrl + "/api/v1/submissions",
                    JsonUtility.ToJson(current),
                    delegate(string response, string requestError)
                    {
                        completed = true;
                        error = requestError;
                        succeeded = string.IsNullOrEmpty(requestError);
                    });

                if (completed && succeeded)
                {
                    _pending.RemoveAt(0);
                    SavePending();
                    Status = _pending.Count == 0 ? "Score uploaded" : "Uploading queued scores";
                    _lastReportedError = null;
                    Action callback = SubmissionUploaded;
                    if (callback != null)
                    {
                        callback();
                    }
                    yield return null;
                }
                else
                {
                    Status = "Offline - score kept for retry";
                    ReportNetworkError(error);
                    yield return new WaitForSecondsRealtime(15f);
                }
            }
        }

        private IEnumerator PostJson(string url, string json, Action<string, string> callback)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(body);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                AddHeaders(request);
                request.timeout = Mathf.Clamp(_timeoutSeconds.Value, 2, 60);
                yield return request.SendWebRequest();
                string error = RequestError(request);
                callback(request.downloadHandler == null ? string.Empty : request.downloadHandler.text, error);
            }
        }

        private IEnumerator GetJson<T>(string url, Action<T, string> callback) where T : class
        {
            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                AddHeaders(request);
                request.timeout = Mathf.Clamp(_timeoutSeconds.Value, 2, 60);
                yield return request.SendWebRequest();
                string error = RequestError(request);
                if (!string.IsNullOrEmpty(error))
                {
                    callback(null, error);
                    yield break;
                }
                try
                {
                    T parsed = JsonUtility.FromJson<T>(request.downloadHandler.text);
                    PopulateNestedArrays(parsed, request.downloadHandler.text);
                    if (parsed == null)
                    {
                        callback(null, "Server returned an empty JSON object");
                    }
                    else
                    {
                        callback(parsed, null);
                    }
                }
                catch (Exception exception)
                {
                    callback(null, "Invalid server response: " + exception.Message);
                }
            }
        }

        private static void PopulateNestedArrays<T>(T parsed, string json) where T : class
        {
            LeaderboardResponse leaderboard = parsed as LeaderboardResponse;
            if (leaderboard != null)
            {
                leaderboard.entries = ParseObjectArray<LeaderboardEntry>(json, "entries");
                leaderboard.nearby = ParseObjectArray<LeaderboardEntry>(json, "nearby");
                return;
            }

            PlayedLevelsResponse played = parsed as PlayedLevelsResponse;
            if (played != null)
            {
                played.levels = ParseObjectArray<PlayedLevel>(json, "levels");
            }
        }

        private static TItem[] ParseObjectArray<TItem>(string json, string fieldName) where TItem : class
        {
            List<TItem> items = new List<TItem>();
            if (string.IsNullOrEmpty(json))
            {
                return items.ToArray();
            }

            string marker = "\"" + fieldName + "\"";
            int markerIndex = json.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                return items.ToArray();
            }

            int arrayStart = json.IndexOf('[', markerIndex + marker.Length);
            if (arrayStart < 0)
            {
                return items.ToArray();
            }

            bool insideString = false;
            bool escaped = false;
            int objectDepth = 0;
            int objectStart = -1;
            for (int index = arrayStart + 1; index < json.Length; index++)
            {
                char current = json[index];
                if (insideString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (current == '\\')
                    {
                        escaped = true;
                    }
                    else if (current == '"')
                    {
                        insideString = false;
                    }
                    continue;
                }

                if (current == '"')
                {
                    insideString = true;
                    continue;
                }
                if (current == ']' && objectDepth == 0)
                {
                    break;
                }
                if (current == '{')
                {
                    if (objectDepth == 0)
                    {
                        objectStart = index;
                    }
                    objectDepth++;
                    continue;
                }
                if (current != '}' || objectDepth <= 0)
                {
                    continue;
                }

                objectDepth--;
                if (objectDepth == 0 && objectStart >= 0)
                {
                    string itemJson = json.Substring(objectStart, index - objectStart + 1);
                    TItem item = JsonUtility.FromJson<TItem>(itemJson);
                    if (item != null)
                    {
                        items.Add(item);
                    }
                    objectStart = -1;
                }
            }
            return items.ToArray();
        }

        private void AddHeaders(UnityWebRequest request)
        {
            string key = _apiKey.Value == null ? string.Empty : _apiKey.Value.Trim();
            if (key.Length > 0)
            {
                request.SetRequestHeader("X-Overrank-Key", key);
            }
            request.SetRequestHeader("User-Agent", "Overrank/" + BuildInfo.Version);
        }

        private static string RequestError(UnityWebRequest request)
        {
            if (request.isNetworkError || request.isHttpError)
            {
                string body = request.downloadHandler == null ? string.Empty : request.downloadHandler.text;
                return "HTTP " + request.responseCode + ": " + request.error + (body.Length == 0 ? string.Empty : " " + body);
            }
            return null;
        }

        private void LoadPending()
        {
            try
            {
                if (!File.Exists(_pendingPath))
                {
                    return;
                }
                PendingSubmissionList saved = JsonUtility.FromJson<PendingSubmissionList>(File.ReadAllText(_pendingPath));
                if (saved != null && saved.items != null)
                {
                    _pending.AddRange(saved.items);
                }
            }
            catch (Exception exception)
            {
                _log.LogWarning("Could not load pending Overrank scores: " + exception.Message);
            }
        }

        private void SavePending()
        {
            try
            {
                PendingSubmissionList saved = new PendingSubmissionList { items = _pending.ToArray() };
                File.WriteAllText(_pendingPath, JsonUtility.ToJson(saved));
            }
            catch (Exception exception)
            {
                _log.LogWarning("Could not save pending Overrank scores: " + exception.Message);
            }
        }

        private void ReportNetworkError(string error)
        {
            if (string.IsNullOrEmpty(error) || string.Equals(error, _lastReportedError, StringComparison.Ordinal))
            {
                return;
            }
            _lastReportedError = error;
            _log.LogWarning("Overrank server unavailable; the score will retry later. " + error);
        }
    }
}
