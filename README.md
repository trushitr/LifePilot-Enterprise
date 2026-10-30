# LifePilot — Local-first productivity platform

C# / ASP.NET Core 8, React/TypeScript, EF Core + SQLite. This repository is an AI-assisted starter implementation; do not describe it as 120 hours of work already completed. **Build and tests have not been executed in the generation environment**; run the commands below before publishing.

## Implemented source modules

- User registration, salted PBKDF2 password hashes, JWT bearer login, role claims and Member/Admin policies.
- Protected task, grocery, and calendar APIs with per-user filtering.
- Typed validation exceptions and centralized RFC 7807-style Problem Details responses.
- Idempotency keys and SQLite transactions for item creation.
- Authentication rate limiting and health checks.
- Polly exponential backoff with jitter for **transient** HTTP/timeout failures (demonstration endpoint `/api/resilience/demo`; deliberately not applied blindly to database writes).
- Hosted background reminder scanner (15-second interval), recurrence and due-event logging. **This does not send push/email notifications**.
- Audit events for item creation, update and deletion.
- xUnit tests and one basic HTTP integration test (run locally to verify).

## Requirements

.NET 8 SDK (a .NET 10 SDK can also build net8.0 with compatible targeting packs), Node.js 20+, npm. No paid API services.

## Run on Windows PowerShell

```powershell
cd D:\LifePilot
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$bytes = New-Object byte[] 48
$rng.GetBytes($bytes)
$env:LIFEPILOT_JWT_KEY = [Convert]::ToBase64String($bytes)
$rng.Dispose()
dotnet restore backend/LifePilot.Api/LifePilot.Api.csproj
dotnet run --project backend/LifePilot.Api/LifePilot.Api.csproj
```

Second terminal:

```powershell
cd D:\LifePilot\frontend
npm install
npm run dev
```

Open `http://localhost:5173`. Backend listens on `http://localhost:5080`; `GET /api/health` returns health status. Frontend uses the Vite proxy to `/api`.

## Test and build

```powershell
dotnet test backend/LifePilot.Tests/LifePilot.Tests.csproj
dotnet build backend/LifePilot.Api/LifePilot.Api.csproj
cd frontend
npm run build
npm audit
```

**Important migration note:** This prototype uses `EnsureCreated()` instead of EF migrations. If you ran an earlier version of LifePilot, its SQLite database may not have the new `Role`, `Reminders`, and `AuditEvents` tables/columns. Back up any data first. For a disposable local test database, stop the server and delete `backend/LifePilot.Api/lifepilot-enterprise.db` (and its `-wal`/`-shm` companions) before restarting. Never delete a database containing data you need.

## Role-based authorization

New accounts always get `Member`; there is no public endpoint to grant admin privileges. `GET /api/admin/stats` requires the `Admin` role. For local development only, you can use a SQLite editor to set an existing test user's role to `Admin`, then sign in again to receive a fresh JWT. Do not use this workflow in production.

## Reminder processing

Use authenticated `POST /api/reminders` with JSON `{"message":"Take a break","dueUtc":"2030-01-01T12:00:00Z","repeatMinutes":60}`. `GET /api/reminders` lists your reminders; `DELETE /api/reminders/{id}` removes one. A background service marks due reminders as delivered and logs their IDs. This is a local demonstration, not a guaranteed delivery system.

## Safety and project scope

Use fictional data only. This project is for personal productivity; do not store real sensitive information or data about other people. The source is under the MIT license. Review `.gitignore` and Git history for secrets before publishing. No license or code artifact proves ownership or an amount of work completed.

## Limitations

This is a learning/portfolio application, not a production security certification. Refresh tokens, account recovery, EF migrations, comprehensive tests, durable reminder delivery, concurrency tests and UI for every backend feature remain improvements. See `ROADMAP.md`.
