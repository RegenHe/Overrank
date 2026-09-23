from datetime import datetime, timezone
from typing import Literal
from uuid import UUID

from pydantic import BaseModel, Field, field_validator


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


Metric = Literal["score", "dishes"]
Assistance = Literal["all", "unassisted", "assisted"]
