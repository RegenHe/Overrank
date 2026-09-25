import re
from datetime import datetime, timezone
from typing import Literal
from uuid import UUID

from pydantic import BaseModel, Field, field_validator


RICH_TEXT_TAG = re.compile(r"</?[A-Za-z][^<>]*>")


def plain_player_name(value: object) -> str:
    text = RICH_TEXT_TAG.sub("", "" if value is None else str(value))
    cleaned = "".join(
        character for character in text.strip()
        if character >= " " and character != "\x7f"
    )
    return cleaned or "Unknown"


def plain_text(value: object, fallback: str = "") -> str:
    text = RICH_TEXT_TAG.sub("", "" if value is None else str(value))
    cleaned = "".join(
        character for character in text.strip()
        if character >= " " and character != "\x7f"
    )
    return cleaned or fallback


class Submission(BaseModel):
    submission_id: str = Field(min_length=32, max_length=64)
    player_id: str = Field(min_length=16, max_length=128)
    player_name: str = Field(min_length=1, max_length=64)
    level_uid: str = Field(min_length=16, max_length=128, pattern=r"^[A-Za-z0-9_-]+$")
    dlc_id: int = Field(ge=-1, le=1000)
    level_id: int = Field(ge=-1, le=10000)
    level_name: str = Field(min_length=1, max_length=128)
    level_label: str = Field(default="", max_length=128)
    player_count: int = Field(ge=1, le=4)
    score: int = Field(ge=-100000, le=10000000)
    dishes: int = Field(ge=0, le=10000)
    stars: int = Field(ge=0, le=4)
    mod_version: str = Field(min_length=1, max_length=32)
    overwashed_used: bool = False
    overwashed_version: str = Field(default="", max_length=32)
    completed_at: str = Field(min_length=10, max_length=64)
    client_id: str = Field(default="", max_length=128)
    lobby_key: str = Field(default="", max_length=128)
    attempt_nonce: str = Field(default="", max_length=64)
    round_id: str = Field(default="", max_length=64)

    @field_validator("player_name", mode="before")
    @classmethod
    def remove_player_name_markup(cls, value: object) -> str:
        return plain_player_name(value)

    @field_validator("submission_id")
    @classmethod
    def valid_submission_id(cls, value: str) -> str:
        return str(UUID(value))

    @field_validator("attempt_nonce")
    @classmethod
    def valid_attempt_nonce(cls, value: str) -> str:
        return "" if not value else str(UUID(value))

    @field_validator("round_id")
    @classmethod
    def valid_round_id(cls, value: str) -> str:
        return "" if not value else str(UUID(value))

    @field_validator("player_id", "player_name", "level_name", "level_label", "mod_version")
    @classmethod
    def clean_text(cls, value: str) -> str:
        cleaned = "".join(character for character in value.strip() if character >= " " and character != "\x7f")
        return cleaned or "Unknown"

    @field_validator("overwashed_version")
    @classmethod
    def clean_optional_text(cls, value: str) -> str:
        return "".join(character for character in value.strip() if character >= " " and character != "\x7f")

    @field_validator("completed_at")
    @classmethod
    def valid_completed_at(cls, value: str) -> str:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
        if parsed.tzinfo is None:
            parsed = parsed.replace(tzinfo=timezone.utc)
        return parsed.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")


class Presence(BaseModel):
    client_id: str = Field(min_length=16, max_length=128)
    player_id: str = Field(min_length=16, max_length=128)
    lobby_key: str = Field(default="", max_length=128)
    level_uid: str = Field(default="", max_length=128)
    attempt_nonce: str = Field(default="", max_length=64)
    round_id: str = Field(default="", max_length=64)
    local_players: int = Field(ge=1, le=4)
    in_level: bool = False
    mod_version: str = Field(min_length=1, max_length=32)

    @field_validator("attempt_nonce")
    @classmethod
    def valid_presence_nonce(cls, value: str) -> str:
        return "" if not value else str(UUID(value))

    @field_validator("round_id")
    @classmethod
    def valid_presence_round_id(cls, value: str) -> str:
        return "" if not value else str(UUID(value))


class RoundJoin(BaseModel):
    client_id: str = Field(min_length=16, max_length=128)
    player_id: str = Field(min_length=16, max_length=128)
    lobby_key: str = Field(min_length=16, max_length=128)
    level_uid: str = Field(min_length=16, max_length=128, pattern=r"^[A-Za-z0-9_-]+$")
    player_count: int = Field(ge=1, le=4)
    attempt_nonce: str = Field(min_length=32, max_length=64)
    overwashed_used: bool = False
    overwashed_version: str = Field(default="", max_length=32)

    @field_validator("attempt_nonce")
    @classmethod
    def valid_round_nonce(cls, value: str) -> str:
        return str(UUID(value))


class RoundAssistance(BaseModel):
    client_id: str = Field(min_length=16, max_length=128)
    attempt_nonce: str = Field(min_length=32, max_length=64)
    overwashed_version: str = Field(default="", max_length=32)

    @field_validator("attempt_nonce")
    @classmethod
    def valid_assistance_nonce(cls, value: str) -> str:
        return str(UUID(value))


class RoomCreate(BaseModel):
    client_id: str = Field(min_length=16, max_length=128)
    player_id: str = Field(min_length=16, max_length=128)
    player_name: str = Field(min_length=1, max_length=64)
    title: str = Field(min_length=1, max_length=48)
    description: str = Field(default="", max_length=160)
    password: str = Field(default="", max_length=32)
    lobby_id: str = Field(min_length=16, max_length=20, pattern=r"^[0-9]+$")
    game_player_count: int = Field(ge=1, le=4)
    game_player_limit: int = Field(default=4, ge=1, le=4)
    status: Literal["lobby", "playing"] = "lobby"

    @field_validator("player_name", mode="before")
    @classmethod
    def clean_player_name(cls, value: object) -> str:
        return plain_player_name(value)

    @field_validator("title", mode="before")
    @classmethod
    def clean_title(cls, value: object) -> str:
        return plain_text(value, "Room")

    @field_validator("description", "password", mode="before")
    @classmethod
    def clean_optional_room_text(cls, value: object) -> str:
        return plain_text(value)


class RoomJoin(BaseModel):
    client_id: str = Field(min_length=16, max_length=128)
    player_id: str = Field(min_length=16, max_length=128)
    player_name: str = Field(min_length=1, max_length=64)
    password: str = Field(default="", max_length=32)

    @field_validator("player_name", mode="before")
    @classmethod
    def clean_join_name(cls, value: object) -> str:
        return plain_player_name(value)

    @field_validator("password", mode="before")
    @classmethod
    def clean_join_password(cls, value: object) -> str:
        return plain_text(value)


class RoomHeartbeat(BaseModel):
    client_id: str = Field(min_length=16, max_length=128)
    player_id: str = Field(min_length=16, max_length=128)
    player_name: str = Field(min_length=1, max_length=64)
    host_token: str = Field(default="", max_length=64)
    lobby_id: str = Field(default="", max_length=20, pattern=r"^[0-9]*$")
    game_player_count: int = Field(default=1, ge=1, le=4)
    game_player_limit: int = Field(default=4, ge=1, le=4)
    status: Literal["lobby", "playing"] = "lobby"
    last_message_id: int = Field(default=0, ge=0)

    @field_validator("player_name", mode="before")
    @classmethod
    def clean_heartbeat_name(cls, value: object) -> str:
        return plain_player_name(value)


class RoomMessage(BaseModel):
    client_id: str = Field(min_length=16, max_length=128)
    player_id: str = Field(min_length=16, max_length=128)
    player_name: str = Field(min_length=1, max_length=64)
    text: str = Field(min_length=1, max_length=240)

    @field_validator("player_name", mode="before")
    @classmethod
    def clean_message_name(cls, value: object) -> str:
        return plain_player_name(value)

    @field_validator("text", mode="before")
    @classmethod
    def clean_message_text(cls, value: object) -> str:
        return plain_text(value)


class RoomLeave(BaseModel):
    client_id: str = Field(min_length=16, max_length=128)
    host_token: str = Field(default="", max_length=64)


class RoomKick(BaseModel):
    client_id: str = Field(min_length=16, max_length=128)
    host_token: str = Field(min_length=64, max_length=64)
    target_client_id: str = Field(min_length=16, max_length=128)


Metric = Literal["score", "dishes"]
Assistance = Literal["all", "unassisted", "assisted"]
