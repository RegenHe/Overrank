using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
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
        private readonly string _apiKey;
        private readonly int _timeoutSeconds;
        private readonly string _pendingPath;
        private readonly List<ScoreSubmission> _pending = new List<ScoreSubmission>();
        private bool _queueRunning;
        private bool _presenceRunning;
        private bool _roundJoinRunning;
        private bool _assistanceRunning;
        private string _lastReportedError;

        internal string Status { get; private set; }

        internal event Action SubmissionUploaded;

        internal LeaderboardClient(
            MonoBehaviour host,
            ManualLogSource log,
            string serverUrl,
            string apiKey,
            int timeoutSeconds)
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
            Status = OverrankText.Get("Score queued for upload", "成绩已加入上传队列");
            Start();
        }

        internal void RequestLeaderboard(
            string levelKey,
            int players,
            string metric,
            string playerId,
            string clientId,
            string assistance,
            Action<LeaderboardResponse, string> callback)
        {
            string url = BaseUrl
                + "/api/v1/leaderboards/" + UnityWebRequest.EscapeURL(levelKey)
                + "?players=" + players
                + "&metric=" + UnityWebRequest.EscapeURL(metric)
                + "&player_id=" + UnityWebRequest.EscapeURL(playerId)
                + "&client_id=" + UnityWebRequest.EscapeURL(clientId)
                + "&assistance=" + UnityWebRequest.EscapeURL(assistance)
                + "&limit=100&around=100";
            _host.StartCoroutine(GetJson(url, callback));
        }

        internal void RequestPlayedLevels(string playerId, Action<PlayedLevelsResponse, string> callback)
        {
            string url = BaseUrl + "/api/v1/players/" + UnityWebRequest.EscapeURL(playerId) + "/levels";
            _host.StartCoroutine(GetJson(url, callback));
        }

        internal void RequestRooms(
            string clientId,
            int afterMessageId,
            int roomRevision,
            Action<RoomListResponse, string> callback)
        {
            _host.StartCoroutine(GetJson(
                BaseUrl + "/api/v1/rooms?client_id=" + UnityWebRequest.EscapeURL(clientId)
                    + "&after_message_id=" + Math.Max(0, afterMessageId)
                    + "&room_revision=" + Math.Max(0, roomRevision),
                callback));
        }

        internal void RequestLobbyMessages(int afterId, Action<LobbyChatResponse, string> callback)
        {
            _host.StartCoroutine(GetJson(
                BaseUrl + "/api/v1/chat/lobby/messages?after_id=" + Math.Max(0, afterId),
                callback));
        }

        internal void CreateRoom(RoomCreateRequest request, Action<RoomInfo, string> callback)
        {
            _host.StartCoroutine(PostJsonResponse(
                BaseUrl + "/api/v1/rooms",
                JsonUtility.ToJson(request),
                callback));
        }

        internal void JoinRoom(string roomId, RoomJoinRequest request, Action<RoomInfo, string> callback)
        {
            _host.StartCoroutine(PostJsonResponse(
                BaseUrl + "/api/v1/rooms/" + UnityWebRequest.EscapeURL(roomId) + "/join",
                JsonUtility.ToJson(request),
                callback));
        }

        internal void SendRoomHeartbeat(
            string roomId,
            RoomHeartbeatRequest request,
            Action<RoomInfo, string> callback)
        {
            _host.StartCoroutine(PostJsonResponse(
                BaseUrl + "/api/v1/rooms/" + UnityWebRequest.EscapeURL(roomId) + "/heartbeat",
                JsonUtility.ToJson(request),
                callback));
        }

        internal void SendRoomMessage(
            string roomId,
            RoomMessageRequest request,
            Action<RoomInfo, string> callback)
        {
            _host.StartCoroutine(PostJsonResponse(
                BaseUrl + "/api/v1/rooms/" + UnityWebRequest.EscapeURL(roomId) + "/messages",
                JsonUtility.ToJson(request),
                callback));
        }

        internal void SendLobbyMessage(
            RoomMessageRequest request,
            Action<LobbyChatResponse, string> callback)
        {
            _host.StartCoroutine(PostJsonResponse(
                BaseUrl + "/api/v1/chat/lobby/messages",
                JsonUtility.ToJson(request),
                callback));
        }

        internal void LeaveRoom(
            string roomId,
            RoomLeaveRequest request,
            Action<RoomLeaveResponse, string> callback)
        {
            _host.StartCoroutine(PostJsonResponse(
                BaseUrl + "/api/v1/rooms/" + UnityWebRequest.EscapeURL(roomId) + "/leave",
                JsonUtility.ToJson(request),
                callback));
        }

        internal void KickRoomMember(
            string roomId,
            RoomKickRequest request,
            Action<RoomInfo, string> callback)
        {
            _host.StartCoroutine(PostJsonResponse(
                BaseUrl + "/api/v1/rooms/" + UnityWebRequest.EscapeURL(roomId) + "/kick",
                JsonUtility.ToJson(request),
                callback));
        }

        internal bool SendPresence(PresenceHeartbeat heartbeat, Action<PresenceResponse, string> callback)
        {
            if (_presenceRunning || heartbeat == null)
            {
                return false;
            }
            _host.StartCoroutine(PostPresence(heartbeat, callback));
            return true;
        }

        internal bool JoinRound(RoundJoinRequest request, Action<RoundResponse, string> callback)
        {
            if (_roundJoinRunning || request == null)
            {
                return false;
            }
            _host.StartCoroutine(PostRoundJoin(request, callback));
            return true;
        }

        internal bool ReportAssistance(
            string roundId,
            RoundAssistanceRequest request,
            Action<RoundResponse, string> callback)
        {
            if (_assistanceRunning || string.IsNullOrEmpty(roundId) || request == null)
            {
                return false;
            }
            _host.StartCoroutine(PostRoundAssistance(roundId, request, callback));
            return true;
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
                    Status = OverrankText.Get("Connected", "已连接");
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
                    Status = _pending.Count == 0
                        ? OverrankText.Get("Score uploaded", "成绩已上传")
                        : OverrankText.Get("Uploading queued scores", "正在上传队列中的成绩");
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
                    Status = OverrankText.Get("Offline - score kept for retry", "离线：成绩已保存，稍后重试");
                    ReportNetworkError(error);
                    yield return new WaitForSecondsRealtime(15f);
                }
            }
        }

        private IEnumerator PostPresence(PresenceHeartbeat heartbeat, Action<PresenceResponse, string> callback)
        {
            _presenceRunning = true;
            string responseBody = null;
            string error = null;
            yield return PostJson(
                BaseUrl + "/api/v1/presence",
                JsonUtility.ToJson(heartbeat),
                delegate(string response, string requestError)
                {
                    responseBody = response;
                    error = requestError;
                });
            _presenceRunning = false;

            PresenceResponse parsed = null;
            if (string.IsNullOrEmpty(error))
            {
                try
                {
                    parsed = JsonUtility.FromJson<PresenceResponse>(responseBody);
                }
                catch (Exception exception)
                {
                    error = OverrankText.Get("Invalid presence response: ", "在线状态响应无效：") + exception.Message;
                }
            }
            if (callback != null)
            {
                callback(parsed, error);
            }
        }

        private IEnumerator PostRoundJoin(RoundJoinRequest request, Action<RoundResponse, string> callback)
        {
            _roundJoinRunning = true;
            RoundResponse parsed = null;
            string responseBody = null;
            string error = null;
            yield return PostJson(
                BaseUrl + "/api/v1/rounds/join",
                JsonUtility.ToJson(request),
                delegate(string response, string requestError)
                {
                    responseBody = response;
                    error = requestError;
                });
            _roundJoinRunning = false;
            if (string.IsNullOrEmpty(error))
            {
                try
                {
                    parsed = JsonUtility.FromJson<RoundResponse>(responseBody);
                    if (parsed == null || string.IsNullOrEmpty(parsed.round_id))
                    {
                        error = OverrankText.Get("Server returned an invalid round", "服务器返回了无效对局");
                    }
                }
                catch (Exception exception)
                {
                    error = OverrankText.Get("Invalid round response: ", "对局响应无效：") + exception.Message;
                }
            }
            if (callback != null)
            {
                callback(parsed, error);
            }
        }

        private IEnumerator PostRoundAssistance(
            string roundId,
            RoundAssistanceRequest request,
            Action<RoundResponse, string> callback)
        {
            _assistanceRunning = true;
            RoundResponse parsed = null;
            string responseBody = null;
            string error = null;
            yield return PostJson(
                BaseUrl + "/api/v1/rounds/" + UnityWebRequest.EscapeURL(roundId) + "/assistance",
                JsonUtility.ToJson(request),
                delegate(string response, string requestError)
                {
                    responseBody = response;
                    error = requestError;
                });
            _assistanceRunning = false;
            if (string.IsNullOrEmpty(error))
            {
                try
                {
                    parsed = JsonUtility.FromJson<RoundResponse>(responseBody);
                }
                catch (Exception exception)
                {
                    error = OverrankText.Get("Invalid assistance response: ", "机器人状态响应无效：") + exception.Message;
                }
            }
            if (callback != null)
            {
                callback(parsed, error);
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
                request.timeout = Mathf.Clamp(_timeoutSeconds, 2, 60);
                yield return request.SendWebRequest();
                string error = RequestError(request);
                callback(request.downloadHandler == null ? string.Empty : request.downloadHandler.text, error);
            }
        }

        private IEnumerator PostJsonResponse<T>(string url, string json, Action<T, string> callback)
            where T : class
        {
            string responseBody = null;
            string error = null;
            yield return PostJson(
                url,
                json,
                delegate(string response, string requestError)
                {
                    responseBody = response;
                    error = requestError;
                });
            T parsed = null;
            if (string.IsNullOrEmpty(error))
            {
                try
                {
                    parsed = JsonUtility.FromJson<T>(responseBody);
                    PopulateNestedArrays(parsed, responseBody);
                    if (parsed == null)
                    {
                        error = OverrankText.Get(
                            "Server returned an empty JSON object",
                            "服务器返回了空的 JSON 对象");
                    }
                }
                catch (Exception exception)
                {
                    error = OverrankText.Get("Invalid server response: ", "服务器响应无效：")
                        + exception.Message;
                }
            }
            if (callback != null)
            {
                callback(parsed, error);
            }
        }

        private IEnumerator GetJson<T>(string url, Action<T, string> callback) where T : class
        {
            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                AddHeaders(request);
                request.timeout = Mathf.Clamp(_timeoutSeconds, 2, 60);
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
                        callback(null, OverrankText.Get("Server returned an empty JSON object", "服务器返回了空的 JSON 对象"));
                    }
                    else
                    {
                        callback(parsed, null);
                    }
                }
                catch (Exception exception)
                {
                    callback(
                        null,
                        OverrankText.Get("Invalid server response: ", "服务器响应无效：") + exception.Message);
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
                return;
            }

            RoomListResponse roomList = parsed as RoomListResponse;
            if (roomList != null)
            {
                roomList.rooms = ParseObjectArray<RoomInfo>(json, "rooms");
                roomList.messages = ParseObjectArray<RoomMessage>(json, "messages");
                return;
            }

            LobbyChatResponse lobbyChat = parsed as LobbyChatResponse;
            if (lobbyChat != null)
            {
                lobbyChat.messages = ParseObjectArray<RoomMessage>(json, "messages");
                return;
            }

            RoomInfo room = parsed as RoomInfo;
            if (room != null)
            {
                room.members = ParseObjectArray<RoomMember>(json, "members");
                room.messages = ParseObjectArray<RoomMessage>(json, "messages");
            }
        }

        private static TItem[] ParseObjectArray<TItem>(string json, string fieldName) where TItem : class
        {
            List<TItem> items = new List<TItem>();
            if (string.IsNullOrEmpty(json))
            {
                return items.ToArray();
            }

            int arrayStart = FindTopLevelArrayStart(json, fieldName);
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

        private static int FindTopLevelArrayStart(string json, string fieldName)
        {
            bool insideString = false;
            bool escaped = false;
            int stringStart = -1;
            int objectDepth = 0;
            int arrayDepth = 0;
            for (int index = 0; index < json.Length; index++)
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
                        int length = index - stringStart;
                        if (objectDepth == 1
                            && arrayDepth == 0
                            && length == fieldName.Length
                            && string.CompareOrdinal(json, stringStart, fieldName, 0, length) == 0)
                        {
                            int separator = index + 1;
                            while (separator < json.Length && char.IsWhiteSpace(json[separator]))
                            {
                                separator++;
                            }
                            if (separator < json.Length && json[separator] == ':')
                            {
                                separator++;
                                while (separator < json.Length && char.IsWhiteSpace(json[separator]))
                                {
                                    separator++;
                                }
                                if (separator < json.Length && json[separator] == '[')
                                {
                                    return separator;
                                }
                            }
                        }
                    }
                    continue;
                }

                if (current == '"')
                {
                    insideString = true;
                    stringStart = index + 1;
                }
                else if (current == '{')
                {
                    objectDepth++;
                }
                else if (current == '}')
                {
                    objectDepth--;
                }
                else if (current == '[')
                {
                    arrayDepth++;
                }
                else if (current == ']')
                {
                    arrayDepth--;
                }
            }
            return -1;
        }

        private void AddHeaders(UnityWebRequest request)
        {
            string key = _apiKey == null ? string.Empty : _apiKey.Trim();
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
