import hmac
import os
from contextlib import asynccontextmanager

from fastapi import Depends, FastAPI, Header, HTTPException, Query

from .database import connect, initialise, save_submission
from .schemas import Metric, Submission


def require_api_key(x_overrank_key: str | None = Header(default=None)) -> None:
    expected = os.environ.get("OVERRANK_API_KEY", "")
    if expected and (x_overrank_key is None or not hmac.compare_digest(expected, x_overrank_key)):
        raise HTTPException(status_code=401, detail="Invalid API key")


@asynccontextmanager
async def lifespan(_: FastAPI):
    initialise()
    yield


app = FastAPI(title="Overrank", version="0.2.2", lifespan=lifespan)


def board_order(metric: Metric) -> str:
    if metric == "dishes":
        return "dishes DESC, score DESC, completed_at ASC, submission_id ASC"
    return "score DESC, dishes DESC, completed_at ASC, submission_id ASC"


def entry(row) -> dict:
    return {
        "rank": int(row["rank_position"]),
        "player_id": row["player_id"],
        "player_name": row["player_name"],
        "score": int(row["score"]),
        "dishes": int(row["dishes"]),
        "overwashed_used": bool(row["overwashed_used"]),
        "overwashed_version": row["overwashed_version"],
        "completed_at": row["completed_at"],
    }


@app.get("/health")
def health() -> dict:
    return {"status": "ok", "version": app.version}


@app.post("/api/v1/submissions", dependencies=[Depends(require_api_key)])
def submit(payload: Submission) -> dict:
    level_key = payload.level_uid
    record = payload.model_dump()
    record["level_key"] = level_key
    with connect() as connection:
        connection.execute("BEGIN IMMEDIATE")
        updated = save_submission(connection, record)
    return {
        "accepted": True,
        "level_key": level_key,
        "personal_best_updated": updated,
    }


@app.get("/api/v1/leaderboards/{level_key}", dependencies=[Depends(require_api_key)])
def leaderboard(
    level_key: str,
    players: int = Query(ge=1, le=4),
    metric: Metric = Query(default="score"),
    player_id: str = Query(default="", max_length=128),
    limit: int = Query(default=100, ge=1, le=100),
    around: int = Query(default=100, ge=1, le=100),
    around_overwashed: bool | None = Query(default=None),
) -> dict:
    order = board_order(metric)
    requested_overwashed = around_overwashed if isinstance(around_overwashed, bool) else None
    common = f"""
        WITH ranked AS (
            SELECT *, ROW_NUMBER() OVER (ORDER BY {order}) AS rank_position
            FROM personal_bests
            WHERE level_key = ? AND player_count = ? AND metric = ?
        )
    """
    with connect() as connection:
        board_rows = connection.execute(
            common + " SELECT * FROM ranked ORDER BY rank_position LIMIT ?",
            (level_key, players, metric, limit),
        ).fetchall()
        total_row = connection.execute(
            """
            SELECT COUNT(*) AS total FROM personal_bests
            WHERE level_key = ? AND player_count = ? AND metric = ?
            """,
            (level_key, players, metric),
        ).fetchone()
        self_rows = []
        self_row = None
        nearby_rows = []
        if player_id:
            self_rows = connection.execute(
                common + " SELECT * FROM ranked WHERE player_id = ? ORDER BY rank_position",
                (level_key, players, metric, player_id),
            ).fetchall()
            if self_rows:
                if requested_overwashed is None:
                    self_row = self_rows[0]
                else:
                    self_row = next(
                        (
                            row
                            for row in self_rows
                            if bool(row["overwashed_used"]) == requested_overwashed
                        ),
                        self_rows[0],
                    )
            if self_row is not None:
                total_players = int(total_row["total"])
                window_size = min(around, total_players)
                first = max(1, int(self_row["rank_position"]) - window_size // 2)
                last = first + window_size - 1
                if last > total_players:
                    last = total_players
                    first = max(1, last - window_size + 1)
                nearby_rows = connection.execute(
                    common + " SELECT * FROM ranked WHERE rank_position BETWEEN ? AND ? ORDER BY rank_position",
                    (level_key, players, metric, first, last),
                ).fetchall()
        level_row = connection.execute(
            """
            SELECT dlc_id, level_id, level_name, level_label
            FROM player_levels WHERE level_key = ? ORDER BY last_played DESC LIMIT 1
            """,
            (level_key,),
        ).fetchone()

    return {
        "level_key": level_key,
        "level_name": "" if level_row is None else level_row["level_name"],
        "level_label": "" if level_row is None else level_row["level_label"],
        "dlc_id": -1 if level_row is None else int(level_row["dlc_id"]),
        "level_id": -1 if level_row is None else int(level_row["level_id"]),
        "players": players,
        "metric": metric,
        "total_players": int(total_row["total"]),
        "self_rank": 0 if self_row is None else int(self_row["rank_position"]),
        "nearby_overwashed_used": False if self_row is None else bool(self_row["overwashed_used"]),
        "self_entries": [entry(row) for row in self_rows],
        "entries": [entry(row) for row in board_rows],
        "nearby": [entry(row) for row in nearby_rows],
    }


@app.get("/api/v1/players/{player_id}/levels", dependencies=[Depends(require_api_key)])
def played_levels(player_id: str, limit: int = Query(default=100, ge=1, le=500)) -> dict:
    with connect() as connection:
        rows = connection.execute(
            """
            SELECT
                activity.level_key,
                activity.level_name,
                activity.level_label,
                activity.dlc_id,
                activity.level_id,
                COALESCE(best.best_score, 0) AS best_score,
                COALESCE(best.best_dishes, 0) AS best_dishes,
                activity.last_played
            FROM player_levels AS activity
            LEFT JOIN (
                SELECT
                    level_key,
                    MAX(CASE WHEN metric = 'score' THEN score END) AS best_score,
                    MAX(CASE WHEN metric = 'dishes' THEN dishes END) AS best_dishes
                FROM personal_bests
                WHERE player_id = ?
                GROUP BY level_key
            ) AS best ON best.level_key = activity.level_key
            WHERE activity.player_id = ?
            ORDER BY activity.last_played DESC
            LIMIT ?
            """,
            (player_id, player_id, limit),
        ).fetchall()
    return {
        "levels": [
            {
                "level_key": row["level_key"],
                "level_name": row["level_name"],
                "level_label": row["level_label"],
                "dlc_id": int(row["dlc_id"]),
                "level_id": int(row["level_id"]),
                "best_score": int(row["best_score"]),
                "best_dishes": int(row["best_dishes"]),
                "last_played": row["last_played"],
            }
            for row in rows
        ]
    }
