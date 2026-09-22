import os
import sqlite3
from contextlib import contextmanager
from pathlib import Path


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


def initialise() -> None:
    with connect() as connection:
        connection.executescript(
            """
            CREATE TABLE IF NOT EXISTS attempts (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                submission_id TEXT NOT NULL UNIQUE,
                player_id TEXT NOT NULL,
                player_name TEXT NOT NULL,
                dlc_id INTEGER NOT NULL,
                level_id INTEGER NOT NULL,
                level_key TEXT NOT NULL,
                level_name TEXT NOT NULL,
                level_label TEXT NOT NULL,
                player_count INTEGER NOT NULL,
                score INTEGER NOT NULL,
                dishes INTEGER NOT NULL,
                stars INTEGER NOT NULL,
                mod_version TEXT NOT NULL,
                completed_at TEXT NOT NULL,
                received_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE INDEX IF NOT EXISTS idx_attempts_board_score
                ON attempts(level_key, player_count, score DESC, dishes DESC);
            CREATE INDEX IF NOT EXISTS idx_attempts_board_dishes
                ON attempts(level_key, player_count, dishes DESC, score DESC);
            CREATE INDEX IF NOT EXISTS idx_attempts_player
                ON attempts(player_id, level_key, player_count);
            """
        )
