# Overrank Server

Small FastAPI/SQLite leaderboard service for the Overrank BepInEx mod.

The database keeps at most two personal-best rows for each player, level and
player-count combination: the best score attempt and the best dishes attempt.
Inferior attempts update the last-played timestamp but are not retained.

## Local setup

```powershell
conda env create -f environment.yml
conda run -n overrank python -m pip install --index-url https://pypi.org/simple -r requirements.txt
conda run -n overrank python run_server.py
```

The default URL is `http://127.0.0.1:3005`. Open `/health` for a health check or `/docs` for the generated API documentation.

Configuration is loaded from the project-root `.env` and may be overridden by process environment variables. For a public server, use a trusted HTTPS reverse proxy and set a non-empty API key.
