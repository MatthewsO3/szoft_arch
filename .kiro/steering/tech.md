# Tech Stack & Architecture

The architecture below is **prescribed by the assignment** — do not substitute
frameworks or data stores without an explicit decision from the team.

## Architecture

Three-tier client–server. **The authoritative game state lives on the server.**
Clients render state and send intents; they never own game logic that the server
must enforce (scoring, phase transitions, who won a territory).

```
  WPF desktop (game master)  ─┐
                              ├─ HTTPS / WSS ─►  ASP.NET Core Web API + SignalR
  Responsive web (players)   ─┘                         │
                                                        ▼
                                              EF Core ──► PostgreSQL
```

## Backend

- **ASP.NET Core Web API** (C#).
- **SignalR** for real-time communication. **Rooms map to SignalR groups.**
- **Entity Framework Core** as the ORM.
- **PostgreSQL** as the database.
- Runs in **Docker** (containerized; use docker-compose for API + PostgreSQL in dev).

## Clients

- **Game master**: **WPF** desktop application, Windows 10/11.
- **Player**: mobile-friendly, **responsive web app**, reachable from a browser on
  Android and iOS. Connects to the server with the **SignalR JavaScript client**.

## Cross-cutting

- Transport: **HTTPS / WSS** everywhere.
- Authentication: **JWT**.
- Real-time events (join, question pushed, answer submitted, phase change, score
  update) flow over SignalR, scoped to the room's group.

## Conventions

- Keep server-authoritative rules on the server; clients must not be trusted to
  compute scores, timing results, or territory ownership.
- Share DTO/contract shapes between clients and server deliberately — document any
  SignalR hub method and its payload when you add or change it.
- Prefer async/await for all I/O (EF Core queries, SignalR calls).
- Use EF Core migrations for every schema change; never edit the database by hand.
- Store secrets (JWT signing key, DB connection string) in configuration/environment,
  never in source. Use `appsettings.Development.json` + user-secrets / env vars.

## Common commands

Fill these in as the project takes shape. Expected shape:

```bash
# Backend (from the backend project directory)
dotnet restore
dotnet build
dotnet run
dotnet ef migrations add <Name>
dotnet ef database update
dotnet test

# Local infra
docker compose up -d        # API + PostgreSQL
docker compose down

# Web player client (from the web client directory)
npm install
npm run dev
npm run build
```
