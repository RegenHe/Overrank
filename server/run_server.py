import os
from pathlib import Path

import uvicorn


def load_root_environment() -> None:
    env_file = Path(__file__).resolve().parents[1] / ".env"
    if not env_file.is_file():
        return
    for raw_line in env_file.read_text(encoding="utf-8").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        key = key.strip()
        value = value.strip().strip('"').strip("'")
        if key:
            os.environ.setdefault(key, value)


if __name__ == "__main__":
    load_root_environment()
    uvicorn.run(
        "overrank_server.app:app",
        host=os.environ.get("OVERRANK_HOST", "127.0.0.1"),
        port=int(os.environ.get("OVERRANK_PORT", "3005")),
        reload=False,
    )
