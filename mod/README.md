# Overrank Mod

BepInEx 5 client for the Overrank community leaderboard.

- Automatically records cooperative level results.
- Displays Simplified Chinese UI when the game language is Simplified Chinese; all other languages use English.
- Uploads score, successful dishes, player count, level identity and player identity.
- Keeps failed uploads in `BepInEx/config/Overrank.pending.json` and retries later.
- Opens the leaderboard from the ranking icon near the top-right corner.
- Supports official levels and OC2DIYLevel package UIDs; unknown custom loaders use a stable scene/config fingerprint.

The server endpoint is embedded in the DLL by `build.ps1` from the project-root `.env`; it is not exposed as a BepInEx configuration entry.
