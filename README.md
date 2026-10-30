# LifePilot Enterprise — Local-first productivity platform

LifePilot Enterprise is an AI-assisted, local-first full-stack productivity prototype built with **ASP.NET Core 8**, **React/TypeScript**, **EF Core**, and **SQLite**. It runs locally without paid AI APIs or hosted services. It is a portfolio/learning project, not a production-hardened service.

## Features

- Registration and login with salted PBKDF2 password hashing and JWT bearer authentication.
- Member/Admin role-based authorization; new accounts receive the Member role.
- Task, grocery, and calendar item APIs with user-specific data access.
- Input validation and centralized Problem Details error responses.
- Idempotent item creation using an `Idempotency-Key` header, request fingerprint, and SQLite transaction.
- Authentication rate limiting, health checks, and item-change audit events.
- Polly-based retry demonstration for transient HTTP/timeout failures at `/api/resilience/demo`.
- Background reminder scanning approximately every 15 seconds, with recurrence and database delivery state. **No email, push, or desktop notification delivery.**
- Rule-based assistant endpoint (not an LLM integration).
- Automated unit and API integration tests; GitHub Actions backend test and frontend build workflows.

## Requirements

- .NET 8 SDK, or a newer compatible SDK with .NET 8 targeting support
- Node.js 20+ and npm
- Windows PowerShell for the commands below (equivalent commands work on other systems)

## Run locally

Clone the repository and open PowerShell **in the cloned repository root** (the folder containing `backend` and `frontend`).

Set a fresh local JWT signing key for this terminal session:

```powershell
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$bytes = New-Object byte[] 48
$rng.GetBytes($bytes)
$env:LIFEPILOT_JWT_KEY = [Convert]::ToBase64String($bytes)
$rng.Dispose()

dotnet restore backend/LifePilot.Api/LifePilot.Api.csproj
dotnet run --project backend/LifePilot.Api/LifePilot.Api.csproj
```

In a **second terminal**, from the same repository root:

```powershell
cd frontend
npm install
npm run dev
```

Open `http://localhost:5173`. The API listens at `http://localhost:5080`, and the frontend development server proxies `/api` requests to it. Check `http://localhost:5080/api/health` for the API health endpoint.

**Important:** The JWT environment variable is set per terminal session. Set it again when opening a new terminal to run the API. Do not commit your signing key.

## Test and build

From the repository root:

```powershell
dotnet test backend/LifePilot.Tests/LifePilot.Tests.csproj
dotnet build backend/LifePilot.Api/LifePilot.Api.csproj

cd frontend
npm run build
npm audit
```

**Verification as of October 30, 2026:** 10 local .NET tests passed (7 API integration tests and 3 unit tests). GitHub Actions **Build and test** succeeded on commit `d8e7f8e`, covering the configured backend test and frontend build jobs. These results are limited to the checked scenarios and are not a production security audit.

The API integration tests exercise health/authentication boundaries, registration and login, incorrect-password rejection, member-only authorization boundaries, user data isolation, idempotency replay, and invalid-item validation. Unit tests cover salted password hashes and transient/non-transient retry behavior.

## Authorization

New accounts are assigned the `Member` role. There is no public endpoint to grant `Admin`. The `GET /api/admin/stats` endpoint requires `Admin` authorization. For **local development only**, an existing test user's role can be changed in a SQLite editor; sign in again to obtain a token with the updated role. Do not treat this as a production administration workflow.

## Item creation and idempotency

Authenticated `POST /api/items` requests require an `Idempotency-Key` header. Repeating a request with the same key and payload returns the previously created item instead of creating a duplicate. Reusing the key with different payload returns HTTP 409. Concurrent requests and key-retention policies need additional hardening.

## Reminders

Create a reminder with authenticated `POST /api/reminders`:

```json
{"message":"Take a break","dueUtc":"2030-01-01T12:00:00Z","repeatMinutes":60}
```

`GET /api/reminders` lists reminders; `DELETE /api/reminders/{id}` removes one. The background worker updates reminder delivery state and logs due reminders. **It does not send notifications or guarantee external delivery.**

## Database and migration note

This prototype uses EF Core `EnsureCreated()` rather than migrations. Schema changes are **not** automatically migrated. If you previously ran a different version, back up important data before making changes. For a **disposable test database only**, stop the API, locate its `lifepilot-enterprise.db` file and associated `-wal`/`-shm` files, and delete them before restarting. Never delete a database containing data you need.

## Security and scope

Use fictional data only. Do not store personal secrets, regulated information, or real sensitive records. Review tracked files and Git history for accidental secrets before publishing. The project is distributed under the MIT license.

## Limitations and planned improvements

This is a learning/portfolio prototype. Missing production capabilities include refresh-token rotation, account recovery, schema migrations, durable notification delivery, stronger concurrency testing, broader API coverage, observability, and full UI coverage of backend features. See [architecture notes](docs/ARCHITECTURE.md) and [roadmap](ROADMAP.md).

This repository was developed with AI assistance. Project history, test evidence, and stated development hours should reflect actual work; source code or a license alone does not establish time spent or authorship.
