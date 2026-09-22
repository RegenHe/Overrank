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
from overrank_server.database import initialise  # noqa: E402
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

    def payload(self, submission_id, player_id, level_uid, score, dishes, name="Player"):
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
            completed_at="2026-09-22T00:00:00Z",
        )

    def test_best_attempt_per_player_and_nearby_rank(self):
        board = "oc2diy-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        submit(self.payload("00000000-0000-0000-0000-000000000001", "player-one-00001", board, 800, 9, "One"))
        submit(self.payload("00000000-0000-0000-0000-000000000002", "player-one-00001", board, 1200, 11, "One"))
        submit(self.payload("00000000-0000-0000-0000-000000000003", "player-two-00002", board, 1000, 12, "Two"))

        score_board = leaderboard(board, players=2, metric="score", player_id="player-two-00002", limit=10, around=3)
        self.assertEqual([row["player_name"] for row in score_board["entries"]], ["One", "Two"])
        self.assertEqual(score_board["self_rank"], 2)
        self.assertEqual(len(score_board["nearby"]), 2)

        dishes_board = leaderboard(board, players=2, metric="dishes", player_id="player-two-00002", limit=10, around=3)
        self.assertEqual(dishes_board["entries"][0]["player_name"], "Two")

    def test_duplicate_submission_is_idempotent(self):
        payload = self.payload(
            "00000000-0000-0000-0000-000000000010",
            "player-three-003",
            "official-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            500,
            5,
        )
        self.assertTrue(submit(payload)["accepted"])
        self.assertTrue(submit(payload)["duplicate"])

    def test_same_name_different_custom_uid_stays_separate(self):
        first = "oc2diy-cccccccccccccccccccccccccccccccc"
        second = "oc2diy-dddddddddddddddddddddddddddddddd"
        submit(self.payload("00000000-0000-0000-0000-000000000020", "player-four-0004", first, 400, 4))
        submit(self.payload("00000000-0000-0000-0000-000000000021", "player-four-0004", second, 900, 9))
        self.assertEqual(leaderboard(first, 2, "score", "player-four-0004", 10, 3)["entries"][0]["score"], 400)
        self.assertEqual(leaderboard(second, 2, "score", "player-four-0004", 10, 3)["entries"][0]["score"], 900)
        self.assertGreaterEqual(len(played_levels("player-four-0004", 100)["levels"]), 2)


if __name__ == "__main__":
    unittest.main()
