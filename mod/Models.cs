using System;

#pragma warning disable 0649 // Unity JsonUtility fills response DTO fields by reflection.

namespace Overrank
{
    [Serializable]
    public sealed class ScoreSubmission
    {
        public string submission_id;
        public string player_id;
        public string player_name;
        public string level_uid;
        public int dlc_id;
        public int level_id;
        public string level_name;
        public string level_label;
        public int player_count;
        public int score;
        public int dishes;
        public int stars;
        public string mod_version;
        public bool overwashed_used;
        public string overwashed_version;
        public string completed_at;
        public string client_id;
        public string lobby_key;
        public string attempt_nonce;
        public string round_id;
    }

    [Serializable]
    public sealed class PendingSubmissionList
    {
        public ScoreSubmission[] items = new ScoreSubmission[0];
    }

    [Serializable]
    public sealed class PresenceHeartbeat
    {
        public string client_id;
        public string player_id;
        public string lobby_key;
        public string level_uid;
        public string attempt_nonce;
        public string round_id;
        public int local_players;
        public bool in_level;
        public string mod_version;
    }

    [Serializable]
    public sealed class PresenceResponse
    {
        public int online_clients;
        public int online_players;
        public int playing_players;
        public int heartbeat_seconds;
        public bool round_valid;
        public string round_id;
        public bool overwashed_used;
    }

    [Serializable]
    public sealed class RoundJoinRequest
    {
        public string client_id;
        public string player_id;
        public string lobby_key;
        public string level_uid;
        public int player_count;
        public string attempt_nonce;
        public bool overwashed_used;
        public string overwashed_version;
    }

    [Serializable]
    public sealed class RoundAssistanceRequest
    {
        public string client_id;
        public string attempt_nonce;
        public string overwashed_version;
    }

    [Serializable]
    public sealed class RoundResponse
    {
        public string round_id;
        public bool overwashed_used;
        public string overwashed_version;
        public int member_count;
    }

    [Serializable]
    public sealed class LeaderboardEntry
    {
        public int rank;
        public string player_id;
        public string player_name;
        public int score;
        public int dishes;
        public bool overwashed_used;
        public string overwashed_version;
        public string completed_at;
    }

    [Serializable]
    public sealed class LeaderboardResponse
    {
        public string level_key;
        public string level_name;
        public string level_label;
        public int dlc_id;
        public int level_id;
        public int players;
        public string metric;
        public string assistance;
        public int total_players;
        public int self_rank;
        public int self_percentile;
        public bool nearby_overwashed_used;
        public LeaderboardEntry[] self_entries = new LeaderboardEntry[0];
        public LeaderboardEntry[] entries = new LeaderboardEntry[0];
        public LeaderboardEntry[] nearby = new LeaderboardEntry[0];
    }

    [Serializable]
    public sealed class PlayedLevel
    {
        public string level_key;
        public string level_name;
        public string level_label;
        public int dlc_id;
        public int level_id;
        public int best_score;
        public int best_dishes;
        public string last_played;
    }

    [Serializable]
    public sealed class PlayedLevelsResponse
    {
        public PlayedLevel[] levels = new PlayedLevel[0];
    }

    [Serializable]
    public sealed class RoomCreateRequest
    {
        public string client_id;
        public string player_id;
        public string player_name;
        public string title;
        public string description;
        public string password;
        public string lobby_id;
        public int game_player_count;
        public int game_player_limit;
        public string status;
    }

    [Serializable]
    public sealed class RoomJoinRequest
    {
        public string client_id;
        public string player_id;
        public string player_name;
        public string password;
    }

    [Serializable]
    public sealed class RoomHeartbeatRequest
    {
        public string client_id;
        public string player_id;
        public string player_name;
        public string host_token;
        public string lobby_id;
        public int game_player_count;
        public int game_player_limit;
        public string status;
        public int last_message_id;
    }

    [Serializable]
    public sealed class RoomMessageRequest
    {
        public string client_id;
        public string player_id;
        public string player_name;
        public string text;
    }

    [Serializable]
    public sealed class RoomLeaveRequest
    {
        public string client_id;
        public string host_token;
    }

    [Serializable]
    public sealed class RoomKickRequest
    {
        public string client_id;
        public string host_token;
        public string target_client_id;
    }

    [Serializable]
    public sealed class RoomMember
    {
        public string client_id;
        public string player_id;
        public string player_name;
        public bool is_host;
    }

    [Serializable]
    public sealed class RoomMessage
    {
        public int message_id;
        public string player_id;
        public string player_name;
        public string text;
        public long sent_at;
    }

    [Serializable]
    public sealed class RoomInfo
    {
        public string room_id;
        public string title;
        public string description;
        public bool locked;
        public int game_player_count;
        public int game_player_limit;
        public string status;
        public string host_player_name;
        public int member_count;
        public RoomMember[] members = new RoomMember[0];
        public RoomMessage[] messages = new RoomMessage[0];
        public string lobby_id;
        public string host_token;
        public bool is_owner;
    }

    [Serializable]
    public sealed class RoomListResponse
    {
        public RoomInfo[] rooms = new RoomInfo[0];
        public RoomMessage[] messages = new RoomMessage[0];
        public int latest_message_id;
        public int oldest_message_id;
        public bool reset;
        public int room_revision;
        public bool rooms_changed;
    }

    [Serializable]
    public sealed class LobbyChatResponse
    {
        public RoomMessage[] messages = new RoomMessage[0];
        public int latest_message_id;
        public int oldest_message_id;
        public bool reset;
    }

    [Serializable]
    public sealed class RoomLeaveResponse
    {
        public bool left;
        public bool room_closed;
    }
}

#pragma warning restore 0649
