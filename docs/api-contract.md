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
| `QuestionNotFound` | `SendQuestion`: no active question with that id |
| `RoundInProgress` | `SendQuestion` while another round is open |
| `NoActiveRound` | `CloseRound` / `SubmitAnswer` with no open round, or for a round that is not the open one |
| `InvalidRound` | Kind does not fit the question type (Grab = multiple choice, BaseOrder/Battle = estimation), malformed question, or an invalid battle (no attacker/target, target has no owner, own territory) |
| `InvalidAnswer` | Missing/unknown option (multiple choice) or missing number (estimation) |
| `NotParticipant` | The player is not part of this round (battle: only attacker and defender) |

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

---

## 5. Question rounds

- **Opening:** `SendQuestion` works while no round is open. Kind ↔ type: `Grab` needs a multiple-choice question,
  `BaseOrder` and `Battle` an estimation question. Phase gating (BaseOrder only in BaseSelection, Grab/Battle only in
  Conquest) arrives with the phase machine; until then a round may open in any phase.
- **Who receives what:** host group → `QuestionStarted` with the full `QuestionDto` (correct answer included) plus
  `AnswerCountChanged(0, n)`; players → `QuestionStarted` with `QuestionForPlayer` (options only, no text, no correct
  answer, `ServerDeadlineUtc`). Battle: only attacker and defender get the question, everybody else `BattleAnnounced`.
- **Round id:** `QuestionForPlayer.RoundId` is the id players answer with. The host does not need it (`CloseRound` has no
  argument); it is in `RoomState.CurrentRound` and in `RoundClosedDto.RoundId`.
- **Deadline:** `ServerDeadlineUtc = start + question.TimeLimitSeconds` (room default if 0). Answers are accepted up to
  deadline + 300 ms (`Rounds:GraceMilliseconds`), later ones get `AnswerTooLate`. Receive time is stamped by the server.
- **Answering:** the first answer is final (`AlreadyAnswered`). Multiple choice sends `Option` (`'A'`–`'D'`, case-insensitive),
  estimation sends `Number`.
- **Closing:** the round closes at deadline + grace, when every *connected* participant has answered, or when the host calls
  `CloseRound` (offline or removed players do not hold it open). Exactly one `RoundClosed` is sent to the whole room.
- **Ranking (`RoundClosedDto.Ranking`, ranks 1..n):**
  - Multiple choice: correct answers by receive time, then wrong answers, then non-answerers (join order).
    `WinnerPlayerIds` = first `WinnersPerRound` correct players (empty if nobody was correct).
  - Estimation: smallest `Distance` first, ties by earlier receive time, non-answerers last (join order).
    `BaseOrder` → `WinnerPlayerIds` lists everybody in ranking order (the pick order). `IsCorrect` = exact hit.
  - Battle: same distance ranking, but a tie goes to the defender; `WinnerPlayerIds` has one entry.
    The defender is the owner of the target territory (needs the map, M3).
- **Reconnect:** a player or host that (re)attaches while a round is open gets `QuestionStarted` again (same deadline).
- **Persistence:** on close the `Rounds` row and its `Answers` rows are written in one transaction (players without an
  answer have no `Answers` row). History write failures are logged and never affect the game.

