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

from overrank_server.app import leaderboard, played_levels, submit  # noqa: E402
from overrank_server.database import connect, create_schema, initialise  # noqa: E402
from overrank_server.schemas import Submission  # noqa: E402


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
        )

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
