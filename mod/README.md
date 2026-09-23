# Overrank Mod

BepInEx 5 client for the Overrank community leaderboard.

- Automatically records cooperative level results.
- Uploads score, successful dishes, player count, level identity and player identity.
- Keeps failed uploads in `BepInEx/config/Overrank.pending.json` and retries later.
- Opens the leaderboard from the ranking icon near the top-right corner.
- Marks Overwashed-assisted personal bests with a robot icon while preserving the player's unassisted bests.
- Uses an Overall / No bot / Bot cycling control to switch between three independently ranked leaderboards.
- Supports official levels and OC2DIYLevel package UIDs; unknown custom loaders use a stable scene/config fingerprint.

The server endpoint is embedded in the DLL by `build.ps1` from the project-root `.env`; it is not exposed as a BepInEx configuration entry.
