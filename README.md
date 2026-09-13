# Planner

A local-first desktop-oriented planner built with .NET 10, Blazor Interactive Server, MudBlazor, EF Core, and SQLite.

## Prerequisites

- .NET SDK 10.0.400 or compatible patch
- Node.js and npm

## Run

```powershell
npm ci
npm run assets
dotnet restore
dotnet run --project src/Planner.Web
```

The database is stored at `%LOCALAPPDATA%\Planner\Data\planner.db` and migrations are applied at startup. Development launch settings choose a local port; a published executable binds to `http://127.0.0.1:5187` and opens that address in the default browser. If the legacy `%LOCALAPPDATA%\Planner\planner.db` exists, Planner copies it non-destructively on first use of the new location. Consistent SQLite backups are kept under `%LOCALAPPDATA%\Planner\Backups`; Settings supports manual backups and folder actions, while startup creates a daily backup and a pre-migration backup. Retention defaults to 14 files and is configurable with `Planner:Backup:RetentionCount`.

## Optional Microsoft Outlook calendar setup

Planner works normally when this integration is not configured. To enable read-only Outlook availability, create an Entra public-client registration:

1. Open the [Microsoft Entra admin center](https://entra.microsoft.com/), then go to **Identity > Applications > App registrations > New registration**.
2. Enter a name such as `Planner local app`.
3. For **Supported account types**, choose **Accounts in any organizational directory and personal Microsoft accounts**.
4. Create the registration and copy its **Application (client) ID**. A client ID is an application identifier, not a secret.
5. Open **Authentication > Add a platform > Mobile and desktop applications**.
6. Select the recommended native-client redirect URI `http://localhost`, enable **Allow public client flows**, and save. Do not create or configure a client secret.
7. Open **API permissions > Add a permission > Microsoft Graph > Delegated permissions** and add:
   - `User.Read`
   - `Calendars.Read`

Set the client ID through .NET user secrets (recommended for a developer machine):

```powershell
dotnet user-secrets init --project src/Planner.Web
dotnet user-secrets set "Planner:Microsoft:ClientId" "YOUR-APPLICATION-CLIENT-ID" --project src/Planner.Web
```

Alternatively set the environment variable `Planner__Microsoft__ClientId`. Keep `Planner:Microsoft:Authority` as `https://login.microsoftonline.com/common` so both organizational and personal accounts are supported. `appsettings.json` intentionally contains a blank client ID; never add credentials or tokens to configuration.

Connecting is always initiated from **Settings > Microsoft Outlook > Connect account** and uses the system browser with explicit account selection. Periodic/background synchronization only attempts account-specific silent authentication and never opens an interactive prompt. MSAL stores refresh/access tokens in its encrypted persistent cache at `%LOCALAPPDATA%\Planner\Auth`; tokens are not stored in SQLite, logs, or Planner backups.

## Verify

```powershell
dotnet format Planner.sln --verify-no-changes
dotnet build Planner.sln --no-restore
dotnet test Planner.sln --no-build
```

## Architecture

The database contains one canonical task record. The Tasks page combines quick capture, the inbox queue, projects, and active work, and its reusable task editor is shared with Today, Due Calendar, and board cards. Boards contain placements that reference tasks and own board-local column and ordering state; dynamic columns, rules, suppression, transactional quick-create, and SortableJS moves all go through the authoritative board service. Today is built by a query service that combines due work, Planner blocks, imported meetings, future conflicts, unscheduled priority work, and sync freshness. Razor components call application services and never access `PlannerDbContext` directly.

Schedule saves use half-open ranges and require an explicit **Save Anyway** for every detected conflict, including warning-only tentative/working-elsewhere events and stale availability. Any override records `ConflictOverrideAtUtc`.
