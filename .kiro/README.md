# Kiro configuration

This folder holds the shared [Kiro](https://kiro.dev) setup for the project. It is
committed so the whole team works with the same project context and conventions.

## What's here

### `steering/`

Steering files are Markdown context that Kiro injects into every session. They give
the agent persistent knowledge about this project so you don't have to re-explain it.

| File           | Purpose                                                        |
| -------------- | -------------------------------------------------------------- |
| `product.md`   | What we're building: roles, game flow, question types, glossary |
| `tech.md`      | Prescribed stack & architecture, conventions, common commands   |
| `structure.md` | Repo layout, naming/style rules, working agreements             |

By default these are **always included** (`inclusion: always`). You can change a file
to load only on demand by adding front matter — see the Kiro steering docs.

### `skills/`

Skills are portable instruction packages (each a folder with a `SKILL.md`). Kiro loads
only the `name` + `description` at startup and pulls in the full instructions when a
request matches, or when an agent declares it.

| Skill     | Purpose                                                             |
| --------- | ------------------------------------------------------------------- |
| `caveman` | Respond in terse caveman speak. Tone only — facts and code stay correct. |

Use it directly in any session with the `/caveman` slash command, or let an agent that
wires it in apply it automatically.

### `agents/`

Custom agents — one Markdown file per agent, the filename is the agent name. Each wires
in the `caveman` skill via a `skill://` resource (custom agents don't auto-load skills).

| Agent             | Purpose                                                                 |
| ----------------- | ----------------------------------------------------------------------- |
| `caveman-backend` | Backend dev (ASP.NET Core + SignalR + EF Core + PostgreSQL), caveman tone |
| `caveman-reviewer`| Read-only code reviewer, caveman tone                                   |

Switch with `/agent swap <name>` or launch with `kiro-cli --agent <name>`.

### `settings/cli.json`

Workspace settings committed for the team. Sets the default agent:

```json
{ "chat.defaultAgent": "caveman-backend" }
```

So opening this repo starts in `caveman-backend` by default. To change it:

```bash
kiro-cli settings --workspace chat.defaultAgent <agent-name>
```

(`settings/*.lock` is a transient CLI file and is gitignored.)

## Using it

Just open the project with Kiro — the steering files load automatically. No setup
needed beyond having Kiro installed.

## Maintaining it

Treat these files like code:

- Keep them accurate as the project evolves (new commands, structure changes,
  decisions). Stale steering is worse than none.
- Edit via normal commits and PRs so the team reviews context changes.
- Keep them concise and factual — they consume context on every request.

The assignment spec (`../szoftarch_spec.pdf`) remains the source of truth for
requirements; steering summarizes it for day-to-day work.
