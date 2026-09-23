import os
import shutil
import sqlite3
import sys
import unittest
from pathlib import Path


SERVER_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SERVER_ROOT))
TEST_ROOT = SERVER_ROOT / "tmp" / "tests"
os.environ["OVERRANK_DATABASE"] = str(TEST_ROOT / "overrank-test.sqlite3")

import overrank_server.app as app_module  # noqa: E402
from overrank_server.app import (  # noqa: E402
    join_round,
    leaderboard,
    played_levels,
    presence,
    report_assistance,
    submit,
)
from overrank_server.database import connect, create_schema, initialise  # noqa: E402
from overrank_server.schemas import Presence, RoundAssistance, RoundJoin, Submission  # noqa: E402


class OverrankApiTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if TEST_ROOT.exists():
            shutil.rmtree(TEST_ROOT)
        TEST_ROOT.mkdir(parents=True)
        initialise()

    @classmethod
    def tearDownClass(cls):
        if TEST_ROOT.exists():
            shutil.rmtree(TEST_ROOT)

    def payload(
        self,
        submission_id,
        player_id,
        level_uid,
        score,
        dishes,
        name="Player",
        completed_at="2026-09-22T00:00:00Z",
        overwashed_used=False,
        overwashed_version="",
        client_id="",
        lobby_key="",
        attempt_nonce="",
        round_id="",
    ):
        return Submission(
            submission_id=submission_id,
            player_id=player_id,
            player_name=name,
            level_uid=level_uid,
            dlc_id=15,
            level_id=2,
            level_name="same_scene_name",
            level_label="Same display name",
            player_count=2,
            score=score,
            dishes=dishes,
            stars=3,
            mod_version="0.1.0",
            overwashed_used=overwashed_used,
            overwashed_version=overwashed_version,
            completed_at=completed_at,
            client_id=client_id,
            lobby_key=lobby_key,
            attempt_nonce=attempt_nonce,
            round_id=round_id,
        )

    def round_join(
        self,
        client_id,
        player_id,
        lobby_key,
        level_uid,
        attempt_nonce,
        overwashed_used=False,
    ):
        return join_round(
            RoundJoin(
                client_id=client_id,
                player_id=player_id,
                lobby_key=lobby_key,
                level_uid=level_uid,
                player_count=2,
                attempt_nonce=attempt_nonce,
                overwashed_used=overwashed_used,
                overwashed_version="1.7.0" if overwashed_used else "",
            )
        )

    def heartbeat(self, client_id, player_id, lobby_key, attempt_nonce, round_id):
        return presence(
            Presence(
                client_id=client_id,
                player_id=player_id,
                lobby_key=lobby_key,
                level_uid="official-online-test",
                attempt_nonce=attempt_nonce,
                round_id=round_id,
                local_players=1,
                in_level=True,
                mod_version="1.1.0",
            )
        )

    def test_clients_join_one_round_and_share_assistance(self):
        board = "official-online-shared-assistance"
        lobby = "a" * 64
        nonce_a = "30000000-0000-0000-0000-000000000011"
        nonce_b = "30000000-0000-0000-0000-000000000021"
        first_round = self.round_join("client-online-000a", "player-online-000a", lobby, board, nonce_a)
        second_round = self.round_join("client-online-000b", "player-online-000b", lobby, board, nonce_b)
        self.assertEqual(first_round["round_id"], second_round["round_id"])
        self.assertEqual(second_round["member_count"], 2)

        assisted = report_assistance(
            first_round["round_id"],
            RoundAssistance(
                client_id="client-online-000b",
                attempt_nonce=nonce_b,
                overwashed_version="1.7.0",
            ),
        )
        self.assertTrue(assisted["overwashed_used"])

        first = submit(self.payload(
            "30000000-0000-0000-0000-000000000010", "player-online-000a",
            board,
            1234,
            12,
            client_id="client-online-000a",
            lobby_key=lobby,
            attempt_nonce=nonce_a,
            round_id=first_round["round_id"],
        ))
        second = submit(self.payload(
            "30000000-0000-0000-0000-000000000020", "player-online-000b",
            board, 1234, 12, client_id="client-online-000b", lobby_key=lobby,
            attempt_nonce=nonce_b, round_id=first_round["round_id"],
        ))
        self.assertTrue(first["overwashed_used"])
        self.assertTrue(second["overwashed_used"])

        result = leaderboard(board, 2, "score", "player-online-000a", 10, 3)
        self.assertEqual(result["total_players"], 2)
        self.assertTrue(all(row["overwashed_used"] for row in result["entries"]))

    def test_same_attempt_is_idempotent_and_restart_creates_new_round(self):
        board = "official-online-restart"
        lobby = "b" * 64
        nonce_one = "30000000-0000-0000-0000-000000000031"
        nonce_two = "30000000-0000-0000-0000-000000000032"
        first = self.round_join("client-restart-000a", "player-restart-000a", lobby, board, nonce_one)
        repeated = self.round_join("client-restart-000a", "player-restart-000a", lobby, board, nonce_one)
        restarted = self.round_join("client-restart-000a", "player-restart-000a", lobby, board, nonce_two)
        old_retry = self.round_join("client-restart-000a", "player-restart-000a", lobby, board, nonce_one)
        self.assertEqual(first["round_id"], repeated["round_id"])
        self.assertNotEqual(first["round_id"], restarted["round_id"])
        self.assertEqual(first["round_id"], old_retry["round_id"])

    def test_teammate_without_overrank_does_not_block_submission(self):
        board = "official-online-single-client"
        lobby = "c" * 64
        nonce = "30000000-0000-0000-0000-000000000041"
        joined = self.round_join("client-only-new-mod", "player-only-new-mod", lobby, board, nonce)
        result = submit(
            self.payload(
                "30000000-0000-0000-0000-000000000030",
                "player-only-new-mod",
                board,
                888,
                8,
                client_id="client-only-new-mod",
                lobby_key=lobby,
                attempt_nonce=nonce,
                round_id=joined["round_id"],
            )
        )
        self.assertTrue(result["accepted"])
        self.assertEqual(leaderboard(board, 2, "score", "player-only-new-mod", 10, 3)["total_players"], 1)

    def test_presence_validates_round_and_reports_counts(self):
        board = "official-online-presence"
        lobby = "d" * 64
        nonce = "30000000-0000-0000-0000-000000000051"
        joined = self.round_join("client-presence-01", "player-presence-01", lobby, board, nonce)
        status = self.heartbeat(
            "client-presence-01", "player-presence-01", lobby, nonce, joined["round_id"]
        )
        self.assertTrue(status["round_valid"])
        self.assertEqual(status["round_id"], joined["round_id"])
        self.assertGreaterEqual(status["online_clients"], 1)
        self.assertGreaterEqual(status["playing_players"], 1)

    def test_submission_recovers_round_after_server_memory_restart(self):
        board = "official-online-recovery"
        lobby = "e" * 64
        nonce = "30000000-0000-0000-0000-000000000061"
        joined = self.round_join("client-recovery-01", "player-recovery-01", lobby, board, nonce, True)
        app_module._rounds.clear()
        app_module._active_round_by_lobby.clear()
        app_module._attempt_rounds.clear()
        result = submit(
            self.payload(
                "30000000-0000-0000-0000-000000000060", "player-recovery-01",
                board, 999, 9, overwashed_used=True, overwashed_version="1.7.0",
                client_id="client-recovery-01", lobby_key=lobby,
                attempt_nonce=nonce, round_id=joined["round_id"],
            )
        )
        self.assertTrue(result["accepted"])
        self.assertTrue(result["overwashed_used"])
        self.assertNotEqual(result["round_id"], joined["round_id"])

    def test_best_attempt_per_player_and_nearby_rank(self):
        board = "oc2diy-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        submit(
            self.payload(
                "00000000-0000-0000-0000-000000000001",
                "player-one-00001",
                board,
                1400,
                15,
                "One",
                overwashed_used=True,
                overwashed_version="1.7.0",
            )
        )
        submit(self.payload("00000000-0000-0000-0000-000000000002", "player-one-00001", board, 1200, 11, "One"))
        submit(self.payload("00000000-0000-0000-0000-000000000003", "player-two-00002", board, 1000, 12, "Two"))
        worse = submit(
            self.payload(
                "00000000-0000-0000-0000-000000000004",
                "player-one-00001",
                board,
                700,
                7,
                "One",
                "2026-09-23T00:00:00Z",
            )
        )
        self.assertEqual(worse["personal_best_updated"], {"score": False, "dishes": False})

        score_board = leaderboard(board, players=2, metric="score", player_id="player-two-00002", limit=10, around=3)
        self.assertEqual([row["player_name"] for row in score_board["entries"]], ["One", "One", "Two"])
        self.assertEqual(score_board["self_rank"], 3)
        self.assertEqual(len(score_board["nearby"]), 3)
        self.assertEqual(score_board["entries"][0]["score"], 1400)
        self.assertTrue(score_board["entries"][0]["overwashed_used"])
        self.assertEqual(score_board["entries"][1]["score"], 1200)
        self.assertFalse(score_board["entries"][1]["overwashed_used"])

        default_self = leaderboard(
            board,
            players=2,
            metric="score",
            player_id="player-one-00001",
            limit=10,
            around=1,
        )
        self.assertEqual([row["rank"] for row in default_self["self_entries"]], [1, 2])
        self.assertEqual(default_self["self_rank"], 1)
        self.assertTrue(default_self["nearby_overwashed_used"])
        self.assertEqual([row["rank"] for row in default_self["nearby"]], [1])

        unassisted_self = leaderboard(
            board,
            players=2,
            metric="score",
            player_id="player-one-00001",
            limit=10,
            around=1,
            around_overwashed=False,
        )
        self.assertEqual(unassisted_self["self_rank"], 2)
        self.assertFalse(unassisted_self["nearby_overwashed_used"])
        self.assertEqual([row["rank"] for row in unassisted_self["nearby"]], [2])

        assisted_self = leaderboard(
            board,
            players=2,
            metric="score",
            player_id="player-one-00001",
            limit=10,
            around=1,
            around_overwashed=True,
        )
        self.assertEqual(assisted_self["self_rank"], 1)
        self.assertTrue(assisted_self["nearby_overwashed_used"])
        self.assertEqual([row["rank"] for row in assisted_self["nearby"]], [1])

        unassisted_board = leaderboard(
            board,
            players=2,
            metric="score",
            player_id="player-one-00001",
            limit=10,
            around=10,
            assistance="unassisted",
        )
        self.assertEqual(unassisted_board["assistance"], "unassisted")
        self.assertEqual(unassisted_board["total_players"], 2)
        self.assertEqual(unassisted_board["self_rank"], 1)
        self.assertEqual([row["score"] for row in unassisted_board["entries"]], [1200, 1000])
        self.assertTrue(all(not row["overwashed_used"] for row in unassisted_board["entries"]))

        assisted_board = leaderboard(
            board,
            players=2,
            metric="score",
            player_id="player-one-00001",
            limit=10,
            around=10,
            assistance="assisted",
        )
        self.assertEqual(assisted_board["assistance"], "assisted")
        self.assertEqual(assisted_board["total_players"], 1)
        self.assertEqual(assisted_board["self_rank"], 1)
        self.assertEqual([row["score"] for row in assisted_board["entries"]], [1400])
        self.assertTrue(all(row["overwashed_used"] for row in assisted_board["entries"]))

        dishes_board = leaderboard(board, players=2, metric="dishes", player_id="player-two-00002", limit=10, around=3)
        self.assertEqual(dishes_board["entries"][0]["player_name"], "One")
        self.assertEqual(dishes_board["entries"][0]["score"], 1400)
        self.assertTrue(dishes_board["entries"][0]["overwashed_used"])
        self.assertEqual(dishes_board["entries"][0]["overwashed_version"], "1.7.0")

        with connect() as connection:
            personal_best_count = connection.execute(
                "SELECT COUNT(*) FROM personal_bests WHERE player_id = ? AND level_key = ?",
                ("player-one-00001", board),
            ).fetchone()[0]
            activity_count = connection.execute(
                "SELECT COUNT(*) FROM player_levels WHERE player_id = ? AND level_key = ?",
                ("player-one-00001", board),
            ).fetchone()[0]
        self.assertEqual(personal_best_count, 4)
        self.assertEqual(activity_count, 1)
        self.assertEqual(played_levels("player-one-00001", 100)["levels"][0]["last_played"], "2026-09-23T00:00:00Z")

    def test_duplicate_submission_is_idempotent(self):
        payload = self.payload(
            "00000000-0000-0000-0000-000000000010",
            "player-three-003",
            "official-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            500,
            5,
        )
        first = submit(payload)
        repeated = submit(payload)
        self.assertTrue(first["accepted"])
        self.assertEqual(first["personal_best_updated"], {"score": True, "dishes": True})
        self.assertTrue(repeated["accepted"])
        self.assertEqual(repeated["personal_best_updated"], {"score": False, "dishes": False})

        with connect() as connection:
            count = connection.execute(
                "SELECT COUNT(*) FROM personal_bests WHERE player_id = ?",
                ("player-three-003",),
            ).fetchone()[0]
        self.assertEqual(count, 2)

    def test_same_name_different_custom_uid_stays_separate(self):
        first = "oc2diy-cccccccccccccccccccccccccccccccc"
        second = "oc2diy-dddddddddddddddddddddddddddddddd"
        submit(self.payload("00000000-0000-0000-0000-000000000020", "player-four-0004", first, 400, 4))
        submit(self.payload("00000000-0000-0000-0000-000000000021", "player-four-0004", second, 900, 9))
        self.assertEqual(leaderboard(first, 2, "score", "player-four-0004", 10, 3)["entries"][0]["score"], 400)
        self.assertEqual(leaderboard(second, 2, "score", "player-four-0004", 10, 3)["entries"][0]["score"], 900)
        self.assertGreaterEqual(len(played_levels("player-four-0004", 100)["levels"]), 2)

    def test_equal_metric_values_share_rank_and_percentile(self):
        board = "official-tied-ranking-values"
        attempts = (
            ("40000000-0000-0000-0000-000000000001", "tie-player-00001", "Alpha", 1000, 5),
            ("40000000-0000-0000-0000-000000000002", "tie-player-00002", "Bravo", 1000, 4),
            ("40000000-0000-0000-0000-000000000003", "tie-player-00003", "Charlie", 900, 5),
            ("40000000-0000-0000-0000-000000000004", "tie-player-00004", "Delta", 800, 5),
        )
        for submission_id, player_id, name, score, dishes in attempts:
            submit(self.payload(submission_id, player_id, board, score, dishes, name))

        score_board = leaderboard(
            board, 2, "score", "tie-player-00002", limit=100, around=3
        )
        self.assertEqual(
            [(row["player_name"], row["rank"]) for row in score_board["entries"]],
            [("Alpha", 1), ("Bravo", 1), ("Charlie", 3), ("Delta", 4)],
        )
        self.assertEqual(score_board["self_rank"], 1)
        self.assertEqual(score_board["self_percentile"], 25)
        self.assertEqual(len(score_board["nearby"]), 3)

        dishes_board = leaderboard(
            board, 2, "dishes", "tie-player-00003", limit=100, around=3
        )
        self.assertEqual(
            [(row["player_name"], row["rank"]) for row in dishes_board["entries"]],
            [("Alpha", 1), ("Charlie", 1), ("Delta", 1), ("Bravo", 4)],
        )
        self.assertEqual(dishes_board["self_rank"], 1)
        self.assertEqual(dishes_board["self_percentile"], 25)

    def test_large_board_returns_one_hundred_top_and_nearby_entries(self):
        board = "official-eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"
        for index in range(1, 201):
            submit(
                self.payload(
                    f"10000000-0000-0000-0000-{index:012x}",
                    f"large-player-{index:04d}",
                    board,
                    10000 - index,
                    index,
                    f"Player {index:03d}",
                )
            )

        response = leaderboard(
            board,
            players=2,
            metric="score",
            player_id="large-player-0100",
            limit=100,
            around=100,
        )
        self.assertEqual(response["total_players"], 200)
        self.assertEqual(response["self_rank"], 100)
        self.assertEqual(len(response["entries"]), 100)
        self.assertEqual(response["entries"][0]["rank"], 1)
        self.assertEqual(response["entries"][-1]["rank"], 100)
        self.assertEqual(len(response["nearby"]), 100)
        self.assertEqual(response["nearby"][0]["rank"], 50)
        self.assertEqual(response["nearby"][-1]["rank"], 149)

    def test_schema_v2_personal_best_is_migrated_as_unassisted(self):
        connection = sqlite3.connect(":memory:")
        connection.row_factory = sqlite3.Row
        try:
            connection.executescript(
                """
                CREATE TABLE personal_bests (
                    player_id TEXT NOT NULL,
                    level_key TEXT NOT NULL,
                    player_count INTEGER NOT NULL,
                    metric TEXT NOT NULL,
                    submission_id TEXT NOT NULL,
                    player_name TEXT NOT NULL,
                    dlc_id INTEGER NOT NULL,
                    level_id INTEGER NOT NULL,
                    level_name TEXT NOT NULL,
                    level_label TEXT NOT NULL,
                    score INTEGER NOT NULL,
                    dishes INTEGER NOT NULL,
                    stars INTEGER NOT NULL,
                    mod_version TEXT NOT NULL,
                    completed_at TEXT NOT NULL,
                    received_at TEXT NOT NULL,
                    PRIMARY KEY (player_id, level_key, player_count, metric)
                );
                INSERT INTO personal_bests VALUES (
                    'legacy-player', 'official-legacy', 2, 'score',
                    '20000000-0000-0000-0000-000000000001', 'Legacy', -1, 2,
                    'Story_1_1', '1-1', 900, 9, 3, '0.2.0',
                    '2026-09-22T00:00:00Z', '2026-09-22 00:00:01'
                );
                """
            )
            create_schema(connection)
            columns = connection.execute("PRAGMA table_info(personal_bests)").fetchall()
            primary_key = [row[1] for row in sorted(columns, key=lambda row: row[5]) if row[5] > 0]
            migrated = connection.execute("SELECT * FROM personal_bests").fetchone()
            self.assertEqual(
                primary_key,
                ["player_id", "level_key", "player_count", "metric", "overwashed_used"],
            )
            self.assertEqual(migrated["score"], 900)
            self.assertEqual(migrated["overwashed_used"], 0)
            self.assertEqual(migrated["overwashed_version"], "")
        finally:
            connection.close()


if __name__ == "__main__":
    unittest.main()
