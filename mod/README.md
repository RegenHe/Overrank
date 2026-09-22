# Overrank Mod

BepInEx 5 client for the Overrank community leaderboard.

- Automatically records cooperative level results.
- Uploads score, successful dishes, player count, level identity and player identity.
- Keeps failed uploads in `BepInEx/config/Overrank.pending.json` and retries later.
- Opens the leaderboard from the ranking icon near the top-right corner.
- Supports official levels and OC2DIYLevel package UIDs; unknown custom loaders use a stable scene/config fingerprint.

Configure the server in `BepInEx/config/local.overcooked2.overrank.cfg` after the first launch.
