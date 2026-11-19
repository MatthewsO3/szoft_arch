# Implementation Plan – Stratégiai kvízjáték

Course: Szoftverarchitektúrák (VIAUMA21) · Team: Kovács Donát, Nagy Tamás, Tasi Gergő, Czap Máté

This plan turns the requirement specification into a concrete, buildable design. Where the spec is silent or ambiguous, a **decision** is stated explicitly (marked `DECISION`) so the team can confirm or change it once instead of arguing about it mid-development. All decisions are collected again in section 16.

---

## 1. Goals and guiding principles

1. **Meet the mandated stack exactly**: ASP.NET Core Web API + SignalR, EF Core, PostgreSQL, Docker, WPF desktop host, mobile-friendly responsive web client with the SignalR JavaScript client, HTTPS/WSS, JWT auth.
2. **Keep everything else minimal.** Team preference is the smallest stack that satisfies the spec. Therefore:
   - No native mobile app; the player client is a static web app.
   - **No JS framework and no build step** for the player client: plain HTML + CSS + ES modules + the SignalR JS client, served by the backend from `wwwroot`. One origin means no CORS, no separate hosting, and one HTTPS certificate.
   - No CQRS/MediatR/microservices. A single backend project layered internally (see section 3).
3. **The server is the single source of truth.** All game state lives on the server; clients only send intents (join, answer, pick territory) and render state pushed by the server. Clients never compute outcomes.
4. **Server-side timing.** Answer deadlines and ordering ("fastest correct") are decided by server receive time, never by client clocks.
5. **Vertical slices first.** Get a thin end-to-end path (host creates room → player joins → one question → result) working early, then widen.

---

## 2. System overview

```
┌────────────────────────── Host PC (Windows 10/11) ──────────────────────────┐
│  WPF app (Game Master)                                                      │
│   ├─ Control window   (host only)                                           │
│   ├─ Question window  (projected)                                           │
│   └─ Map window       (projected)                                           │
│        │ REST (login, question bank CRUD, create room)  + SignalR (game)    │
└────────┼────────────────────────────────────────────────────────────────────┘
         │ HTTPS / WSS (JWT)
┌────────▼────────────────────── Docker host ─────────────────────────────────┐
│  [ASP.NET Core container]                  [PostgreSQL container]           │
│   ├─ REST controllers  (auth, questions,   ◄── EF Core ──►  quiz database   │
│   │   categories, rooms, results)                                           │
│   ├─ SignalR GameHub   (rooms = groups)                                     │
│   ├─ GameRoomManager   (in-memory live state)                               │
│   └─ wwwroot/          (player web app, static)                             │
└────────▲────────────────────────────────────────────────────────────────────┘
         │ HTTPS / WSS (JWT)
┌────────┴──────────── Players' phones (Android / iOS browsers) ──────────────┐
│  Player web app (HTML/CSS/JS + @microsoft/signalr)                          │
└─────────────────────────────────────────────────────────────────────────────┘
```

Three tiers as required: presentation (WPF + web), application/game logic (ASP.NET Core), data (PostgreSQL via EF Core).

---

## 3. Repository and solution layout

Monorepo, one Git repository.

```
szoft_arch/
├─ docker-compose.yml
├─ .env.example
├─ README.md
├─ docs/                      # architecture notes, API contract, diagrams
│   └─ implementation_plan.md
├─ src/
│  ├─ QuizGame.Contracts/     # class library: DTOs, enums, hub method names/event names
│  ├─ QuizGame.Server/        # ASP.NET Core Web API + SignalR + EF Core
│  │   ├─ Controllers/
│  │   ├─ Hubs/
│  │   ├─ Domain/             # pure game logic, no ASP.NET/EF dependencies
│  │   ├─ Services/           # GameRoomManager, TimerService, ScoringService...
│  │   ├─ Data/               # DbContext, entities, migrations, seed
│  │   ├─ Auth/
│  │   └─ wwwroot/            # player web app
│  │       ├─ index.html
│  │       ├─ css/
│  │       └─ js/
│  └─ QuizGame.Host/          # WPF app (net10.0-windows)
│      ├─ Views/ ViewModels/ Services/ Models/
└─ tests/
   ├─ QuizGame.Domain.Tests/  # xUnit: game rules, scoring, map, state machine
   └─ QuizGame.Server.Tests/  # integration tests (WebApplicationFactory + Testcontainers or SQLite-less in-memory)
```

**Why a `Contracts` library**: the WPF client and the server share DTOs, enums and hub method/event name constants, so the contract cannot silently drift. (The JS client mirrors the same names; they are documented in `docs/api-contract.md` and section 6 below.)

**Why `Domain/` inside the server project** (instead of a separate assembly): keeps the solution small, but the folder has a strict rule: *no references to ASP.NET, SignalR or EF Core*. That makes the game rules unit-testable and easy to explain in the architecture documentation. If the team prefers stricter separation, it can be moved to `QuizGame.Domain` at no real cost.

Target frameworks: **.NET 10 (LTS)** for all projects; WPF as `net10.0-windows`.

NuGet packages (kept small):

| Project | Packages |
|---|---|
| Server | `Microsoft.AspNetCore.SignalR` (built in), `Npgsql.EntityFrameworkCore.PostgreSQL`, `Microsoft.EntityFrameworkCore.Design`, `Microsoft.AspNetCore.Authentication.JwtBearer`, `BCrypt.Net-Next` (password hashing), `Swashbuckle.AspNetCore` (dev only) |
| Host (WPF) | `Microsoft.AspNetCore.SignalR.Client`, `CommunityToolkit.Mvvm`, `QRCoder` (QR code image), `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Http` |
| Tests | `xunit`, `FluentAssertions`, `Microsoft.AspNetCore.Mvc.Testing`, `Testcontainers.PostgreSql` |
| Player web | `@microsoft/signalr` (single file, vendored into `wwwroot/js/lib/`, no npm build) |

---

## 4. Domain model and game rules

### 4.1 Vocabulary (from the glossary)

Game master (Játékmester), Player (Játékos), Room (Szoba), Map (Térkép), Territory (Terület), Base (Bázis), Question bank (Kérdésbank), Multiple-choice question (Feleletválasztós), Estimation question (Becslős), Battle (Csata), Phase (Fázis), Projector (Kivetítő).

### 4.2 Phases (state machine)

```
Lobby ──► BaseSelection ──► Conquest ──► Results
```

Only the game master advances phases. Transitions are validated on the server (no skipping, no going back).

Inside a phase, there is a **question round** sub-state machine:

```
Idle ──(GM sends question)──► QuestionOpen ──(deadline OR all answered OR GM closes)──► Evaluating
  ▲                                                                                    │
  │                                                              ┌─────────────────────┘
  │                                                              ▼
  └──(territory assigned / round finished)◄── AwaitingPick (if winner must choose a territory) 
```

`DECISION D1` – Phase rules in detail:

**Lobby**
- Room exists with a 6-digit code. Players join with code + display name. Names are unique within a room (case-insensitive); duplicates are rejected.
- Host can kick players. Host starts the game when at least 2 players are connected (configurable minimum).

**BaseSelection (bázisfoglalás)**
- Map is generated according to player count (see 4.4).
- The host sends **one estimation question** (fixed for this phase; the host can optionally resend if the result is a total tie).
- Result: players are ranked by absolute distance to the correct number (ties broken by earlier server receive time). Players who did not answer rank last (in order of join).
- In ranking order, each player picks **one free territory as their base** (player UI: map picker; "Terület választása"). The server enforces: territory free, not yet taken, and (optional rule) not adjacent to another base. A pick timeout (default 20 s) auto-assigns a random valid territory so the game cannot stall.
- Base territories get a higher point value (`BaseValue`, default 300 vs. normal 100).

**Conquest (hódítás)**
Each round the host picks a question type:
- **Multiple-choice round** (territory grab for *free* territories): the server ranks correct answers by server receive time. The fastest correct player becomes the *picker* and chooses a free territory that is **adjacent to a territory they already own** (or any free territory if they have none, e.g. their base was lost). `DECISION D2`: the number of winners per round is configurable (`WinnersPerRound`, default 1; with N winners, picks happen in order of speed).
- **Estimation round as battle** (for *occupied* territories): the host selects (or the attacker nominates; `DECISION D3`: attacker nominates, host confirms) an attacker and a defender-owned territory adjacent to the attacker. Only attacker and defender receive the question; the closest guess wins the territory (ties → the defender keeps it).

Rule summary for ownership: at most one owner per territory; owner color shown on the map.

`DECISION D4` – **Conquest end condition**: host-controlled. The host ends Conquest manually, and the UI additionally suggests ending when all territories are owned or when a configured number of rounds (default 10) is reached.

**Results**
- Final standings sorted by score; ties share a rank. Map shown in final state. Results persisted to the DB.

### 4.3 Scoring

- `Score(player) = Σ value(territory)` over owned territories at any time. It is **derived** from current ownership (not accumulated), so losing a territory immediately reduces score. This matches the glossary: *"Értéke hozzájárul a játékos pontszámához."*
- Value: base = `BaseValue` (300), normal = `TerritoryValue` (100). Both are room settings.
- `DECISION D5`: no additional speed bonus points in v1 (speed only decides who wins the pick). Can be added later without model changes.
- Tiebreak on final score: more territories, then earlier base-selection rank.

### 4.4 Map

`DECISION D6` – Square grid, 4-neighbour adjacency.
- Size derived from player count: `cells ≈ max(24, players × 6)`, chosen as the nearest rectangular grid (e.g. 6×6 for ≤6 players, 6×8, 8×8, ...). Fixed presets keep it simple: `S 5×5`, `M 6×8`, `L 8×10`, host can override in room settings.
- Cell id = `"r{row}c{col}"` or an integer index; neighbours computed by `MapService`.
- Rendering is a plain grid of coloured rectangles (WPF `UniformGrid`/`Canvas`, web `<div>` grid or `<canvas>`). A hex or irregular "Honfoglaló"-like layout is a stretch goal (section 15); the model only needs `Territory { Id, Neighbours[] }` and is layout-agnostic.

### 4.5 Question types and evaluation

| | Multiple choice | Estimation |
|---|---|---|
| Answer | one of A–D | a number (decimal allowed) |
| Correct | exactly one option | one numeric value |
| Winner | correct and fastest (server receive time) | smallest `|guess − correct|`; tie → earliest |
| Used for | free-territory capture | base order, battles |
| Late answers | rejected | rejected |
| Re-answer | allowed until deadline? **No**: first answer is final (`DECISION D7`) | same |

Pure functions in `Domain/Evaluation/` take `(question, answers[], deadline)` and return an ordered ranking; no I/O.

---

## 5. Database design (PostgreSQL + EF Core)

Persistent data: users, question bank, and completed/ongoing game records. Live game state is held in memory (section 7) and **snapshotted** to the DB at phase transitions and round ends, which gives results history and crash tolerance without making every click a DB write.

### 5.1 Tables

```
GameMasters         Id (uuid PK), Username (unique), PasswordHash, CreatedAt
Categories          Id (int PK), Name (unique)
Questions           Id (uuid PK), CategoryId FK, Type (enum: MultipleChoice|Estimation),
                    Text, Difficulty (1–3), TimeLimitSeconds (default per question),
                    CorrectNumber (numeric, nullable; estimation only),
                    Unit (text, nullable), OwnerId FK→GameMasters (nullable = shared),
                    CreatedAt, IsActive
QuestionOptions     Id (uuid PK), QuestionId FK, Label ('A'..'D'), Text, IsCorrect
GameSessions        Id (uuid PK), RoomCode (char(6)), GameMasterId FK, Status (phase),
                    SettingsJson (map size, values, winners/round...), 
                    CreatedAt, StartedAt, EndedAt
Players             Id (uuid PK), SessionId FK, Name, ColorHex, JoinedAt,
                    BaseRank (int, nullable), FinalScore (int, nullable), FinalRank (int, nullable)
                    UNIQUE(SessionId, lower(Name))
Territories         Id (uuid PK), SessionId FK, Index (int), Row, Col, Value (int),
                    IsBase (bool), OwnerPlayerId FK (nullable)
                    UNIQUE(SessionId, Index)
Rounds              Id (uuid PK), SessionId FK, Seq (int), Phase, Kind (BaseOrder|Grab|Battle),
                    QuestionId FK, StartedAt, Deadline, ClosedAt,
                    AttackerId FK null, DefenderId FK null, TargetTerritoryId FK null,
                    WinnerPlayerId FK null
Answers             Id (uuid PK), RoundId FK, PlayerId FK, ChosenOption (char, null),
                    NumericValue (numeric, null), ReceivedAt (timestamptz),
                    IsCorrect (bool, null), Distance (numeric, null), Rank (int, null)
                    UNIQUE(RoundId, PlayerId)
```

### 5.2 Notes

- Use `uuid` PKs for entities created at runtime, `int` for small lookup tables.
- `RoomCode` is **not** globally unique forever; it must be unique among *active* sessions. Enforce with a partial unique index `WHERE "Status" <> 'Results'` (or `EndedAt IS NULL`).
- Constraint: a multiple-choice question has exactly 2–4 options with exactly one `IsCorrect`; an estimation question has `CorrectNumber` set. Validated in the service layer and with a DB `CHECK` for `CorrectNumber` nullability by type.
- Use EF Core migrations from day one; the Docker entrypoint applies pending migrations at startup (`Database.Migrate()`) – acceptable for a course project.
- **Seed data** (`Data/Seed.cs`): one default game master (`admin` / password from env var), ~5 categories and **at least 40 questions** (about 25 multiple choice, 15 estimation) so the game is demoable immediately. Keep the seed as a JSON file embedded in the project.

---

## 6. Backend API design

### 6.1 Authentication (JWT)

Two kinds of tokens, same signing key:

| Token | Issued to | Claims | Lifetime |
|---|---|---|---|
| **GM token** | game master after login | `sub`=gmId, `role`=GameMaster | 8 h |
| **Player token** | player after joining a room | `sub`=playerId, `role`=Player, `room`=roomCode | 12 h |

- Passwords hashed with BCrypt.
- Signing key, issuer, audience from environment variables (never committed).
- SignalR with JWT: the browser WebSocket cannot send headers, so tokens are passed via `accessTokenFactory` and read from the `access_token` query string in `JwtBearerEvents.OnMessageReceived` for paths under `/hubs`.
- Authorization policies: `GameMasterOnly`, `PlayerOnly`; hub methods carry `[Authorize(Policy = ...)]`.
- Registration of game masters: a `POST /api/auth/register` endpoint, optionally disabled by config (`AllowRegistration`) so only seeded users can host. The use case diagram has "Bejelentkezés" as an «include» of room creation and question-bank management, so login is mandatory for the host.

### 6.2 REST endpoints

All JSON, all under `/api`.

| Method | Route | Auth | Purpose |
|---|---|---|---|
| POST | `/auth/login` | – | GM login → GM token |
| POST | `/auth/register` | – (config) | Create GM account |
| GET | `/categories` | GM | List categories |
| POST/PUT/DELETE | `/categories[/{id}]` | GM | Manage categories |
| GET | `/questions?categoryId=&type=&difficulty=&search=&page=` | GM | Filtered, paged list |
| GET | `/questions/{id}` | GM | One question incl. options |
| POST | `/questions` | GM | Create (validated by type) |
| PUT | `/questions/{id}` | GM | Update |
| DELETE | `/questions/{id}` | GM | Soft delete (`IsActive=false`), so past rounds keep their reference |
| POST | `/rooms` | GM | Create room → `{ roomCode, joinUrl, sessionId }` |
| GET | `/rooms/{code}/exists` | – | Lightweight check used by the join page |
| POST | `/rooms/{code}/join` | – | Body `{ name }` → player token + player info |
| POST | `/rooms/{code}/rejoin` | Player token | Re-attach after disconnect (see 7.5) |
| GET | `/sessions/{id}/results` | GM | Final standings, rounds, answers |
| GET | `/health` | – | Liveness for Docker |

Error format: RFC 7807 `ProblemDetails`. Input validation with data annotations + FluentValidation-style checks in services (keep it dependency-free; plain code is fine).

### 6.3 SignalR hub: `/hubs/game`

A single `GameHub`. **Group strategy**: `room:{code}` (everybody), `room:{code}:gm` (host only), `room:{code}:players`. Group membership is established in `OnConnectedAsync` from the token claims, not from client arguments, so a player can never join someone else's room.

**Client → server methods**

| Method | Caller | Args | Effect |
|---|---|---|---|
| `HostAttach` | GM | `roomCode` | Host connection registers for room (also used by the 2 projector windows if they use their own connection – see 9.2) |
| `KickPlayer` | GM | `playerId` | Remove player |
| `UpdateSettings` | GM | `RoomSettings` | Only in Lobby |
| `StartGame` | GM | – | Lobby → BaseSelection, generates map |
| `AdvancePhase` | GM | `targetPhase` | Validates transition |
| `SendQuestion` | GM | `questionId`, `kind`, optional `attackerId`, `targetTerritoryId` | Opens a round |
| `CloseRound` | GM | – | Ends the round early |
| `ConfirmBattle` | GM | – | Confirms an attacker's nomination |
| `SubmitAnswer` | Player | `roundId`, `option` or `number` | Records answer |
| `PickTerritory` | Player | `territoryIndex` | Valid only when this player is the current picker |
| `NominateTarget` | Player | `territoryIndex` | Attacker nominates battle target (D3) |
| `Ping` | any | – | Latency / keepalive and clock sync |

**Server → client events**

| Event | Target | Payload |
|---|---|---|
| `RoomState` | caller on (re)connect | Full snapshot: phase, players, map, current round, scores, time offset |
| `PlayerJoined` / `PlayerLeft` / `PlayerConnectionChanged` | room | player summary |
| `PhaseChanged` | room | new phase |
| `MapUpdated` | room | changed territories only (diff) + scores |
| `QuestionStarted` | gm group | Full question incl. correct answer (host needs it) |
| `QuestionStarted` | players group | Question **without correct answer**, `roundId`, `serverDeadlineUtc`, options or number-input flag. For battles: sent only to the two involved players; others get `BattleAnnounced` (spectator info) |
| `AnswerCountChanged` | gm group | `answered/total` (progress on host UI, no answer content) |
| `RoundClosed` | room | ranking summary (names, times/distances), correct answer, who wins |
| `PickRequested` | the picker | list of valid territory indices, pick deadline |
| `PickWaiting` | others | "X is choosing…" |
| `ScoreboardUpdated` | room | sorted scores |
| `GameFinished` | room | final standings |
| `Error` | caller | code + message |

**Rule**: never send correct answers or other players' answers to player clients before `RoundClosed`.

The event/method names live as `const string`s in `QuizGame.Contracts.HubNames`, and the same list is copied into `wwwroot/js/hubNames.js`.

---

## 7. Server internals

### 7.1 Components

| Component | Responsibility |
|---|---|
| `GameRoomManager` (singleton) | Dictionary `roomCode → GameRoom`; creates/disposes rooms, code generation (6 digits, collision-checked, cryptographically random), cleanup of idle rooms |
| `GameRoom` (domain object) | Holds live state: settings, players, map, phase, current round. Exposes **intent methods** returning domain results/events; no I/O |
| `GameRoomRunner` (per room) | Serializes all commands for the room (single-reader `Channel<T>` or `SemaphoreSlim`) to avoid race conditions between simultaneous answers, timer expiry and host actions |
| `RoundTimerService` | Schedules deadline tasks (`Task.Delay` with `CancellationToken`) that enqueue a `CloseRound` command |
| `MapService` | Grid generation, adjacency, valid-pick computation |
| `EvaluationService` / `Domain.Evaluation` | Ranking of answers (pure) |
| `ScoringService` | Computes scores from ownership |
| `PersistenceService` | Writes snapshots/rounds/answers to DB via scoped `DbContext` (use `IDbContextFactory` because singletons cannot hold scoped contexts) |
| `QuestionService` | CRUD + filtering + random pick helpers |
| `ConnectionRegistry` | Maps `connectionId ↔ (room, role, playerId)` for presence tracking |

### 7.2 Concurrency model

All mutations of one room go through its **command queue**: the hub method enqueues a command; the runner processes commands one by one and returns the result. This removes the need for locks scattered over the code and makes "two players answer at the same millisecond" deterministic (order = enqueue order, plus recorded `ReceivedAt` stamped **at hub method entry**).

### 7.3 Timing

- When a round opens: `StartedAt = UtcNow`, `Deadline = StartedAt + timeLimit`.
- Clients receive `serverDeadlineUtc` and the server's current UTC; they compute an offset and show the countdown based on that, so slightly wrong phone clocks still display approximately right.
- The server accepts an answer if `ReceivedAt <= Deadline + grace` (grace default 300 ms for network latency). Anything later is rejected with `AnswerTooLate`.
- Timer expiry or "all players answered" triggers evaluation. The host can also close early.
- `DECISION D8`: ties on server receive time (practically impossible) are broken by queue order.

### 7.4 Persistence cadence

- On room creation: insert `GameSession`.
- On player join: insert `Player`.
- On map creation / after each territory change: update `Territories` (batched with the round).
- On round close: insert `Round` + `Answers` in one transaction.
- On game end: write final scores/ranks.
- If the server restarts mid-game, rooms are lost in v1 (`DECISION D9`); the DB still keeps what was recorded. Rehydration from DB is a stretch goal.

### 7.5 Reconnection ("Újracsatlakozás", «extend» of join)

- Player token + `playerId` are stored in the browser's `localStorage`.
- On page load the client tries `rejoin`; the server verifies the token, marks the player connected, re-adds the new connection to the groups and sends `RoomState`. If a question is open, the player gets the question again with the remaining time.
- Use SignalR `withAutomaticReconnect([0, 1000, 2000, 5000, 10000])`. On `onreconnected` the client calls `Rejoin`.
- A disconnected player is not removed; they are shown as "offline" on host UI. During base selection an offline player still gets auto-assigned after the pick timeout.
- The host reconnecting works identically (GM token + `HostAttach`), important because the WPF app can lose network.

---

## 8. Security and transport

- **HTTPS/WSS**: Kestrel with a certificate. Dev: `dotnet dev-certs`. Demo: either a self-signed cert trusted on the host + phones (annoying), or put a **reverse proxy** in the compose file (Caddy is the minimal option: automatic HTTPS if a domain is available, or `tls internal`). `DECISION D10`: use Caddy in `docker-compose.yml` for TLS termination; the app itself listens on HTTP inside the Docker network. For a local demo on a LAN, document how to install the Caddy local CA on phones, or use a tunnel (e.g. a quick free tunnel service) which provides valid HTTPS.
- JWT validation: issuer, audience, lifetime, signature; clock skew 1 min.
- Rate-limiting: simple per-connection throttle on `SubmitAnswer`, plus the built-in ASP.NET rate limiter on `/rooms/{code}/join` and `/auth/login` (prevent code guessing and brute-force).
- Room codes: 6 digits from `RandomNumberGenerator`; join attempts with wrong codes are throttled by IP.
- Input validation: names 1–20 chars, trimmed, HTML-escaped on render (use `textContent`, never `innerHTML` in the web client) to avoid XSS from player names.
- Secrets via environment variables / `.env` (git-ignored); `.env.example` committed.
- The player token does not grant any host method (policy-checked).

---

## 9. WPF host application (QuizGame.Host)

### 9.1 Architecture

MVVM with `CommunityToolkit.Mvvm`, DI via `Microsoft.Extensions.DependencyInjection`.

Services (singletons shared by all three windows):
- `ApiClient` – typed `HttpClient` wrapper for REST, attaches GM JWT.
- `GameConnection` – owns the single `HubConnection`, exposes C# events / observable state, handles reconnection.
- `GameStateStore` – the host-side mirror of the room state (players, map, round, scores). It is updated only from server events. The three windows **bind to this one store**, so there is one connection and consistent data across windows.
- `WindowManager` – opens/positions windows, remembers target monitor, supports "send to monitor N" and fullscreen toggle.
- `SettingsService` – server URL etc., stored in `%AppData%`.

### 9.2 Windows and screens

1. **Login window**: server URL, username, password → token. Remember last server URL.
2. **Control window (host only)** – tabs/sections:
   - *Question bank*: filterable DataGrid (category/type/difficulty/search), add/edit/delete dialog with type-specific form (options A–D with correct marker, or numeric answer + unit), time limit, category management.
   - *Room / Lobby*: "Create room" button, big room code, **QR code** (QRCoder, encodes `https://<host>/?code=123456`), join URL, live player list with connection status and a Kick button, room settings (map size, values, time limits, winners/round, min players).
   - *Game*: current phase and phase buttons ("Start game", "Next phase", "End game"), round panel (see below), scoreboard, round log.
   - *Question picker*: filter by category/type/difficulty → preview → **"Send question"** button. For battle rounds: shows the nominated attacker/target and a Confirm button. Shows `answered 7/12` live progress and a "Close round now" button.
   - *Results*: final standings, per-round history, option to export CSV.
3. **Question window (projected)**: big text, options A–D coloured tiles (or "Estimate the number" + unit), large countdown (progress bar + digits), after close: correct answer highlighted and the winner(s) shown. Black/high-contrast theme, scales with window size (`Viewbox`).
4. **Map window (projected)**: grid of territories coloured by owner, base marker icon, owner initials/name on hover or label, **scoreboard side panel**, current phase banner. Animated colour transition when ownership changes; highlight the territories valid for current pick and the battle target.

Both projector windows are opened on demand from the control window (`Open question window`, `Open map window`), are chromeless-capable (`F11` fullscreen), and can be dragged to the second monitor.

### 9.3 UX details worth doing

- Disable buttons that are invalid for the current phase/state (bind to `CanExecute`).
- Show connection status badge (connected / reconnecting / lost).
- Error toast for server `Error` events.
- Dispatch SignalR callbacks to the UI thread (`Dispatcher.InvokeAsync`).

---

## 10. Player web application (wwwroot)

### 10.1 Technology

Plain HTML/CSS/JS (ES modules). One `index.html` as a single-page app with sections that are shown/hidden by state (`join`, `lobby`, `question`, `waiting`, `pick`, `results`). No bundler. Mobile-first CSS (flex/grid, `100dvh`, touch-sized buttons ≥ 48 px, `viewport` meta, prevents zoom on inputs by using 16 px font).

File structure:
```
wwwroot/
├─ index.html
├─ css/app.css
└─ js/
   ├─ lib/signalr.min.js      (vendored)
   ├─ main.js                 (bootstrap, state machine of screens)
   ├─ api.js                  (fetch wrappers)
   ├─ hub.js                  (connection, event handlers, rejoin)
   ├─ hubNames.js             (shared names)
   ├─ ui/join.js lobby.js question.js map.js results.js
   └─ util/timer.js           (server-offset countdown)
```

### 10.2 Screens

1. **Join**: code field (prefilled from `?code=` – the QR code target), name field → `join`. Clear error messages ("Room not found", "Name already taken").
2. **Lobby/Waiting**: "You're in! Waiting for the host…", own name and colour, player count.
3. **Question (multiple choice)**: only four big option buttons (A–D) + countdown. After tapping, the choice is locked and visibly confirmed ("Answer sent"); no result shown until the round closes. (Per spec the phone shows only the options, not the question text, since the question is on the projector.) `DECISION D11`: optionally show a collapsed question text for accessibility – default off to follow the spec.
4. **Question (estimation)**: numeric input (`inputmode="decimal"`) + Send + countdown; locked after submit.
5. **Territory pick**: a compact map (grid) where only valid cells are tappable; confirm step to avoid mis-taps; countdown for the pick timeout. For others: "X is choosing a territory…".
6. **Result of round**: "You were fastest!" / "Correct, but not fastest" / "Wrong" / "Your guess was 14 off – rank 3", plus current score and rank.
7. **Final results**: standings, own rank, final map.

A permanent small header: name, colour chip, score, connection indicator. Handles `visibilitychange` and screen lock (mobile browsers suspend sockets) by relying on automatic reconnect + `rejoin`.

---

## 11. Docker and deployment

`docker-compose.yml` services:
- `db`: `postgres:16`, volume `pgdata`, healthcheck `pg_isready`.
- `api`: built from `src/QuizGame.Server/Dockerfile` (multi-stage: `sdk:10.0` build → `aspnet:10.0` runtime), env: connection string, JWT settings, seed admin password. `depends_on: db (healthy)`.
- `proxy` (Caddy): TLS termination, forwards HTTP + WebSocket (Caddy handles WS upgrade automatically) to `api:8080`.

Commands to document in README: `docker compose up --build`, default URLs, how the WPF app is pointed to the server, how to reset the DB (`docker compose down -v`).

CI (optional, small): GitHub Actions workflow running `dotnet build` and `dotnet test` on push.

---

## 12. Testing strategy

| Level | What | How |
|---|---|---|
| Unit (most valuable) | Evaluation ranking (MC fastest-correct, estimation distance, ties, no-answers), scoring from ownership, map adjacency and valid-pick sets, phase state machine transitions, base-selection ordering, battle resolution | xUnit on `Domain/` |
| Integration | REST auth + question CRUD; join / rejoin; full hub flow with two simulated players using `HubConnection` against `WebApplicationFactory` | Testcontainers PostgreSQL |
| Manual / E2E script | A written checklist: 1 host + 4 phones (or browser tabs in device emulation) playing a complete game, including reconnect (airplane mode toggle) and late answers | Checklist in `docs/test-plan.md` |
| Load smoke | 30–50 simulated players answering simultaneously | small console tool using the SignalR client |

Definition of done for each feature: unit tests for rules, one integration test for the happy path, manual check on a real phone.

---

## 13. Work breakdown and schedule

Roles (suggested; swap freely – the interfaces are defined by the contract in section 6):

| Member | Primary area | Secondary |
|---|---|---|
| **A – Backend/domain** | Domain rules, state machine, evaluation, GameRoom, hub | Tests |
| **B – Backend/data** | EF Core model, migrations, seed, REST controllers, JWT, Docker/Caddy | CI |
| **C – WPF host** | Control window, question bank UI, lobby/QR, hub client | Projector windows |
| **D – Player web + WPF projectors** | Web client, mobile UX, Question/Map windows | Manual testing |

Everyone writes tests for their own part and reviews at least one other member's PRs.

### Milestones

**M0 – Foundation (all, ~1–2 days)**
- Create repo, solution structure, `Contracts` library with DTOs/enums/hub names.
- Agree on `docs/api-contract.md` (section 6) and the decisions list (section 16).
- Docker compose with Postgres + empty API + health check; CI build.
- Branch strategy: `main` protected, feature branches, PRs with one review.

**M1 – Walking skeleton (~1 week)**
- B: DbContext, entities, first migration, seed, `/auth/login`, JWT.
- A: `GameRoomManager`, create room, join, `RoomState`, presence.
- C: Login window + control window shell + "create room" showing the code (QR later).
- D: Web join page → lobby with live player list.
- **Exit criterion**: host creates a room, phones join and appear on the host's list in real time.

**M2 – Question bank + question round (~1 week)**
- B: Categories/Questions CRUD with validation.
- C: Question bank UI (grid + editor dialog).
- A: Round lifecycle, timer, answer submission, evaluation for both types, `RoundClosed`.
- D: Question screens on phone, countdown with server offset; WPF Question window.
- **Exit criterion**: host picks a question, phones answer, the projector shows the result with ranking.

**M3 – Map and base selection (~1 week)**
- A: `MapService`, territory model, base selection flow with ranking order and pick timeout.
- D: WPF Map window, web territory picker.
- C: Phase controls, room settings panel.
- **Exit criterion**: complete Lobby → BaseSelection with bases visible on the projected map.

**M4 – Conquest and battles (~1 week)**
- A: Multiple-choice grabs with adjacency rule, battle flow (nominate → confirm → duel), scoring.
- C/D: battle UI on host, phone and projectors, scoreboard.
- **Exit criterion**: a full game to Results with correct scores.

**M5 – Robustness and polish (~1 week)**
- Reconnect handling (host and players), kicks, error paths, rate limiting, persistence of results, results screen + CSV export, QR code, TLS proxy.
- Load smoke test, full manual test pass on real iOS and Android phones.

**M6 – Documentation and submission (~3–4 days)**
- Architecture documentation (diagrams from sections 2, 5, 6, 7), user guide, README, demo video/script, rehearsal of the demo.
- Feature freeze at least 3 days before the deadline; only bug fixes afterwards.

Integration points are weekly: at the end of each milestone, merge to `main` and run the exit-criterion demo together.

---

## 14. Traceability: spec → implementation

| Spec requirement | Where implemented |
|---|---|
| Central question DB | PostgreSQL `Questions`, `QuestionOptions`, `Categories` (section 5) |
| Host picks question by category / type | Control window question picker + `GET /questions` filters (6.2, 9.2) |
| Question shown in separate projector window | WPF Question window (9.2) |
| Mobile clients answer within deadline | `SubmitAnswer` + server deadline + countdown (7.3, 10.2) |
| Room creation, 6-digit code or QR | `POST /rooms`, QRCoder, `?code=` deep link (6.2, 9.2, 10.2) |
| Honfoglaló-style map in a separate window | WPF Map window (9.2) |
| Four phases (lobby, base, conquest, results) | State machine (4.2) |
| MC decides territory capture by speed + correctness | Grab rounds, evaluation (4.2, 4.5) |
| Estimation decides bases and battles | Base selection, battles (4.2) |
| Game state on server | `GameRoomManager`/`GameRoom` (7) |
| ASP.NET Core + SignalR (rooms = groups) | Hub groups (6.3) |
| EF Core + PostgreSQL in Docker | Section 5, 11 |
| WPF desktop client Win 10/11 | Section 9 |
| Mobile-friendly responsive web app, Android + iOS, SignalR JS | Section 10 |
| HTTPS/WSS, JWT | Sections 6.1, 8, 11 |
| Use cases: manage bank, create room, manage players, start/advance phases, send question, view results, project | 9.2, 6.2, 6.3 |
| Player use cases: answer, choose territory, view score/result, join, rejoin | 10.2, 7.5 |

---

## 15. Risks and stretch goals

**Risks and mitigations**

| Risk | Mitigation |
|---|---|
| Phones cannot reach the host server during demo (Wi-Fi isolation, certificates) | Decide the network setup in M0; rehearse early; prefer a publicly reachable HTTPS endpoint or a tunnel; keep a mobile hotspot as backup |
| iOS Safari quirks (WebSocket suspension, input zoom, `dvh`) | Test on a real iPhone from M1; automatic reconnect + rejoin; 16 px inputs |
| Race conditions in rounds | Per-room command queue (7.2), heavy unit testing of the state machine |
| Rules ambiguity discovered late | Decisions list (16) confirmed in M0; keep rules parameterized via `RoomSettings` |
| WPF multi-window state inconsistency | Single `GameStateStore` bound by all windows |
| Time pressure | Milestones have independent exit criteria; stretch goals are cut first |

**Stretch goals (only after M5)**
- Hex or irregular "Honfoglaló"-like map layout; map editor presets.
- Speed bonus points; streaks.
- Rehydrating rooms from DB after a server restart.
- Question import from CSV/JSON; image questions.
- Sound effects and animations on the projector windows.
- Spectator mode in the web client (read-only map).
- Localisation (HU/EN) of the web client and WPF app.

---

## 16. Decisions to confirm in M0

| ID | Decision | Default in this plan |
|---|---|---|
| D1 | Phase behaviour details | As in 4.2 |
| D2 | Winners per multiple-choice round | 1 (configurable) |
| D3 | Who selects the battle target | Attacker nominates, host confirms |
| D4 | End of Conquest | Host ends; suggestions at all-owned / 10 rounds |
| D5 | Speed bonus points | None in v1 |
| D6 | Map shape and size | Square grid, 4-neighbour, S/M/L presets |
| D7 | Answer change before deadline | Not allowed; first answer final |
| D8 | Tie on receive time | Queue order |
| D9 | Server restart mid-game | Game lost; DB keeps recorded data |
| D10 | TLS | Caddy reverse proxy in compose |
| D11 | Question text on phones | Hidden (options only), per spec |

Other open questions for the instructor/team: how many simultaneous rooms must be supported (plan assumes a handful, each ≤ 30 players); whether the host login must support multiple host accounts (plan supports it); whether the demo environment has a second monitor/projector (plan assumes the windows can be demoed on one screen too).

---

## 17. Definition of done for the whole project

- `docker compose up` starts the full backend, and the README explains setup in under 10 commands/steps.
- A complete game (≥ 4 players, all four phases, both question types, at least one battle) works on real phones and the WPF host.
- Reconnect works for both phone and host.
- All spec requirements in section 14 are demonstrably covered.
- Unit tests for domain rules pass in CI; the manual test checklist has been executed once end to end.
- Architecture documentation and user guide are included in `docs/`.
