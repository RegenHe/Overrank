# Overrank

Overrank is a lightweight Overcooked! 2 community leaderboard made of a BepInEx 5 client and a Python FastAPI server.

- `mod/` contains the game client.
- `server/` contains the leaderboard API.

The default development server URL is `http://127.0.0.1:3005`.

## Shared environment

Copy `.env.example` to `.env` and edit the public host, bind host, port, database path and optional API key. `mod/build.ps1` reads the public host and port from this root `.env` and embeds the resulting default URL in the DLL. The Python launcher reads the same file at runtime.

`OVERRANK_PUBLIC_HOST` is the address players use. `OVERRANK_HOST` is the interface Uvicorn binds to; keep it at `127.0.0.1` behind a reverse proxy, or use `0.0.0.0` for direct network access.

## Level identity

The visible level name is presentation only and is never used as the leaderboard key.

- Official levels use the game's DLC ID plus internal level ID.
- OC2DIYLevel maps use the level-set UID plus scene name.
- Other custom loaders use a fingerprint of the active scene and level configuration.

Player count is stored as a separate ranking dimension. 
