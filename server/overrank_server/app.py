import hmac
import os
import threading
import time
from contextlib import asynccontextmanager
from uuid import uuid4

from fastapi import Depends, FastAPI, Header, HTTPException, Query

from .database import connect, initialise, save_submission
from .schemas import (
    Assistance,
    Metric,
    Presence,
    RoundAssistance,
    RoundJoin,
    Submission,
    plain_player_name,
)


PRESENCE_TTL_SECONDS = 150
ROUND_TTL_SECONDS = 20 * 60
_presence_lock = threading.Lock()
_presence: dict[str, dict] = {}
_round_lock = threading.Lock()
_rounds: dict[str, dict] = {}
_active_round_by_lobby: dict[str, str] = {}
_attempt_rounds: dict[tuple[str, str], str] = {}


def require_api_key(x_overrank_key: str | None = Header(default=None)) -> None:
    expected = os.environ.get("OVERRANK_API_KEY", "")
    if expected and (x_overrank_key is None or not hmac.compare_digest(expected, x_overrank_key)):
        raise HTTPException(status_code=401, detail="Invalid API key")


@asynccontextmanager
async def lifespan(_: FastAPI):
    initialise()
    yield


app = FastAPI(title="Overrank", version="1.1.0", lifespan=lifespan)


def _active_presence(now: float | None = None) -> list[dict]:
    current = time.time() if now is None else now
    cutoff = current - PRESENCE_TTL_SECONDS
    expired = [client_id for client_id, item in _presence.items() if item["seen_at"] < cutoff]
    for client_id in expired:
        _presence.pop(client_id, None)
    return list(_presence.values())


def _purge_rounds(now: float) -> None:
    expired = [
        round_id
        for round_id, state in _rounds.items()
        if state["last_seen"] < now - ROUND_TTL_SECONDS
    ]
    for round_id in expired:
        state = _rounds.pop(round_id, None)
        if state is not None and _active_round_by_lobby.get(state["lobby_key"]) == round_id:
            _active_round_by_lobby.pop(state["lobby_key"], None)
    stale_attempts = [key for key, round_id in _attempt_rounds.items() if round_id not in _rounds]
    for key in stale_attempts:
        _attempt_rounds.pop(key, None)


def _round_response(state: dict) -> dict:
    return {
        "round_id": state["round_id"],
        "overwashed_used": bool(state["overwashed_used"]),
        "overwashed_version": state["overwashed_version"],
        "member_count": len(state["members"]),
    }


def _join_round_locked(data: dict, now: float) -> dict:
    _purge_rounds(now)
    client_id = data["client_id"]
    attempt_nonce = data["attempt_nonce"]
    attempt_key = (client_id, attempt_nonce)
    mapped_id = _attempt_rounds.get(attempt_key)
    if mapped_id in _rounds:
        state = _rounds[mapped_id]
        state["last_seen"] = now
        if data.get("overwashed_used", False):
            state["overwashed_used"] = True
            if data.get("overwashed_version"):
                state["overwashed_version"] = data["overwashed_version"]
        return state

    lobby_key = data["lobby_key"]
    active_id = _active_round_by_lobby.get(lobby_key)
    state = _rounds.get(active_id) if active_id else None
    create_new = (
        state is None
        or state["level_uid"] != data["level_uid"]
        or state["player_count"] != data["player_count"]
    )
    if not create_new:
        previous_nonce = state["members"].get(client_id)
        create_new = previous_nonce is not None and previous_nonce != attempt_nonce

    if create_new:
        round_id = str(uuid4())
        state = {
            "round_id": round_id,
            "lobby_key": lobby_key,
            "level_uid": data["level_uid"],
            "player_count": data["player_count"],
            "created_at": now,
            "last_seen": now,
            "overwashed_used": False,
            "overwashed_version": "",
            "members": {},
        }
        _rounds[round_id] = state
        _active_round_by_lobby[lobby_key] = round_id

    state["members"][client_id] = attempt_nonce
    state["last_seen"] = now
    _attempt_rounds[attempt_key] = state["round_id"]
    if data.get("overwashed_used", False):
        state["overwashed_used"] = True
        if data.get("overwashed_version"):
            state["overwashed_version"] = data["overwashed_version"]
    return state


def _resolve_submission_round(payload: Submission, now: float) -> dict | None:
    if not payload.lobby_key or not payload.client_id or not payload.attempt_nonce:
        return None
    state = _rounds.get(payload.round_id) if payload.round_id else None
    if state is not None:
        same_attempt = state["members"].get(payload.client_id) == payload.attempt_nonce
        same_level = state["level_uid"] == payload.level_uid
        same_lobby = state["lobby_key"] == payload.lobby_key
        same_player_count = state["player_count"] == payload.player_count
        if not same_attempt or not same_level or not same_lobby or not same_player_count:
            state = None
    if state is None:
        state = _join_round_locked(
            {
                "client_id": payload.client_id,
                "player_id": payload.player_id,
                "lobby_key": payload.lobby_key,
                "level_uid": payload.level_uid,
                "player_count": payload.player_count,
                "attempt_nonce": payload.attempt_nonce,
                "overwashed_used": payload.overwashed_used,
                "overwashed_version": payload.overwashed_version,
            },
            now,
        )
    state["last_seen"] = now
    if payload.overwashed_used:
        state["overwashed_used"] = True
        if payload.overwashed_version:
            state["overwashed_version"] = payload.overwashed_version
    return state


def board_order(metric: Metric) -> str:
    if metric == "dishes":
        return "dishes DESC, score DESC, completed_at ASC, submission_id ASC"
    return "score DESC, dishes DESC, completed_at ASC, submission_id ASC"


def rank_order(metric: Metric) -> str:
    return "dishes DESC" if metric == "dishes" else "score DESC"


def rank_percentile(rank: int, total: int) -> int:
    if rank <= 0 or total <= 0:
        return 0
    return min(100, max(1, (rank * 100 + total - 1) // total))


def entry(row) -> dict:
    return {
        "rank": int(row["rank_position"]),
        "player_id": row["player_id"],
        "player_name": plain_player_name(row["player_name"]),
        "score": int(row["score"]),
        "dishes": int(row["dishes"]),
        "overwashed_used": bool(row["overwashed_used"]),
        "overwashed_version": row["overwashed_version"],
        "completed_at": row["completed_at"],
    }


@app.get("/health")
def health() -> dict:
    return {"status": "ok", "version": app.version}


@app.post("/api/v1/presence", dependencies=[Depends(require_api_key)])
def presence(payload: Presence) -> dict:
    now = time.time()
    record = payload.model_dump()
    record["seen_at"] = now
    with _presence_lock:
        _presence[payload.client_id] = record
        active = _active_presence(now)
        online_players = sum(int(item["local_players"]) for item in active)
        playing_players = sum(
            int(item["local_players"]) for item in active if bool(item["in_level"])
        )
    round_valid = False
    round_state = None
    if payload.round_id:
        with _round_lock:
            _purge_rounds(now)
            round_state = _rounds.get(payload.round_id)
            round_valid = (
                round_state is not None
                and round_state["members"].get(payload.client_id) == payload.attempt_nonce
            )
            if round_valid:
                round_state["last_seen"] = now
    return {
        "online_clients": len(active),
        "online_players": online_players,
        "playing_players": playing_players,
        "heartbeat_seconds": 60,
        "round_valid": round_valid,
        "round_id": payload.round_id if round_valid else "",
        "overwashed_used": False if not round_valid else bool(round_state["overwashed_used"]),
    }


@app.post("/api/v1/rounds/join", dependencies=[Depends(require_api_key)])
def join_round(payload: RoundJoin) -> dict:
    with _round_lock:
        state = _join_round_locked(payload.model_dump(), time.time())
        return _round_response(state)


@app.post("/api/v1/rounds/{round_id}/assistance", dependencies=[Depends(require_api_key)])
def report_assistance(round_id: str, payload: RoundAssistance) -> dict:
    with _round_lock:
        _purge_rounds(time.time())
        state = _rounds.get(round_id)
        if state is None:
            raise HTTPException(status_code=404, detail="Round not found")
        if state["members"].get(payload.client_id) != payload.attempt_nonce:
            raise HTTPException(status_code=409, detail="Client attempt does not belong to this round")
        state["overwashed_used"] = True
        if payload.overwashed_version:
            state["overwashed_version"] = payload.overwashed_version
        state["last_seen"] = time.time()
        return _round_response(state)


@app.post("/api/v1/submissions", dependencies=[Depends(require_api_key)])
def submit(payload: Submission) -> dict:
    level_key = payload.level_uid
    record = payload.model_dump()
    record["level_key"] = level_key
    round_id = ""
    with _round_lock:
        _purge_rounds(time.time())
        state = _resolve_submission_round(payload, time.time())
        if state is not None:
            round_id = state["round_id"]
            record["overwashed_used"] = bool(state["overwashed_used"])
            record["overwashed_version"] = state["overwashed_version"] if state["overwashed_used"] else ""
    with connect() as connection:
        connection.execute("BEGIN IMMEDIATE")
        updated = save_submission(connection, record)
    return {
        "accepted": True,
        "round_id": round_id,
        "level_key": level_key,
        "overwashed_used": bool(record["overwashed_used"]),
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
    assistance: Assistance = Query(default="all"),
) -> dict:
    order = board_order(metric)
    ranking = rank_order(metric)
    requested_overwashed = around_overwashed if isinstance(around_overwashed, bool) else None
    selected_assistance = assistance if assistance in ("all", "unassisted", "assisted") else "all"
    board_filter = "WHERE level_key = ? AND player_count = ? AND metric = ?"
    board_parameters = (level_key, players, metric)
    if selected_assistance != "all":
        board_filter += " AND overwashed_used = ?"
        board_parameters += (1 if selected_assistance == "assisted" else 0,)
    common = f"""
        WITH ranked AS (
            SELECT *,
                RANK() OVER (ORDER BY {ranking}) AS rank_position,
                ROW_NUMBER() OVER (ORDER BY {order}) AS row_position
            FROM personal_bests
            {board_filter}
        )
    """
    with connect() as connection:
        board_rows = connection.execute(
            common + " SELECT * FROM ranked ORDER BY row_position LIMIT ?",
            board_parameters + (limit,),
        ).fetchall()
        total_row = connection.execute(
            "SELECT COUNT(*) AS total FROM personal_bests " + board_filter,
            board_parameters,
        ).fetchone()
        self_rows = []
        self_row = None
        nearby_rows = []
        if player_id:
            self_rows = connection.execute(
                common + " SELECT * FROM ranked WHERE player_id = ? ORDER BY row_position",
                board_parameters + (player_id,),
            ).fetchall()
            if self_rows:
                if selected_assistance != "all" or requested_overwashed is None:
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
                first = max(1, int(self_row["row_position"]) - window_size // 2)
                last = first + window_size - 1
                if last > total_players:
                    last = total_players
                    first = max(1, last - window_size + 1)
                nearby_rows = connection.execute(
                    common + " SELECT * FROM ranked WHERE row_position BETWEEN ? AND ? ORDER BY row_position",
                    board_parameters + (first, last),
                ).fetchall()
        level_row = connection.execute(
            """
            SELECT dlc_id, level_id, level_name, level_label
            FROM player_levels WHERE level_key = ? ORDER BY last_played DESC LIMIT 1
            """,
            (level_key,),
        ).fetchone()

    self_rank = 0 if self_row is None else int(self_row["rank_position"])
    total_players = int(total_row["total"])
    return {
        "level_key": level_key,
        "level_name": "" if level_row is None else level_row["level_name"],
        "level_label": "" if level_row is None else level_row["level_label"],
        "dlc_id": -1 if level_row is None else int(level_row["dlc_id"]),
        "level_id": -1 if level_row is None else int(level_row["level_id"]),
        "players": players,
        "metric": metric,
        "assistance": selected_assistance,
        "total_players": total_players,
        "self_rank": self_rank,
        "self_percentile": rank_percentile(self_rank, total_players),
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
