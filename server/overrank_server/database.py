import os
import sqlite3
from contextlib import contextmanager
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Mapping


SCHEMA_VERSION = 2
METRICS = ("score", "dishes")


def database_path() -> Path:
    configured = os.environ.get("OVERRANK_DATABASE", "data/overrank.sqlite3")
    path = Path(configured).expanduser().resolve()
    path.parent.mkdir(parents=True, exist_ok=True)
    return path


@contextmanager
def connect():
    connection = sqlite3.connect(str(database_path()), timeout=15.0)
    connection.row_factory = sqlite3.Row
    connection.execute("PRAGMA foreign_keys = ON")
    connection.execute("PRAGMA journal_mode = WAL")
    connection.execute("PRAGMA synchronous = NORMAL")
    try:
        yield connection
        connection.commit()
    except Exception:
        connection.rollback()
        raise
    finally:
        connection.close()


def create_schema(connection: sqlite3.Connection) -> None:
    connection.executescript(
        """
        CREATE TABLE IF NOT EXISTS personal_bests (
            player_id TEXT NOT NULL,
            level_key TEXT NOT NULL,
            player_count INTEGER NOT NULL,
            metric TEXT NOT NULL CHECK (metric IN ('score', 'dishes')),
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
            received_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
            PRIMARY KEY (player_id, level_key, player_count, metric)
        );

        CREATE TABLE IF NOT EXISTS player_levels (
            player_id TEXT NOT NULL,
            level_key TEXT NOT NULL,
            player_name TEXT NOT NULL,
            dlc_id INTEGER NOT NULL,
            level_id INTEGER NOT NULL,
            level_name TEXT NOT NULL,
            level_label TEXT NOT NULL,
            last_played TEXT NOT NULL,
            last_submission_id TEXT NOT NULL,
            updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
            PRIMARY KEY (player_id, level_key)
        );

        CREATE INDEX IF NOT EXISTS idx_personal_bests_board_score
            ON personal_bests(level_key, player_count, metric, score DESC, dishes DESC, completed_at ASC);
        CREATE INDEX IF NOT EXISTS idx_personal_bests_board_dishes
            ON personal_bests(level_key, player_count, metric, dishes DESC, score DESC, completed_at ASC);
        CREATE INDEX IF NOT EXISTS idx_player_levels_recent
            ON player_levels(player_id, last_played DESC);
        """
    )
    connection.execute("PRAGMA user_version = " + str(SCHEMA_VERSION))


def initialise() -> None:
    with connect() as connection:
        create_schema(connection)


def _received_at(record: Mapping[str, Any]) -> str:
    value = record.get("received_at")
    if value:
        return str(value)
    return datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S")


def _is_better(record: Mapping[str, Any], current: sqlite3.Row, metric: str) -> bool:
    if metric == "dishes":
        candidate = (int(record["dishes"]), int(record["score"]))
        existing = (int(current["dishes"]), int(current["score"]))
    else:
        candidate = (int(record["score"]), int(record["dishes"]))
        existing = (int(current["score"]), int(current["dishes"]))
    if candidate != existing:
        return candidate > existing
    return str(record["completed_at"]) < str(current["completed_at"])


def save_submission(connection: sqlite3.Connection, record: Mapping[str, Any]) -> dict[str, bool]:
    """Update the two metric-specific personal bests and the last-played record."""
    connection.execute(
        """
        INSERT INTO player_levels (
            player_id, level_key, player_name, dlc_id, level_id,
            level_name, level_label, last_played, last_submission_id
        ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
        ON CONFLICT(player_id, level_key) DO UPDATE SET
            player_name = excluded.player_name,
            dlc_id = excluded.dlc_id,
            level_id = excluded.level_id,
            level_name = excluded.level_name,
            level_label = excluded.level_label,
            last_played = CASE
                WHEN excluded.last_played > player_levels.last_played
                    THEN excluded.last_played
                ELSE player_levels.last_played
            END,
            last_submission_id = CASE
                WHEN excluded.last_played > player_levels.last_played
                    THEN excluded.last_submission_id
                ELSE player_levels.last_submission_id
            END,
            updated_at = CURRENT_TIMESTAMP
        """,
        (
            record["player_id"],
            record["level_key"],
            record["player_name"],
            record["dlc_id"],
            record["level_id"],
            record["level_name"],
            record["level_label"],
            record["completed_at"],
            record["submission_id"],
        ),
    )

    updated: dict[str, bool] = {}
    for metric in METRICS:
        current = connection.execute(
            """
            SELECT score, dishes, completed_at
            FROM personal_bests
            WHERE player_id = ? AND level_key = ? AND player_count = ? AND metric = ?
            """,
            (record["player_id"], record["level_key"], record["player_count"], metric),
        ).fetchone()
        improved = current is None or _is_better(record, current, metric)
        updated[metric] = improved

        if improved:
            connection.execute(
                """
                INSERT INTO personal_bests (
                    player_id, level_key, player_count, metric, submission_id,
                    player_name, dlc_id, level_id, level_name, level_label,
                    score, dishes, stars, mod_version, completed_at, received_at
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                ON CONFLICT(player_id, level_key, player_count, metric) DO UPDATE SET
                    submission_id = excluded.submission_id,
                    player_name = excluded.player_name,
                    dlc_id = excluded.dlc_id,
                    level_id = excluded.level_id,
                    level_name = excluded.level_name,
                    level_label = excluded.level_label,
                    score = excluded.score,
                    dishes = excluded.dishes,
                    stars = excluded.stars,
                    mod_version = excluded.mod_version,
                    completed_at = excluded.completed_at,
                    received_at = excluded.received_at
                """,
                (
                    record["player_id"],
                    record["level_key"],
                    record["player_count"],
                    metric,
                    record["submission_id"],
                    record["player_name"],
                    record["dlc_id"],
                    record["level_id"],
                    record["level_name"],
                    record["level_label"],
                    record["score"],
                    record["dishes"],
                    record["stars"],
                    record["mod_version"],
                    record["completed_at"],
                    _received_at(record),
                ),
            )
        else:
            connection.execute(
                """
                UPDATE personal_bests
                SET player_name = ?, dlc_id = ?, level_id = ?,
                    level_name = ?, level_label = ?
                WHERE player_id = ? AND level_key = ? AND player_count = ? AND metric = ?
                """,
                (
                    record["player_name"],
                    record["dlc_id"],
                    record["level_id"],
                    record["level_name"],
                    record["level_label"],
                    record["player_id"],
                    record["level_key"],
                    record["player_count"],
                    metric,
                ),
            )
    return updated
