import os
import shutil
import sys
import unittest
from pathlib import Path


SERVER_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SERVER_ROOT))
TEST_ROOT = SERVER_ROOT / "tmp" / "tests"
os.environ["OVERRANK_DATABASE"] = str(TEST_ROOT / "overrank-test.sqlite3")

from overrank_server.app import leaderboard, played_levels, submit  # noqa: E402
from overrank_server.database import connect, initialise  # noqa: E402
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
            completed_at=completed_at,
        )

    def test_best_attempt_per_player_and_nearby_rank(self):
        board = "oc2diy-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        submit(self.payload("00000000-0000-0000-0000-000000000001", "player-one-00001", board, 800, 15, "One"))
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
        self.assertEqual([row["player_name"] for row in score_board["entries"]], ["One", "Two"])
        self.assertEqual(score_board["self_rank"], 2)
        self.assertEqual(len(score_board["nearby"]), 2)
        self.assertEqual(score_board["entries"][0]["dishes"], 11)

        dishes_board = leaderboard(board, players=2, metric="dishes", player_id="player-two-00002", limit=10, around=3)
        self.assertEqual(dishes_board["entries"][0]["player_name"], "One")
        self.assertEqual(dishes_board["entries"][0]["score"], 800)

        with connect() as connection:
            personal_best_count = connection.execute(
                "SELECT COUNT(*) FROM personal_bests WHERE player_id = ? AND level_key = ?",
                ("player-one-00001", board),
            ).fetchone()[0]
            activity_count = connection.execute(
                "SELECT COUNT(*) FROM player_levels WHERE player_id = ? AND level_key = ?",
                ("player-one-00001", board),
            ).fetchone()[0]
        self.assertEqual(personal_best_count, 2)
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


if __name__ == "__main__":
    unittest.main()
