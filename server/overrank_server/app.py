import hmac
import os
from contextlib import asynccontextmanager

from fastapi import Depends, FastAPI, Header, HTTPException, Query

from .database import connect, initialise
from .schemas import Metric, Submission


def require_api_key(x_overrank_key: str | None = Header(default=None)) -> None:
    expected = os.environ.get("OVERRANK_API_KEY", "")
    if expected and (x_overrank_key is None or not hmac.compare_digest(expected, x_overrank_key)):
        raise HTTPException(status_code=401, detail="Invalid API key")


@asynccontextmanager
async def lifespan(_: FastAPI):
    initialise()
    yield


app = FastAPI(title="Overrank", version="0.1.0", lifespan=lifespan)


def board_order(metric: Metric) -> str:
    if metric == "dishes":
        return "dishes DESC, score DESC, completed_at ASC, id ASC"
    return "score DESC, dishes DESC, completed_at ASC, id ASC"


def entry(row) -> dict:
    return {
        "rank": int(row["rank_position"]),
        "player_id": row["player_id"],
        "player_name": row["player_name"],
        "score": int(row["score"]),
        "dishes": int(row["dishes"]),
        "completed_at": row["completed_at"],
    }


@app.get("/health")
def health() -> dict:
    return {"status": "ok", "version": app.version}


@app.post("/api/v1/submissions", dependencies=[Depends(require_api_key)])
def submit(payload: Submission) -> dict:
    level_key = payload.level_uid
    values = (
        payload.submission_id,
        payload.player_id,
        payload.player_name,
        payload.dlc_id,
        payload.level_id,
        level_key,
        payload.level_name,
        payload.level_label,
        payload.player_count,
        payload.score,
        payload.dishes,
        payload.stars,
        payload.mod_version,
        payload.completed_at,
    )
    with connect() as connection:
        cursor = connection.execute(
            """
            INSERT OR IGNORE INTO attempts (
                submission_id, player_id, player_name, dlc_id, level_id, level_key,
                level_name, level_label, player_count, score, dishes, stars,
                mod_version, completed_at
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            values,
        )
        accepted = cursor.rowcount == 1
    return {"accepted": accepted, "duplicate": not accepted, "level_key": level_key}


@app.get("/api/v1/leaderboards/{level_key}", dependencies=[Depends(require_api_key)])
def leaderboard(
    level_key: str,
    players: int = Query(ge=1, le=4),
    metric: Metric = Query(default="score"),
    player_id: str = Query(default="", max_length=128),
    limit: int = Query(default=10, ge=1, le=50),
    around: int = Query(default=3, ge=0, le=10),
) -> dict:
    order = board_order(metric)
    common = f"""
        WITH selected AS (
            SELECT *, ROW_NUMBER() OVER (
                PARTITION BY player_id ORDER BY {order}
            ) AS player_choice
            FROM attempts
            WHERE level_key = ? AND player_count = ?
        ), best AS (
            SELECT * FROM selected WHERE player_choice = 1
        ), ranked AS (
            SELECT *, ROW_NUMBER() OVER (ORDER BY {order}) AS rank_position
            FROM best
        )
    """
    with connect() as connection:
        board_rows = connection.execute(
            common + " SELECT * FROM ranked ORDER BY rank_position LIMIT ?",
            (level_key, players, limit),
        ).fetchall()
        total_row = connection.execute(
            "SELECT COUNT(DISTINCT player_id) AS total FROM attempts WHERE level_key = ? AND player_count = ?",
            (level_key, players),
        ).fetchone()
        self_row = None
        nearby_rows = []
        if player_id:
            self_row = connection.execute(
                common + " SELECT * FROM ranked WHERE player_id = ?",
                (level_key, players, player_id),
            ).fetchone()
            if self_row is not None:
                first = max(1, int(self_row["rank_position"]) - around)
                last = int(self_row["rank_position"]) + around
                nearby_rows = connection.execute(
                    common + " SELECT * FROM ranked WHERE rank_position BETWEEN ? AND ? ORDER BY rank_position",
                    (level_key, players, first, last),
                ).fetchall()
        level_row = connection.execute(
            """
            SELECT dlc_id, level_id, level_name, level_label
            FROM attempts WHERE level_key = ? ORDER BY id DESC LIMIT 1
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
        "entries": [entry(row) for row in board_rows],
        "nearby": [entry(row) for row in nearby_rows],
    }


@app.get("/api/v1/players/{player_id}/levels", dependencies=[Depends(require_api_key)])
def played_levels(player_id: str, limit: int = Query(default=100, ge=1, le=500)) -> dict:
    with connect() as connection:
        rows = connection.execute(
            """
            SELECT
                level_key,
                MAX(level_name) AS level_name,
                MAX(level_label) AS level_label,
                MAX(dlc_id) AS dlc_id,
                MAX(level_id) AS level_id,
                MAX(score) AS best_score,
                MAX(dishes) AS best_dishes,
                MAX(completed_at) AS last_played
            FROM attempts
            WHERE player_id = ?
            GROUP BY level_key
            ORDER BY last_played DESC
            LIMIT ?
            """,
            (player_id, limit),
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
