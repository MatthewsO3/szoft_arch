# szoft_arch

## Room system (create a room, join with the 6-digit code)

```bash
# server (dev login admin/admin comes from appsettings.Development.json)
dotnet run --project src/QuizGame.Server --launch-profile lan     # http://0.0.0.0:5084, reachable from phones on the LAN

# game master (Windows): log in, press "Create room", read the code
dotnet run --project src/QuizGame.Host                            # server URL: http://localhost:5084

# players: open http://<PC-LAN-IP>:5084 on a phone, enter code + name
```

Phones cannot reach `localhost`: use the PC's IPv4 address (`ipconfig`), allow port 5084 in Windows Firewall
(`netsh advfirewall firewall add rule name="QuizGame 5084" dir=in action=allow protocol=TCP localport=5084`),
and log in from the host app with that same URL so the join link shows it.

End-to-end check (needs a running server): `cd tests/smoke && npm install && node smoke.mjs`.
Endpoints, hub events and error codes: [docs/api-contract.md](docs/api-contract.md).

### Database (PostgreSQL via EF Core)

Game masters (PBKDF2-SHA256 password hashes), created sessions and joined players are stored in PostgreSQL.
Live room state stays in memory. With `ConnectionStrings__Default` unset, **Development** falls back to an
in-memory store (seed admin only, no history); every other environment requires it.

```bash
# once, then commit the generated src/QuizGame.Server/Migrations folder
dotnet tool install --global dotnet-ef
dotnet ef migrations add InitialCreate --project src/QuizGame.Server

# run against Postgres (docker compose up db), env as in .env.example
ConnectionStrings__Default="Host=localhost;Port=5432;Database=quizgame;Username=quizgame;Password=..." \
  dotnet run --project src/QuizGame.Server
```

Startup applies migrations, seeds `Seed__AdminUsername` / `Seed__AdminPassword` if that user does not exist
(an existing password is never overwritten) and closes sessions left open by a previous run. Until
`InitialCreate` is committed the server creates the schema directly and logs a warning.

### Players on other networks

Same Wi-Fi: use the `lan` launch profile and open `http://<PC-LAN-IP>:5084` (allow the port in Windows Firewall).
Different networks: run `cloudflared tunnel --url http://localhost:5084` and give players the printed
`https://….trycloudflare.com` URL (log into the host with that URL, or set `PublicBaseUrl`, so the join link/QR is correct).
For a stable demo use a VPS/domain and set `SITE_ADDRESS` for Caddy.

### Security (room system)

| Area | What is done |
|---|---|
| HTTPS / WSS | Public access only over HTTPS: Caddy (`proxy/Caddyfile`) in Docker, or a Cloudflare tunnel (`cloudflared tunnel --url http://localhost:5084`) which terminates TLS with a publicly trusted certificate. SignalR then runs over WSS. `X-Forwarded-Proto` is honoured, so join links are `https://…`. |
| Authentication | Game masters log in against the database; passwords are salted PBKDF2-SHA256 (600k iterations), compared in constant time, unknown users cost the same time as wrong passwords. Signed HS256 JWTs (alg pinned, iss/aud/exp checked); players get a token bound to one room. |
| Authorization | `GameMasterOnly` / `PlayerOnly` policies; a game master can only manage rooms they created; hub group membership comes from token claims, never from client input. |
| Input validation | Room code must be exactly 6 digits; player name 1–20 characters, no duplicates; room settings range-checked; password length capped; request bodies are typed DTOs. |
| SQL injection | All data access is EF Core (parameterised queries only, no raw SQL). |
| Abuse limits | Per-IP rate limits on login (10/min) and join/exists (120/min); room size cap; idle rooms are removed. |
| Secrets | JWT key, admin password and DB password come from environment variables (`.env`, git-ignored); the server refuses to start without them. |

### Question rounds (M2-A)

The host opens a round with the hub method `SendQuestion`, players answer with `SubmitAnswer`, `RoundClosed` carries the
ranking (see `docs/api-contract.md` §5). Rounds use the question bank (M2-B); the smoke test creates its own questions
through the REST API. After starting the server:

```
cd tests/smoke
npm ci
node rounds.mjs        # ~12 s, includes the deadline timer
```

New tables `Rounds` and `Answers` need a migration: `dotnet ef migrations add Rounds --project src/QuizGame.Server`.

