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
| `UpdateSettings` | GM | `RoomSettings` | Lobby only; `InvalidPhase` afterwards, `InvalidSettings` for out-of-range values (§6) |
| `StartGame` | GM | – | Lobby → BaseSelection; generates the map and pushes `PhaseChanged` + `RoomState` + `MapUpdated` (§6) |
| `AdvancePhase` | GM | `GamePhase targetPhase` | Validated transition (no skip/back): BaseSelection → Conquest (all bases picked), Conquest → Expansion, Expansion → Duel (map full), Duel → Results (§6, §7) |
| `SendQuestion` | GM | `SendQuestionRequest { questionId }` | Opens the round for the current phase: Conquest (claim stage + MC), Expansion (estimation), Duel (confirms the nomination; use `ConfirmDuel`) (§7) |
| `CloseRound` | GM | – | Ends the current round early |
| `ConfirmDuel` | GM | `ConfirmDuelRequest { questionId }` | Confirms a pending nomination and opens the duel with that multiple-choice question (§7) |
| `SubmitAnswer` | Player | `SubmitAnswerRequest` | Returns `AnswerAccepted { roundId }` after recording an answer (first is final) |
| `PickTerritory` | Player | `int territoryIndex` | The current picker chooses a tile: base, Conquest claim or Expansion pick (§6, §7) |
| `NominateTarget` | Player | `int territoryIndex` | The active Duel player nominates a target; the host is told via `DuelNominated` (§7) |
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
| `QuestionStarted` | gm group | `QuestionForHost { roundId, question: QuestionDto, serverDeadlineUtc }` (includes correct answer) |
| `QuestionSelectionAvailabilityChanged` | gm group | `bool` — authoritative indication that the host may send the next question |
| `QuestionStarted` | players group | `QuestionForPlayer` (no correct answer); Conquest → only the claimers; Duel → only the two players |
| `DuelAnnounced` | non-participants | `DuelAnnouncement { attackerId, defenderId, targetTerritoryIndex }` |
| `DuelNominated` | gm group | `DuelNominationDto { attackerId, defenderId, targetTerritoryIndex }` (awaiting the host's `ConfirmDuel`) |
| `AnswerCountChanged` | gm group | `AnswerCount` |
| `RoundClosed` | room | `RoundClosedDto` (ranking + correct answer + winners; per-row `wonTerritoryIndex` / `bonusAwarded`) |
| `PickRequested` | the picker | `PickRequest { validTerritoryIndices, pickDeadlineUtc, purpose }` |
| `PickWaiting` | others | `PickWaitingDto { picker, pickDeadlineUtc, purpose }` (who is choosing, deadline, and purpose) |
| `ScoreboardUpdated` | room | `ScoreEntry[]` (Duel bonus with no cell change) |
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
| `InvalidTransition` | Illegal phase change (skipping, going back, same phase) |
| `AnswerTooLate` | Received after deadline + grace |
| `AlreadyAnswered` | Second answer rejected (first is final) |
| `NotYourPick` | `PickTerritory` from someone who isn't the picker (also: no pick is pending, or the pick timed out) |
| `InvalidPick` | Territory not in the picker's valid list (taken, out of range, not adjacent, or next to a base when `DisallowAdjacentBases`) |
| `BaseSelectionIncomplete` | `AdvancePhase(Conquest)` before every player has picked a base |
| `PhaseNotComplete` | `AdvancePhase(Duel)` before Expansion filled the map; or a round for a phase whose round budget is used up |
| `NotYourTurn` | `NominateTarget` from a player who is not the active Duel player |
| `NotEnoughPlayers` | StartGame below `MinPlayers` |
| `InvalidCredentials` | Wrong username / password (REST 401) |
| `PlayerNotFound` | Player is not (or no longer) in the room, e.g. kicked |
| `InvalidName` | Display name empty or longer than 20 chars (REST 400) |
| `RoomFull` | Room reached its player limit (REST 409) |
| `NotInLobby` | Join attempted after the game started (REST 409) |
| `InvalidSettings` | `RoomSettings` value out of range (REST 400) |
| `NotAttached` | Hub method needs `HostAttach` / a joined room first |
| `QuestionNotFound` | No active question with the given id (REST 404; `SendQuestion` over the hub) |
| `RoundInProgress` | `SendQuestion` while a round or a territory pick is still in progress |
| `NoActiveRound` | `CloseRound` / `SubmitAnswer` with no open round, or for a round that is not the open one |
| `InvalidRound` | Kind does not fit the question type (Conquest/Duel = multiple choice, Expansion = estimation) or a malformed question |
| `InvalidAnswer` | Missing/unknown option (multiple choice) or missing number (estimation) |
| `NotParticipant` | The player is not part of this round (Conquest: only claimers; Duel: only attacker and defender) |
| `InvalidTarget` | `NominateTarget` on a free, own, non-existent or non-adjacent territory |
| `NoNomination` | `ConfirmDuel` with no pending nomination (it was never made, or the involved player left) |
| `CategoryNotFound` | No category with the given id (REST 404) |
| `CategoryNameTaken` | A category with that name already exists (REST 409) |
| `CategoryInUse` | Category still has questions; cannot be deleted (REST 409) |
| `InvalidCategory` | Category name empty or longer than 64 chars (REST 400) |
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

---

## 5. Question rounds

- **Opening:** `SendQuestion { questionId }` works while no round and no pick is in progress. The server picks the kind from
  the phase: Conquest → multiple choice (claim stage first), Expansion → estimation, Duel → multiple choice (via `ConfirmDuel`).
  A question of the wrong type for the phase is `InvalidRound`; the wrong phase is `InvalidPhase`.
- **Who receives what:** host group → `QuestionStarted` with `QuestionForHost` (round ID, deadline, full question including correct answer) plus
  `AnswerCountChanged(0, n)`; players → `QuestionStarted` with `QuestionForPlayer` (options only, no text, no correct
  answer, `ServerDeadlineUtc`). Conquest: only the claimers get it. Duel: only attacker and defender get it, everybody else `DuelAnnounced`.
- **Round id:** both question payloads carry `RoundId`; the host also receives the authoritative deadline.
- **Deadline:** `ServerDeadlineUtc = start + question.TimeLimitSeconds` (room default if 0). Answers are accepted up to
  deadline + 300 ms (`Rounds:GraceMilliseconds`), later ones get `AnswerTooLate`. Receive time is stamped by the server.
- **Answering:** the first answer is final (`AlreadyAnswered`). Multiple choice sends `Option` (`'A'`–`'D'`, case-insensitive),
  estimation sends `Number`.
- **Closing:** the round closes at deadline + grace, when every *connected* participant has answered, or when the host calls
  `CloseRound` (offline or removed players do not hold it open). Exactly one `RoundClosed` is sent to the whole room.
- **Ranking (`RoundClosedDto.Ranking`, ranks 1..n):**
  - Conquest: every claimer in turn order; `IsCorrect` per player, `WonTerritoryIndex` set for those who kept their claim.
    `WinnerPlayerIds` = the players who kept a tile.
  - Expansion: smallest `Distance` first, ties by earlier receive time, non-answerers last. `WinnerPlayerIds` = the closest
    answerer (who then picks a tile), or empty if nobody answered.
  - Duel: attacker then defender, each with `IsCorrect` and any `BonusAwarded`. `WinnerPlayerIds` = the attacker if they
    captured the tile, otherwise empty.
- **Reconnect:** `RoomState` includes a caller-specific `PlayerQuestion` or `HostQuestion`, `HasAnswered` for the caller, and host-only `AnswerCount`. `LastRoundResult` restores missed results. The active question takes precedence. Snapshots and round events are serialized per room to preserve delivery order.
- **Persistence:** on close the `Rounds` row and its `Answers` rows are written in one transaction (players without an
  answer have no `Answers` row). History write failures are logged and never affect the game.


- **Acceptance:** `SubmitAnswer` returns `AnswerAccepted { roundId }` only after recording the answer.
  Rejections throw `HubException` with a stable code. Unknown transport failures keep input locked until
  a fresh snapshot confirms whether an answer was recorded. No correct answers are sent to players
  before closure; host snapshots are never broadcast to players.
- **Host controls:** `SendQuestion { questionId }` opens the round for the current phase; the server picks the kind from
  the phase. **Close round** closes early. The full per-phase flow is in §7.

---

## 6. Phases, map and base selection

- **Settings:** `UpdateSettings(RoomSettings)` in the Lobby only (the host may call it repeatedly; no event is pushed, the
  caller's success is the confirmation). Ranges as in `POST /rooms`: values > 0, `MinPlayers` ≥ 1, `MapPreset` a defined value.
- **Starting:** `StartGame` needs at least `MinPlayers` *connected* players (`NotEnoughPlayers`). The server generates the map,
  fixes a **random turn order**, moves to BaseSelection and sends, in this order: `PhaseChanged(BaseSelection)`, a fresh
  `RoomState` to every connection (each with its own view, including `TurnOrder`), one `MapUpdated` with **all** cells, then
  `PickRequested` to the first player. `AdvancePhase(BaseSelection)` from the Lobby does the same. Nobody can join after the start (`NotInLobby`).
- **Map:** square grid, 4-neighbour adjacency, row-major `Index = Row × Cols + Col`. Presets: Small 5×5, Medium 6×8, Large 8×10.
  The preset is a minimum: with fewer than 3 cells per player the next preset is used (beyond Large a square grid that fits).
  `RoomState.Map` has the dimensions; `MapUpdated` only carries changed cells. A normal territory is worth `TerritoryValue` with
  1 health; a base is worth `BaseValue` with `BaseHealth` health.
- **Score:** `Σ value` of owned territories **plus** accumulated Duel bonus. `MapUpdate.Scoreboard` and `RoomState.Scoreboard`
  are sorted by score, then territory count, then turn position, then join order.
- **Base selection:** in turn order each player is asked `PickRequested { ValidTerritoryIndices, PickDeadlineUtc, Purpose = Base }`;
  everybody else gets `PickWaiting(PickWaitingDto)` carrying picker, deadline, and purpose. The picker answers `PickTerritory(index)` (errors `NotYourPick` / `InvalidPick`). Valid
  cells are all free tiles, or with `DisallowAdjacentBases` those touching no base (dropped if that leaves nothing). Each assignment
  is announced with `MapUpdated` (`IsBase = true`, `Value = BaseValue`, `Health = BaseHealth`).
- **Timeout:** after `PickTimeoutSeconds` the server assigns a random valid territory (also for offline players) and moves on;
  a pick that arrives afterwards gets `NotYourPick`.
- **Reconnect:** `RoomState.CurrentPick { PickerPlayerId, PickDeadlineUtc, ValidTerritoryIndices, Purpose }` — the valid cells are
  only filled for the picker. After the `RoomState` the server resends `PickRequested` / `PickWaiting` to the reconnecting connection.
- **Players leaving:** a kicked or leaving player's territories become free again (`MapUpdated`), they are removed from the turn
  order, and if it was their turn the next player is asked.
- **Phase changes:** `AdvancePhase(Conquest)` needs every base picked, no round and no pick pending
  (`BaseSelectionIncomplete` / `RoundInProgress`). `AdvancePhase(Expansion)` needs no round/pick. `AdvancePhase(Duel)` needs the
  map full (`PhaseNotComplete` otherwise). `AdvancePhase(Results)` sends `PhaseChanged(Results)` and
  `GameFinished { Standings, FinalMap }` (equal scores share a rank). Everything else is `InvalidTransition`.
- **Persistence:** the cells of the map are stored in `Territories` (all at the start, then the changed ones); history write
  failures are logged and never affect the game.

---

## 7. Conquest, Expansion and Duel

- **Conquest (claim + question):** the host calls `SendQuestion { questionId }` with a **multiple-choice** question. The server
  first runs a **claim stage**: each player in turn order gets `PickRequested { …, Purpose = ConquestClaim }` and claims one free
  tile (adjacent to one they own, else any free; skipped if none). A claim does **not** take ownership. Once every claim is in,
  the server opens the question to the claimers (`QuestionStarted`). On close, every claimer who answered correctly gets their
  claimed tile (normal, 1 health) via `MapUpdated`; wrong/missing answers release the claim. The turn order rotates right. The
  phase allows `ConquestRounds` rounds; a further `SendQuestion` after that returns `PhaseNotComplete`.
- **Expansion (estimation):** `SendQuestion { questionId }` with an **estimation** question opens one round for everyone. On close
  the closest guess gets `PickRequested { …, Purpose = Expansion }` and takes a free tile. Repeat until the map is full, then
  `AdvancePhase(Duel)`. `SendQuestion` with the map already full returns `PhaseNotComplete`.
- **Duel (nominate → confirm → duel):** the active player (front of the turn order) calls `NominateTarget(index)` — an enemy tile
  adjacent to one they own (`NotYourTurn` / `InvalidTarget` otherwise). The host receives `DuelNominated` and calls
  `ConfirmDuel { questionId }` with a **multiple-choice** question (`NoNomination` if none pending). Only attacker and defender get
  the question; everybody else gets `DuelAnnounced`. On close: **attacker correct** → the attack lands (`MapUpdated`); **defender
  correct** → defender keeps the tile and `+DefenderBonus`; **both** → draw, no tile change, each `+DrawBonus` (`ScoreboardUpdated`);
  **neither** → nothing. A base takes `BaseHealth` hits (health drops by one per hit, shown on `TerritoryDto.Health`); a captured
  tile becomes a normal tile; losing a last base frees all of that player's tiles. The turn order rotates right each round; the
  phase allows `DuelRounds` rounds.

---
