# Contributing to MangosSuperUI

Thanks for your interest in contributing to MangosSuperUI! Whether it's a bug report, documentation fix, new feature, or just a question — it's all welcome.

## Before You Start

**Open an issue first** before submitting a PR for anything non-trivial. This lets us align on approach before you invest time writing code. Bug fixes and documentation improvements can go straight to a PR.

You'll need a working VMaNGOS 1.12.1 server environment to develop and test. MangosSuperUI talks to live VMaNGOS databases and the RA interface, so there's no mock/stub mode — you need the real thing running.

## Development Environment

- **IDE:** Visual Studio 2022 (Windows) for development. The app deploys to Linux.
- **Runtime:** .NET 8.0 SDK
- **Database:** MariaDB 10.x+ or MySQL 5.5+ (whatever VMaNGOS uses)
- **Server:** A compiled VMaNGOS instance with populated databases and RA enabled
- **Optional:** A WoW 1.12.1 client for asset extraction (icons, models, minimap tiles)

### Getting Running

1. Fork and clone the repo
2. Open `MangosSuperUI.sln` in Visual Studio
3. Copy `appsettings.json` and configure your database connections and RA credentials
4. Build and run — the first launch will prompt you through setup or run the setup script
5. See **[INSTALL.md](INSTALL.md)** for the full deployment guide if you're setting up from scratch

## Architecture — The One-Paragraph Version

Every page follows the same pattern: a **C# controller** handles routing, database queries, RA commands, and audit logging; a **thin Razor view** provides the HTML shell; a **JS file** drives all dynamic rendering via AJAX. The app is essentially a collection of single-page apps inside an MVC shell. Read the [Architecture section in the README](README.md#architecture) for more detail.

## Code Conventions

### Page Pattern

If you're adding a new page, follow the existing structure:

- **Controller** in `Controllers/` — one controller per page. Handle routing, data access, and audit logging here.
- **View** in `Views/{ControllerName}/` — keep it minimal. The Razor view is a thin HTML skeleton with layout references and script includes. No heavy logic.
- **JavaScript** in `wwwroot/js/` — one JS file per page. All dynamic rendering, AJAX calls, and DOM manipulation live here. jQuery is the standard.
- **CSS** — use the global theme variables where possible. Scoped styles go in the view's `@section Styles` block.

### Database Access

- **Dapper** for all VMaNGOS database reads (mangos, characters, realmd, logs). Raw SQL, read-heavy.
- **EF Core** for MangosSuperUI's own `vmangos_admin` database.
- **RA commands** for VMaNGOS game-state mutations (kick, ban, teleport, send mail, etc.). Use `RaService`.
- **Direct SQL writes** only for content tables — items, spells, game objects, loot tables — where RA has no command. Always audit-log with before/after state via `AuditService`.
- **Read-only enforcement** — `characters` and `logs` databases must remain read-only at the controller level.

### Audit Logging

Every mutation must be audit-logged. Use `AuditService` with:
- Category and action type
- Target identifier
- RA command and response (if applicable)
- Full before/after state as JSON

This is non-negotiable. The audit trail is a core feature, not an afterthought.

### Naming

- SQL identifiers validated against schema whitelists before query construction (security requirement for Database Explorer)
- Custom items use entry IDs ≥ 900000
- Custom game objects, spells, etc. follow similar high-ID conventions to avoid collisions with base VMaNGOS data

## What To Work On

### Good First Contributions

- **Documentation** — typos, unclear install steps, missing explanations
- **Bug reports** — if something breaks, open an issue with steps to reproduce
- **CSS/theme fixes** — visual polish, responsive layout issues
- **Config metadata** — the Config Editor's 601-setting JSON could always use better descriptions

### Larger Contributions (open an issue first)

- **New content editors** — Vendors, Creatures, Quests are all on the Phase 5 roadmap
- **NPC/creature spawn overlay** on the World Map
- **Docker Compose** packaging
- **Database Explorer enhancements** — saved queries, column visibility, baseline diff view
- **Lootifier improvements** — configurable original-item share, expansion math preview

### Areas That Need Expertise

- **WMO model support** in the Extractor — ~15 game object entries reference `.wmo` files (World Map Objects), a completely different format from M2. Currently skipped.
- **Performance** — the Database Explorer loads a ~6MB relationship JSON at startup. If you have ideas for making this lazier or more efficient, that'd be great.

## Pull Request Guidelines

- Keep PRs focused — one feature or fix per PR
- Test against a live VMaNGOS environment before submitting
- If your change touches database writes, verify the audit log captures before/after state correctly
- If adding a new page, include a screenshot in the PR description
- Don't commit extracted assets (icons, models, minimap tiles) — these are in `.gitignore` for a reason

## Reporting Bugs

Open a GitHub issue with:
- What you expected to happen
- What actually happened
- Steps to reproduce
- Browser and OS
- MangosSuperUI version / commit hash
- Relevant log output (check `Live Logs` in the UI or your systemd journal)

## License

By contributing, you agree that your contributions will be licensed under the **GNU General Public License v2.0**, consistent with the project's existing license.
