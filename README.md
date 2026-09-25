# Overrank

Overrank is a lightweight Overcooked! 2 community leaderboard and room browser made of a BepInEx 5 client and a Python FastAPI server.

- `mod/` contains the game client.
- `server/` contains the leaderboard API.

The Popular page shows the ten most-played levels from the previous seven days.

The default development server URL is `http://127.0.0.1:3005`.

## Shared environment

Copy `.env.example` to `.env` and edit the server settings.

## Rooms

The in-game panel includes six-digit rooms, optional passwords, Steam-lobby joining, temporary chat, member lists and host kicking.

## Level identity

The visible level name is presentation only and is never used as the leaderboard key.

- Official levels use the game's DLC ID plus internal level ID.
- OC2DIYLevel maps use the level-set UID plus scene name.
- Other custom loaders use a fingerprint of the active scene and level configuration.

Player count is stored as a separate ranking dimension.
