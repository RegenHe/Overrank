# Overrank

Overrank is a lightweight Overcooked! 2 community leaderboard and room browser made of a BepInEx 5 client and a Python FastAPI server.

- `mod/` contains the game client.
- `server/` contains the leaderboard API.

The default development server URL is `http://127.0.0.1:3005`.

## Shared environment

Copy `.env.example` to `.env` and edit the public URL, bind host, port, database path and optional API key. `mod/build.ps1` embeds `OVERRANK_SERVER_URL` and the API key in the DLL, while the Python launcher reads the same file at runtime. Behind Caddy, keep `OVERRANK_HOST=127.0.0.1` and use `OVERRANK_PORT` only as the private upstream port. For an HTTP-only site, write the Caddy site address with an explicit `http://` prefix so Unity POST requests are not redirected.

`OVERRANK_PUBLIC_HOST` and `OVERRANK_SCHEME` are fallback build settings when no full server URL is supplied. `OVERRANK_HOST` is the interface Uvicorn binds to; keep it at `127.0.0.1` behind a reverse proxy, or use `0.0.0.0` for direct network access.

## Rooms

The in-game panel includes six-digit rooms, optional passwords, Steam-lobby joining, temporary chat, member lists and host kicking. Room and chat state is intentionally kept in memory only and disappears when the server restarts.

## Level identity

The visible level name is presentation only and is never used as the leaderboard key.

- Official levels use the game's DLC ID plus internal level ID.
- OC2DIYLevel maps use the level-set UID plus scene name.
- Other custom loaders use a fingerprint of the active scene and level configuration.

Player count is stored as a separate ranking dimension.
