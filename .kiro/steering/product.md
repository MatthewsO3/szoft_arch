# Product — Strategic Quiz Game (Stratégiai kvízjáték)

A multiplayer, real-time quiz game where players answer questions to capture and
defend territories on a Conquest-style ("honfoglaló") map. University software
architecture course project (Szoftverarchitektúrák házi feladat).

## Roles & clients

- **Game master (Játékmester)** — runs the **WPF desktop app** (Windows 10/11).
  Creates a room, picks questions, advances game phases, and manages the question
  bank. Their machine shows **three windows**:
  1. a private control window (only the game master sees it),
  2. a projectable **question window** — question text, answer options, countdown,
  3. a projectable **map window** — territory owners, bases, and scores.
- **Player (Játékos)** — joins from a **mobile-friendly responsive web app**
  (browser, Android/iOS) using a **6-digit room code or QR code** plus a display
  name. On their phone they see only the answer options (or a number field for
  estimation questions) together with the countdown.

## Game flow — four phases

1. **Lobby** — players join the room.
2. **Base capture (bázisfoglalás)** — bases are assigned via estimation questions.
3. **Conquest (hódítás)** — players capture and defend territories.
4. **Results (eredményhirdetés)** — final standings.

The game master advances the phases manually.

## Question types

- **Multiple choice (feleletválasztós)** — A/B/C/D options, exactly one correct.
  Decides territory capture: the correct **and fastest** answerer wins.
- **Estimation / numeric (becslős)** — the answer is a number; the **closest guess**
  wins. Used for base assignment and for **battles (csata)** over already-owned
  territories.

All questions have a time limit; players must answer within the countdown.

## Domain glossary

- **Room (Szoba)** — one game instance created by the game master, identified by a
  6-digit code (or QR code) that players use to join.
- **Territory (Terület)** — a map cell a player can win by answering questions. Owned
  by at most one player at a time; the owner is shown by color. Contributes to score.
- **Base (Bázis)** — a player's starting, high-value territory assigned in the base
  capture phase. Worth more than regular territories; conquest starts from it.
- **Question bank (Kérdésbank)** — all questions and categories stored centrally. The
  game master filters by category, type, and difficulty when picking a question.
- **Battle (Csata)** — a duel over an already-owned territory between attacker and
  defender, decided by an estimation question; the closest guess takes the territory.
- **Projector (Kivetítő)** — an external display the game master projects the question
  and map windows onto so players can see them.
