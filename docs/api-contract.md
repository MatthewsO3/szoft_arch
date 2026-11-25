# API contract

The shared contract between the server, the WPF host, and the player web app.
It mirrors plan §6 and the types in `QuizGame.Contracts`. **When you change a hub
method, event, or DTO, update this file, `QuizGame.Contracts`, and
`wwwroot/js/hubNames.js` in the same change** (structure.md working agreements).

- Transport: HTTPS for REST, WSS for SignalR. One origin (web app served from
  `wwwroot`), so no CORS.
- Auth: JWT. GM token (`role=GameMaster`, 8 h) and player token
  (`role=Player`, `room=<code>`, 12 h), same signing key (plan §6.1).
- Errors: REST returns RFC 7807 `ProblemDetails`; SignalR pushes `Error { code, message }`.
- Timing: the server is authoritative. Answer ordering and deadlines use server
  receive time, never client clocks (plan §7.3).

---

## 1. REST endpoints (`/api`)

| Method | Route | Auth | Request → Response |
|---|---|---|---|
| POST | `/auth/login` | – | `LoginRequest` → `AuthResponse` |
| POST | `/auth/register` | – (if `AllowRegistration`) | `LoginRequest` → `AuthResponse` |
| GET | `/categories` | GM | → `CategoryDto[]` |
| POST | `/categories` | GM | `{ name }` → `CategoryDto` |
| PUT | `/categories/{id}` | GM | `{ name }` → `CategoryDto` |
| DELETE | `/categories/{id}` | GM | → 204 |
| GET | `/questions?categoryId=&type=&difficulty=&search=&page=&pageSize=` | GM | → `PagedResult<QuestionDto>` |
| GET | `/questions/{id}` | GM | → `QuestionDto` |
| POST | `/questions` | GM | `QuestionUpsertRequest` → `QuestionDto` |
| PUT | `/questions/{id}` | GM | `QuestionUpsertRequest` → `QuestionDto` |
| DELETE | `/questions/{id}` | GM | → 204 (soft delete, `IsActive=false`) |
| POST | `/rooms` | GM | `RoomSettings?` (optional body) → `CreateRoomResponse` (`201`) |
| GET | `/rooms/{code}` | GM (owner) | → `RoomStateDto` |
| DELETE | `/rooms/{code}` | GM (owner) | → 204; pushes `RoomClosed` to the room |
| GET | `/rooms/{code}/exists` | – | → `RoomExistsResponse` |
| POST | `/rooms/{code}/join` | – | `JoinRequest` → `JoinResponse` |
| POST | `/rooms/{code}/rejoin` | Player | → `JoinResponse` |
| GET | `/sessions/{id}/results` | GM | → `SessionResultsDto` |
| GET | `/health` | – | → `{ status: "ok" }` |

Validation (plan §5.2): a multiple-choice question has 2–4 options with exactly one
correct; an estimation question has `CorrectNumber` set and no options. Player names
are 1–20 chars, trimmed, unique per room (case-insensitive).

Enums are serialized as numbers on the wire (`QuestionType`: `0` MultipleChoice, `1`
Estimation), matching `GamePhase`. `GET /questions` query enums accept the same
numeric values (e.g. `?type=1`); `page` defaults to 1 and `pageSize` to 20 (max 100).

---

## 2. SignalR hub (`/hubs/game`)

Token passed via `accessTokenFactory`; read from the `access_token` query string for
`/hubs/*` (browsers can't set WS headers). Group membership is derived from token
claims in `OnConnectedAsync`, never from client arguments (plan §6.3).

Groups: `room:{code}` (everyone), `room:{code}:gm` (host), `room:{code}:players`.

### Client → server methods

| Method | Caller | Args | Effect |
|---|---|---|---|
| `HostAttach` | GM | `roomCode` | Register host (and projector) connections for the room |
| `KickPlayer` | GM | `playerId` | Remove a player |
| `UpdateSettings` | GM | `RoomSettings` | Lobby only |
| `StartGame` | GM | – | Lobby → BaseSelection; generates the map |
| `AdvancePhase` | GM | `GamePhase targetPhase` | Validated transition (no skip/back) |
| `SendQuestion` | GM | `SendQuestionRequest` | Opens a round |
| `CloseRound` | GM | – | Ends the current round early |
| `ConfirmBattle` | GM | – | Confirms an attacker's nomination (D3) |
| `SubmitAnswer` | Player | `SubmitAnswerRequest` | Records an answer (first is final, D7) |
| `PickTerritory` | Player | `int territoryIndex` | Valid only for the current picker |
| `NominateTarget` | Player | `int territoryIndex` | Attacker nominates a battle target (D3) |
| `Rejoin` | Player/GM | – | Re-attach after disconnect; server replies with `RoomState` |
| `LeaveRoom` | Player | – | Player leaves voluntarily; room gets `PlayerLeft` |
| `Ping` | any | – | Latency / keepalive / clock sync |

### Server → client events

| Event | Target | Payload |
|---|---|---|
| `RoomState` | caller | `RoomStateDto` (full snapshot + `ServerUtcNow`) |
| `PlayerJoined` | room | `PlayerSummary` |
| `PlayerLeft` | room | `PlayerSummary` |
| `PlayerConnectionChanged` | room | `PlayerSummary` |
| `PhaseChanged` | room | `GamePhase` |
| `MapUpdated` | room | `MapUpdate` (changed cells + scoreboard) |
| `QuestionStarted` | gm group | `QuestionDto` (includes correct answer) |
| `QuestionStarted` | players group | `QuestionForPlayer` (no correct answer); battle → only the two players |
| `BattleAnnounced` | non-participants | `BattleAnnouncement` |
| `AnswerCountChanged` | gm group | `AnswerCount` |
| `RoundClosed` | room | `RoundClosedDto` (ranking + correct answer + winners) |
| `PickRequested` | the picker | `PickRequest` |
| `PickWaiting` | others | `PlayerSummary` (who is choosing) |
| `ScoreboardUpdated` | room | `ScoreEntry[]` |
| `GameFinished` | room | `GameFinishedDto` |
| `Kicked` | the kicked player | – (client should drop its session) |
| `RoomClosed` | room | – (host closed the room or it was idle too long) |
| `Error` | caller | `ErrorDto` |

**Invariant:** never send a correct answer or another player's answer to a player
client before `RoundClosed` (plan §6.3).

---

## 3. Error codes (`ErrorDto.Code`)

Initial set; extend as needed and keep in sync with the client:

| Code | Meaning |
|---|---|
| `RoomNotFound` | No active room for the code |
| `NameTaken` | Display name already used in the room |
| `NotAuthorized` | Token lacks the role/room for this action |
| `InvalidPhase` | Action not allowed in the current phase |
| `InvalidTransition` | Illegal phase change |
| `AnswerTooLate` | Received after deadline + grace |
| `AlreadyAnswered` | Second answer rejected (first is final) |
| `NotYourPick` | PickTerritory from someone who isn't the picker |
| `InvalidPick` | Territory not free / not adjacent / not valid |
| `NotEnoughPlayers` | StartGame below `MinPlayers` |
| `InvalidCredentials` | Wrong username / password (REST 401) |
| `PlayerNotFound` | Player is not (or no longer) in the room, e.g. kicked |
| `InvalidName` | Display name empty or longer than 20 chars (REST 400) |
| `RoomFull` | Room reached its player limit (REST 409) |
| `NotInLobby` | Join attempted after the game started (REST 409) |
| `InvalidSettings` | `RoomSettings` value out of range (REST 400) |
| `NotAttached` | Hub method needs `HostAttach` / a joined room first |
| `CategoryNotFound` | No category with the given id (REST 404) |
| `CategoryNameTaken` | A category with that name already exists (REST 409) |
| `CategoryInUse` | Category still has questions; cannot be deleted (REST 409) |
| `InvalidCategory` | Category name empty or longer than 64 chars (REST 400) |
| `QuestionNotFound` | No active question with the given id (REST 404) |
| `InvalidQuestion` | Question fails type-specific validation, plan §5.2 (REST 400) |

---

## 4. Room connection notes

- **Joining:** `POST /rooms/{code}/join` returns a player token bound to that room. The player then connects
  to `/hubs/game` with the token; the server attaches them to the room groups from the token claims and
  pushes `RoomState`. A player with no live connection stays in the room and is shown `Connected = false`.
- **Rejoin:** the web app stores `{ token, roomCode }` and calls `POST /rooms/{code}/rejoin` on load, then
  reconnects. Kicked or removed players get `PlayerNotFound`.
- **Host:** logs in, `POST /rooms`, connects to the hub and calls `HostAttach(roomCode)` (again after every reconnect).
- `GamePhase` is serialized as a number (`0` Lobby … `3` Results); `wwwroot/js/hubNames.js` exports `GamePhase`.
- `RoomExistsResponse.Phase` is `null` when the room does not exist.
- Rate limits: `/auth/login` 10/min and `/rooms/{code}/join|exists` 120/min per client IP (`RateLimits:*`); excess → `429`.
