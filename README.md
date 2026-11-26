# szoft_arch

## Room system (create a room, join with the 6-digit code)

### Run the whole app (server + host + Cloudflare tunnel)

This is the full test setup: the API and database in Docker, the game master on WPF, and a Cloudflare
tunnel giving players a public HTTPS URL. The question bank is seeded automatically (42 questions, 5
categories) on the first database start.

** Clean up on new branch / new pull**
```bash
dotnet clean # optional
dotnet build
docker compose up --build # docker compose up -d you don't have to do this then 
```


**1. Server + database (Docker).** Copy `.env.example` to `.env` and fill the secrets, then:

```bash
docker compose up -d            # starts db + api; api is published on http://localhost:8080
docker compose logs -f api      # watch for "Seeded 42 question(s)" and "Now listening"
curl http://localhost:8080/health
```

The Caddy proxy is off by default (we use Cloudflare instead). Reset the database and re-seed with
`docker compose down -v` then `docker compose up -d`.

**2. Cloudflare tunnel.** Run it in its own terminal (keep it open for the whole session) and copy the
printed URL:

```bash
cloudflared tunnel --url http://localhost:8080
# → https://<random-name>.trycloudflare.com
```

Leave `PublicBaseUrl` **empty** in `.env`: the server reads Cloudflare's forwarded `Host`/`Proto` headers, so
join links and the QR code match the live tunnel URL automatically, with no api restart. Only set
`PublicBaseUrl` (and `docker compose up -d api`) if you use a stable named tunnel or custom domain.

**3. Game master (WPF, Windows).** Log in with the public URL so the host and players hit the same origin:

```bash
dotnet run --project src/QuizGame.Host     # server URL: https://<random-name>.trycloudflare.com
```

Log in as `admin` (password from `Seed__AdminPassword` in `.env`), press **Create room**, read the code.

**4. Players.** Open the `https://<random-name>.trycloudflare.com` URL (or scan the QR), enter the code and a
name. Cloudflare terminates TLS, so SignalR runs over WSS automatically.

> The quick tunnel name changes on every `cloudflared` run. For a stable demo URL use a named Cloudflare
> tunnel or a VPS/domain and set `PublicBaseUrl` to it.

### Same-Wi-Fi alternative (no tunnel, dev server)

For a quick LAN-only test you can skip Docker and the tunnel and run the dev server directly. Note this
**Development** fallback uses the in-memory store with an **empty question bank** unless you point it at
Postgres via `ConnectionStrings__Default`.

```bash
dotnet run --project src/QuizGame.Server --launch-profile lan   # http://0.0.0.0:5084
# phone: http://<PC-LAN-IP>:5084  (find the IP with ipconfig)
```

Allow the port in Windows Firewall and log into the host with that same `http://<PC-LAN-IP>:5084` URL so the
join link shows it:

```
netsh advfirewall firewall add rule name="QuizGame 5084" dir=in action=allow protocol=TCP localport=5084
```

End-to-end check (needs a running server): `cd tests/smoke && npm install && node smoke.mjs`.
Endpoints, hub events and error codes: [docs/api-contract.md](docs/api-contract.md).

### M2 part D: question clients

The phone client supports multiple-choice and estimation input, a server-synchronized
countdown, submission acknowledgment, and round results. The host's **Projector** button
opens a separate question window; F11 toggles fullscreen and Escape exits fullscreen.
The window shares the host connection and retains round state when closed/reopened.

Create a room, join with phones, select a question in the **Question bank** tab, and use
**Send selected question**. **Close round** ends it early; the projector reveals the answer
and ranking when the round closes. Reconnecting clients restore their question, accepted
answer status, or latest results from the server snapshot.

Client logic tests: `node --test tests/web/*.test.mjs` (Node 22.7+; no packages required).
To preview the phone question screens with sample events without starting the server, run
`node tests/web/preview.mjs` and open `http://127.0.0.1:5085`. This local fixture serves
the real question UI with sample data; it does not implement or test server rounds.
Manual integration checks: [M2 client checklist](docs/m2-client-checklist.md).

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

Different networks: run `cloudflared tunnel --url http://localhost:8080` (Docker stack) and give players the
printed `https://….trycloudflare.com` URL. Set `PublicBaseUrl` to it (and log into the host with that URL) so
the join link and QR code are correct. Full steps: "Run the whole app" above.

### Security (room system)

| Area | What is done |
|---|---|
| HTTPS / WSS | Public access over HTTPS via a Cloudflare tunnel (`cloudflared tunnel --url http://localhost:8080`), which terminates TLS with a publicly trusted certificate; SignalR then runs over WSS. `X-Forwarded-Proto` is honoured, so join links are `https://…`. A local Caddy proxy (`proxy/Caddyfile`, compose profile `caddy`) is available as an alternative but off by default. |
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

### Phases, map and base selection (M3-A)

The host starts the game with `StartGame` (Lobby → BaseSelection, generates the map), sends one estimation question as a
`BaseOrder` round, and every player then picks a base territory in ranking order (`PickRequested` / `PickTerritory`, random
assignment after `PickTimeoutSeconds`). `AdvancePhase(Conquest)` closes base selection. Hub methods and events:
`docs/api-contract.md` §6. Rounds are phase-gated now: `BaseOrder` only in BaseSelection, `Grab` / `Battle` only in Conquest.

```
cd tests/smoke
node baseselection.mjs   # ~10 s, Lobby -> BaseSelection -> Conquest -> Results, includes the pick timeout
node rounds.mjs          # plays the base selection first, then the question rounds
```

The cells of the map are stored in a new `Territories` table: `dotnet ef migrations add Territories --project src/QuizGame.Server`.
