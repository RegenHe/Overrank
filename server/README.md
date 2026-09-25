# Overrank Server

Small FastAPI/SQLite leaderboard service for the Overrank BepInEx mod.

The database keeps separate personal bests for unassisted and Overwashed-assisted runs. Each player, level, player-count and assistance combination retains the best score attempt and the best dishes attempt. Inferior attempts update the last-played timestamp but are not retained. Leaderboard responses expose both personal ranks and can center the nearby window on either the unassisted or assisted result.

## Local setup

```powershell
conda env create -f environment.yml
conda run -n overrank python -m pip install --index-url https://pypi.org/simple -r requirements.txt
conda run -n overrank python run_server.py
```

The default URL is `http://127.0.0.1:3005`. Open `/health` for a health check or `/docs` for the generated API documentation.

For public deployments, keep Uvicorn bound to `127.0.0.1` and place Caddy in
front of it. Caddy should terminate HTTPS, offer HTTP/2, and apply GZip to
responses of at least 1024 bytes. The application intentionally does not
compress responses itself.

Configuration is loaded from the project-root `.env` and may be overridden by process environment variables. For a public server, use a trusted HTTPS reverse proxy and set a non-empty API key.
