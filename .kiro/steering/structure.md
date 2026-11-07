# Project Structure & Conventions

This repository is a mono-repo containing the server and both clients. Keep each
component in its own top-level directory so builds and tooling stay isolated.

## Layout

The target layout (create directories as each component is started):

```
szoft_arch/
├─ .kiro/                 # Kiro config — committed, shared by the whole team
│  └─ steering/           # Always-on project context (product, tech, structure)
├─ backend/               # ASP.NET Core Web API + SignalR + EF Core
│  ├─ src/                # API project, hubs, domain, persistence
│  └─ tests/              # Backend unit/integration tests
├─ web-client/            # Responsive web player client (SignalR JS client)
├─ desktop-client/        # WPF game master app (Windows 10/11)
├─ docker-compose.yml     # Local infra: API + PostgreSQL
├─ docs/                  # Design docs, diagrams, decisions
├─ szoftarch_spec.pdf     # Original assignment specification (source of truth)
└─ README.md
```

Adjust names if the team agrees, but keep the one-directory-per-component rule.

## Naming & style

- **Backend (C#)**: PascalCase for types/methods/properties, camelCase for locals and
  parameters. One public type per file, file name matches the type. Follow default
  `dotnet format` / .editorconfig rules.
- **Web client (JS/TS)**: camelCase for variables/functions, PascalCase for components.
  Keep SignalR connection and event handling in a dedicated module, not scattered
  across UI code.
- **Desktop client (WPF)**: follow MVVM — Views (XAML), ViewModels, Models separated.
  Keep the three windows (control, question, map) as distinct views bound to view
  models fed by server state.

## Working agreements

- The server is the single source of truth for game state. Clients send intents and
  render pushed state; they do not compute authoritative results.
- Document every SignalR hub method (name, direction, payload) when you add or change
  one, and keep client and server contract shapes in sync.
- Every schema change goes through an EF Core migration — never hand-edit the DB.
- Keep secrets out of source (JWT key, connection strings) — use env vars / user-secrets.
- When a change spans multiple components, update all affected sides in the same change
  and note the contract change in the PR description.
- The assignment spec (`szoftarch_spec.pdf`) is the source of truth for requirements;
  when in doubt, re-read it rather than guessing.
