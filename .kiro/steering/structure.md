# Project Structure & Conventions

This repository is a mono-repo containing the server and both clients. The layout
follows the implementation plan (`docs/implementation_plan.md`, section 3). Create
directories as each component is started.

## Layout

```
szoft_arch/
├─ .kiro/                        # Kiro config — committed, shared by the whole team
│  └─ steering/                  # Always-on project context (product, tech, structure)
├─ docker-compose.yml            # Local infra: Caddy (TLS) + API + PostgreSQL
├─ .env.example                  # Committed template for secrets (real .env is git-ignored)
├─ README.md
├─ docs/                         # Design docs, API contract, diagrams, decisions
│  ├─ implementation_plan.md
│  └─ szoftarch_spec.pdf         # Original assignment specification (source of truth)
├─ src/
│  ├─ QuizGame.Contracts/        # Class library: DTOs, enums, hub method/event name constants
│  ├─ QuizGame.Server/           # ASP.NET Core Web API + SignalR + EF Core
│  │  ├─ Controllers/            # REST endpoints (auth, questions, categories, rooms, results)
│  │  ├─ Hubs/                   # GameHub (rooms = SignalR groups)
│  │  ├─ Domain/                 # Pure game logic — NO ASP.NET / SignalR / EF Core references
│  │  ├─ Services/               # GameRoomManager, TimerService, ScoringService, ...
│  │  ├─ Data/                   # DbContext, entities, migrations, seed
│  │  ├─ Auth/                   # JWT issuing/validation, authorization policies
│  │  └─ wwwroot/                # Player web app (static, no build step)
│  │     ├─ index.html
│  │     ├─ css/
│  │     └─ js/
│  └─ QuizGame.Host/             # WPF game master app (net10.0-windows)
│     ├─ Views/                  # XAML windows: control, question, map
│     ├─ ViewModels/
│     ├─ Services/               # ApiClient, GameConnection, GameStateStore, WindowManager
│     └─ Models/
└─ tests/
   ├─ QuizGame.Domain.Tests/     # xUnit: game rules, scoring, map, state machine
   └─ QuizGame.Server.Tests/     # Integration tests (WebApplicationFactory + Testcontainers)
```

Target framework: **.NET 10 (LTS)** for all projects; WPF as `net10.0-windows`.

## Why this shape

- **`QuizGame.Contracts`** is shared by the WPF client and the server (DTOs, enums,
  hub method/event name constants) so the contract cannot silently drift. The JS
  client mirrors the same names in `wwwroot/js/hubNames.js`, documented in
  `docs/api-contract.md`.
- **`Domain/` lives inside the server project** (not a separate assembly) to keep the
  solution small, but it has a strict rule: **no references to ASP.NET, SignalR, or
  EF Core**, so the game rules stay unit-testable. It can be promoted to a
  `QuizGame.Domain` assembly later at no real cost.
- **One origin**: the player web app is served from `QuizGame.Server/wwwroot`, so no
  CORS, no separate hosting, one HTTPS certificate.

## Naming & style

- **Backend (C#)**: PascalCase for types/methods/properties, camelCase for locals and
  parameters. One public type per file, file name matches the type. Follow default
  `dotnet format` / .editorconfig rules.
- **Web client (JS)**: plain HTML + CSS + ES modules, no bundler. camelCase for
  variables/functions, PascalCase for classes. Keep the SignalR connection and event
  handling in dedicated modules (`js/hub.js`, `js/hubNames.js`), not scattered across
  UI code. Mobile-first CSS; render untrusted strings with `textContent`, never
  `innerHTML`.
- **Desktop client (WPF)**: MVVM with `CommunityToolkit.Mvvm` — Views (XAML),
  ViewModels, Models separated. Keep the three windows (control, question, map) as
  distinct views bound to view models fed by a single shared `GameStateStore`.

## Working agreements

- The server is the single source of truth for game state. Clients send intents and
  render pushed state; they do not compute authoritative results (scoring, phase
  transitions, who won a territory, timing).
- Server-side timing: answer deadlines and ordering ("fastest correct") are decided by
  server receive time, never by client clocks.
- Document every SignalR hub method and event (name, direction, payload) when you add
  or change one. Keep `QuizGame.Contracts.HubNames`, `wwwroot/js/hubNames.js`, and
  `docs/api-contract.md` in sync.
- Every schema change goes through an EF Core migration — never hand-edit the DB.
- Keep secrets out of source (JWT key, connection strings, seed admin password) — use
  env vars / `.env` (git-ignored) / user-secrets. Commit `.env.example` only.
- When a change spans multiple components, update all affected sides in the same change
  and note the contract change in the PR description.
- The assignment spec (`docs/szoftarch_spec.pdf`) is the source of truth for
  requirements; the implementation plan records explicit decisions (D1–D11) where the
  spec is silent. When in doubt, re-read the spec rather than guessing.
