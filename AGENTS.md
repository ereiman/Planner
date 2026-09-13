# Repository guidance

- Use `dotnet build Planner.sln` and `dotnet test Planner.sln` for verification.
- Run `npm run assets` after installing npm dependencies.
- After every code or style change, stop the running Planner processes, rebuild `src/Planner.Web/Planner.Web.csproj` in Release, and relaunch on `http://127.0.0.1:5187`.
- Keep Razor components behind application service interfaces; do not inject or access `PlannerDbContext` from components.
- Integration tests use isolated temporary SQLite files.
- The production SQLite database is outside the repository at `%LOCALAPPDATA%\Planner\Data\planner.db`; backups are under `%LOCALAPPDATA%\Planner\Backups` and must never include `%LOCALAPPDATA%\Planner\Auth`.
- Keep the shared `TaskEditor.razor` as the canonical task-edit UI and keep board drag/drop mutations authoritative in `IBoardService`.
