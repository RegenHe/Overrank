import hashlib
import hmac
import os
import secrets
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
    RoomCreate,
    RoomHeartbeat,
    RoomJoin,
    RoomKick,
    RoomLeave,
    RoomMessage,
    Submission,
    plain_player_name,
)


PRESENCE_TTL_SECONDS = 150
ROUND_TTL_SECONDS = 20 * 60
ROOM_TTL_SECONDS = 65
ROOM_MEMBER_TTL_SECONDS = 65
ROOM_MESSAGE_LIMIT = 100
ROOM_LIST_LIMIT = 100
ROOM_LIST_MIN_INTERVAL_SECONDS = 1.0
LEADERBOARD_MIN_INTERVAL_SECONDS = 1.0
LOBBY_MESSAGE_TTL_SECONDS = 4 * 60 * 60
LOBBY_MESSAGE_RATE_LIMIT = 10
LOBBY_MESSAGE_RATE_WINDOW_SECONDS = 60
_presence_lock = threading.Lock()
_presence: dict[str, dict] = {}
_round_lock = threading.Lock()
_rounds: dict[str, dict] = {}
_active_round_by_lobby: dict[str, str] = {}
_attempt_rounds: dict[tuple[str, str], str] = {}
_room_lock = threading.Lock()
_rooms: dict[str, dict] = {}
_room_catalog: dict[str, int] = {"revision": 1}
_room_list_request_times: dict[str, float] = {}
_room_list_rate_cleanup_at = 0.0
_leaderboard_rate_lock = threading.Lock()
_leaderboard_request_times: dict[str, float] = {}
_leaderboard_rate_cleanup_at = 0.0
_lobby_chat: dict[str, object] = {"next_message_id": 1, "messages": []}
_lobby_message_times: dict[str, list[float]] = {}


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


def _password_hash(password: str) -> str:
    return hashlib.sha256(password.encode("utf-8")).hexdigest() if password else ""


def _new_room_id() -> str:
    for _ in range(32):
        room_id = str(secrets.randbelow(900_000) + 100_000)
        if room_id not in _rooms:
            return room_id
    raise HTTPException(status_code=503, detail="Could not allocate a room ID")


def _purge_rooms(now: float) -> None:
    expired = [
        room_id
        for room_id, state in _rooms.items()
        if state["host_seen_at"] < now - ROOM_TTL_SECONDS
    ]
    for room_id in expired:
        _rooms.pop(room_id, None)
    changed = bool(expired)
    for state in _rooms.values():
        stale = [
            client_id
            for client_id, member in state["members"].items()
            if member["seen_at"] < now - ROOM_MEMBER_TTL_SECONDS
            and client_id != state["host_client_id"]
        ]
        for client_id in stale:
            state["members"].pop(client_id, None)
            changed = True
    if changed:
        _bump_room_revision()


def _bump_room_revision() -> None:
    _room_catalog["revision"] += 1


def _purge_lobby_chat(now: float) -> None:
    cutoff = now - LOBBY_MESSAGE_TTL_SECONDS
    messages = _lobby_chat["messages"]
    first_live = 0
    while first_live < len(messages) and messages[first_live]["sent_at"] <= cutoff:
        first_live += 1
    if first_live:
        del messages[:first_live]

    stale_clients = []
    rate_cutoff = now - LOBBY_MESSAGE_RATE_WINDOW_SECONDS
    for client_id, sent_times in _lobby_message_times.items():
        recent = [sent_at for sent_at in sent_times if sent_at > rate_cutoff]
        if recent:
            _lobby_message_times[client_id] = recent
        else:
            stale_clients.append(client_id)
    for client_id in stale_clients:
        _lobby_message_times.pop(client_id, None)


def _lobby_chat_response(after_id: int, now: float) -> dict:
    _purge_lobby_chat(now)
    messages = list(_lobby_chat["messages"])
    latest_id = messages[-1]["message_id"] if messages else 0
    oldest_id = messages[0]["message_id"] if messages else 0
    reset = after_id > latest_id
    return {
        "messages": [
            message
            for message in messages
            if reset or message["message_id"] > after_id
        ],
        "latest_message_id": latest_id,
        "oldest_message_id": oldest_id,
        "reset": reset,
    }


def _room_member(payload: RoomCreate | RoomJoin | RoomHeartbeat, now: float) -> dict:
    return {
        "client_id": payload.client_id,
        "player_id": payload.player_id,
        "player_name": plain_player_name(payload.player_name),
        "seen_at": now,
    }


def _room_response(
    state: dict,
    include_lobby: bool = False,
    include_details: bool = True,
    viewer_client_id: str = "",
    messages_after_id: int | None = None,
) -> dict:
    members = list(state["members"].values())
    response = {
        "room_id": state["room_id"],
        "title": state["title"],
        "description": state["description"],
        "locked": bool(state["password_hash"]),
        "game_player_count": state["game_player_count"],
        "game_player_limit": state["game_player_limit"],
        "status": state["status"],
        "host_player_name": state["host_player_name"],
        "member_count": len(members),
        "is_owner": bool(viewer_client_id) and viewer_client_id == state["host_client_id"],
    }
    if include_details:
        members.sort(
            key=lambda member: (
                member["client_id"] != state["host_client_id"],
                member["joined_at"],
            )
        )
        response.update(
            {
                "members": [
                    {
                        "client_id": member["client_id"],
                        "player_id": member["player_id"],
                        "player_name": member["player_name"],
                        "is_host": member["client_id"] == state["host_client_id"],
                    }
                    for member in members
                ],
                "messages": [
                    message
                    for message in state["messages"]
                    if messages_after_id is None or message["message_id"] > messages_after_id
                ],
                "lobby_id": state["lobby_id"] if include_lobby else "",
                "host_token": "",
            }
        )
    return response


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


@app.get("/api/v1/rooms", dependencies=[Depends(require_api_key)])
def list_rooms(
    client_id: str = Query(default="", max_length=128),
    after_message_id: int = Query(default=0, ge=0),
    room_revision: int = Query(default=0, ge=0),
) -> dict:
    global _room_list_rate_cleanup_at
    with _room_lock:
        now = time.time()
        _purge_rooms(now)
        viewer_client_id = client_id if isinstance(client_id, str) else ""
        if viewer_client_id:
            previous_request = _room_list_request_times.get(viewer_client_id, 0.0)
            if now - previous_request < ROOM_LIST_MIN_INTERVAL_SECONDS:
                raise HTTPException(status_code=429, detail="Room list refresh rate limit exceeded")
            _room_list_request_times[viewer_client_id] = now
            if now >= _room_list_rate_cleanup_at:
                stale_refresh_clients = [
                    key
                    for key, requested_at in _room_list_request_times.items()
                    if requested_at < now - ROOM_TTL_SECONDS
                ]
                for key in stale_refresh_clients:
                    _room_list_request_times.pop(key, None)
                _room_list_rate_cleanup_at = now + ROOM_TTL_SECONDS
        message_cursor = after_message_id if isinstance(after_message_id, int) else 0
        known_revision = room_revision if isinstance(room_revision, int) else 0
        current_revision = _room_catalog["revision"]
        rooms_changed = known_revision != current_revision
        rooms = []
        if rooms_changed:
            rooms = sorted(
                _rooms.values(),
                key=lambda state: (
                    state["status"] == "playing",
                    -state["created_at"],
                    state["title"].casefold(),
                ),
            )[:ROOM_LIST_LIMIT]
        response = {
            "rooms": [
                _room_response(
                    state,
                    include_details=False,
                    viewer_client_id=viewer_client_id,
                )
                for state in rooms
            ],
            "room_revision": current_revision,
            "rooms_changed": rooms_changed,
        }
        response.update(_lobby_chat_response(message_cursor, now))
        return response


@app.post("/api/v1/rooms", dependencies=[Depends(require_api_key)])
def create_room(payload: RoomCreate) -> dict:
    now = time.time()
    with _room_lock:
        _purge_rooms(now)
        if any(payload.client_id in state["members"] for state in _rooms.values()):
            raise HTTPException(status_code=409, detail="Leave the current room before creating another")

        room_id = _new_room_id()
        host_token = uuid4().hex + uuid4().hex
        host = _room_member(payload, now)
        host["joined_at"] = now
        state = {
            "room_id": room_id,
            "title": payload.title,
            "description": payload.description,
            "password_hash": _password_hash(payload.password),
            "lobby_id": payload.lobby_id,
            "host_client_id": payload.client_id,
            "host_player_name": payload.player_name,
            "host_token": host_token,
            "game_player_count": min(payload.game_player_count, payload.game_player_limit),
            "game_player_limit": payload.game_player_limit,
            "status": payload.status,
            "created_at": now,
            "host_seen_at": now,
            "members": {payload.client_id: host},
            "kicked_client_ids": set(),
            "kicked_player_ids": set(),
            "messages": [],
            "next_message_id": 1,
        }
        _rooms[room_id] = state
        _bump_room_revision()
        response = _room_response(state, include_lobby=True)
        response["host_token"] = host_token
        return response


@app.post("/api/v1/rooms/{room_id}/join", dependencies=[Depends(require_api_key)])
def join_room(room_id: str, payload: RoomJoin) -> dict:
    now = time.time()
    with _room_lock:
        _purge_rooms(now)
        state = _rooms.get(room_id)
        if state is None:
            raise HTTPException(status_code=404, detail="Room not found")
        host_member = state["members"].get(state["host_client_id"])
        if (payload.client_id == state["host_client_id"]
                or (host_member is not None and payload.player_id == host_member["player_id"])):
            raise HTTPException(status_code=409, detail="You already own this room")
        if (payload.client_id in state["kicked_client_ids"]
                or payload.player_id in state["kicked_player_ids"]):
            raise HTTPException(status_code=403, detail="You were kicked from the room")
        if state["status"] == "playing":
            raise HTTPException(status_code=409, detail="The game has already started")
        if state["game_player_count"] >= state["game_player_limit"]:
            raise HTTPException(status_code=409, detail="The game lobby is full")
        expected = state["password_hash"]
        supplied = _password_hash(payload.password)
        if expected and not hmac.compare_digest(expected, supplied):
            raise HTTPException(status_code=403, detail="Incorrect room password")
        for other_id, other in _rooms.items():
            if other_id == room_id or payload.client_id not in other["members"]:
                continue
            if other["host_client_id"] == payload.client_id:
                raise HTTPException(status_code=409, detail="Close your hosted room before joining another")
            other["members"].pop(payload.client_id, None)
        member = state["members"].get(payload.client_id)
        is_new_member = member is None
        joined_at = now if member is None else member["joined_at"]
        member = _room_member(payload, now)
        member["joined_at"] = joined_at
        state["members"][payload.client_id] = member
        if is_new_member:
            _bump_room_revision()
        return _room_response(state, include_lobby=True)


@app.get("/api/v1/chat/lobby/messages", dependencies=[Depends(require_api_key)])
def lobby_messages(after_id: int = Query(default=0, ge=0)) -> dict:
    with _room_lock:
        # Direct unit-test calls receive FastAPI's Query object rather than a
        # parsed integer; HTTP requests always provide an int.
        cursor = after_id if isinstance(after_id, int) else 0
        return _lobby_chat_response(cursor, time.time())


@app.post("/api/v1/chat/lobby/messages", dependencies=[Depends(require_api_key)])
def post_lobby_message(payload: RoomMessage) -> dict:
    with _room_lock:
        now = time.time()
        _purge_lobby_chat(now)
        recent = [
            sent_at
            for sent_at in _lobby_message_times.get(payload.client_id, [])
            if sent_at > now - LOBBY_MESSAGE_RATE_WINDOW_SECONDS
        ]
        if len(recent) >= LOBBY_MESSAGE_RATE_LIMIT:
            raise HTTPException(status_code=429, detail="Lobby chat rate limit exceeded")
        recent.append(now)
        _lobby_message_times[payload.client_id] = recent
        message = {
            "message_id": int(_lobby_chat["next_message_id"]),
            "player_id": payload.player_id,
            "player_name": payload.player_name,
            "text": payload.text,
            "sent_at": int(now),
        }
        _lobby_chat["next_message_id"] = int(_lobby_chat["next_message_id"]) + 1
        messages = _lobby_chat["messages"]
        messages.append(message)
        if len(messages) > ROOM_MESSAGE_LIMIT:
            del messages[:-ROOM_MESSAGE_LIMIT]
        return {
            "messages": [message],
            "latest_message_id": message["message_id"],
            "oldest_message_id": messages[0]["message_id"],
            "reset": False,
        }


@app.post("/api/v1/rooms/{room_id}/heartbeat", dependencies=[Depends(require_api_key)])
def room_heartbeat(room_id: str, payload: RoomHeartbeat) -> dict:
    now = time.time()
    with _room_lock:
        _purge_rooms(now)
        state = _rooms.get(room_id)
        if state is None:
            raise HTTPException(status_code=404, detail="Room not found")
        member = state["members"].get(payload.client_id)
        if member is None:
            if (payload.client_id in state["kicked_client_ids"]
                    or payload.player_id in state["kicked_player_ids"]):
                raise HTTPException(status_code=403, detail="You were kicked from the room")
            raise HTTPException(status_code=409, detail="Join the room before sending a heartbeat")
        joined_at = member["joined_at"]
        member = _room_member(payload, now)
        member["joined_at"] = joined_at
        state["members"][payload.client_id] = member

        if payload.client_id == state["host_client_id"]:
            if not payload.host_token or not hmac.compare_digest(state["host_token"], payload.host_token):
                raise HTTPException(status_code=403, detail="Invalid room host token")
            if payload.lobby_id and payload.lobby_id != state["lobby_id"]:
                raise HTTPException(status_code=409, detail="The hosted Steam lobby changed")
            visible_before = (
                state["host_player_name"],
                state["game_player_limit"],
                state["game_player_count"],
                state["status"],
            )
            state["host_seen_at"] = now
            state["host_player_name"] = payload.player_name
            state["game_player_limit"] = payload.game_player_limit
            state["game_player_count"] = min(payload.game_player_count, payload.game_player_limit)
            state["status"] = payload.status
            visible_after = (
                state["host_player_name"],
                state["game_player_limit"],
                state["game_player_count"],
                state["status"],
            )
            if visible_after != visible_before:
                _bump_room_revision()
        return _room_response(
            state,
            include_lobby=True,
            messages_after_id=payload.last_message_id,
        )


@app.post("/api/v1/rooms/{room_id}/messages", dependencies=[Depends(require_api_key)])
def post_room_message(room_id: str, payload: RoomMessage) -> dict:
    now = time.time()
    with _room_lock:
        _purge_rooms(now)
        state = _rooms.get(room_id)
        if state is None:
            raise HTTPException(status_code=404, detail="Room not found")
        member = state["members"].get(payload.client_id)
        if member is None:
            raise HTTPException(status_code=409, detail="Join the room before chatting")
        member["seen_at"] = now
        member["player_name"] = payload.player_name
        message = {
            "message_id": state["next_message_id"],
            "player_id": payload.player_id,
            "player_name": payload.player_name,
            "text": payload.text,
            "sent_at": int(now),
        }
        state["next_message_id"] += 1
        state["messages"].append(message)
        if len(state["messages"]) > ROOM_MESSAGE_LIMIT:
            del state["messages"][:-ROOM_MESSAGE_LIMIT]
        return _room_response(
            state,
            include_lobby=True,
            messages_after_id=message["message_id"] - 1,
        )


@app.post("/api/v1/rooms/{room_id}/leave", dependencies=[Depends(require_api_key)])
def leave_room(room_id: str, payload: RoomLeave) -> dict:
    with _room_lock:
        _purge_rooms(time.time())
        state = _rooms.get(room_id)
        if state is None:
            return {"left": True, "room_closed": True}
        if payload.client_id == state["host_client_id"]:
            if not payload.host_token or not hmac.compare_digest(state["host_token"], payload.host_token):
                raise HTTPException(status_code=403, detail="Invalid room host token")
            _rooms.pop(room_id, None)
            _bump_room_revision()
            return {"left": True, "room_closed": True}
        if state["members"].pop(payload.client_id, None) is not None:
            _bump_room_revision()
        return {"left": True, "room_closed": False}


@app.post("/api/v1/rooms/{room_id}/kick", dependencies=[Depends(require_api_key)])
def kick_room_member(room_id: str, payload: RoomKick) -> dict:
    with _room_lock:
        _purge_rooms(time.time())
        state = _rooms.get(room_id)
        if state is None:
            raise HTTPException(status_code=404, detail="Room not found")
        if payload.client_id != state["host_client_id"]:
            raise HTTPException(status_code=403, detail="Only the room host can kick members")
        if not hmac.compare_digest(state["host_token"], payload.host_token):
            raise HTTPException(status_code=403, detail="Invalid room host token")
        if payload.target_client_id == state["host_client_id"]:
            raise HTTPException(status_code=409, detail="The room host cannot be kicked")
        target = state["members"].pop(payload.target_client_id, None)
        if target is None:
            raise HTTPException(status_code=404, detail="Room member not found")
        state["kicked_client_ids"].add(payload.target_client_id)
        state["kicked_player_ids"].add(target["player_id"])
        _bump_room_revision()
        return _room_response(state, include_lobby=True)


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
    client_id: str = Query(default="", max_length=128),
) -> dict:
    global _leaderboard_rate_cleanup_at
    viewer_client_id = client_id if isinstance(client_id, str) else ""
    if viewer_client_id:
        now = time.monotonic()
        with _leaderboard_rate_lock:
            previous_request = _leaderboard_request_times.get(viewer_client_id, 0.0)
            if now - previous_request < LEADERBOARD_MIN_INTERVAL_SECONDS:
                raise HTTPException(status_code=429, detail="Leaderboard refresh rate limit exceeded")
            _leaderboard_request_times[viewer_client_id] = now
            if now >= _leaderboard_rate_cleanup_at:
                stale_refresh_clients = [
                    key
                    for key, requested_at in _leaderboard_request_times.items()
                    if requested_at < now - PRESENCE_TTL_SECONDS
                ]
                for key in stale_refresh_clients:
                    _leaderboard_request_times.pop(key, None)
                _leaderboard_rate_cleanup_at = now + PRESENCE_TTL_SECONDS
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
