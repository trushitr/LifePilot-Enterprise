# LifePilot Enterprise — Local-first productivity platform

LifePilot Enterprise is an AI-assisted, local-first full-stack productivity prototype built with **ASP.NET Core 8**, **React/TypeScript**, **EF Core**, and **SQLite**. It runs locally without paid AI APIs or hosted services. It is a portfolio/learning project, not a production-hardened service.

## Features

- Registration and login with salted PBKDF2 password hashing and JWT bearer authentication.
- Member/Admin role-based authorization; new accounts receive the Member role.
- Task, grocery, and calendar item APIs with user-specific data access.
- JSON backup export and duplicate-aware restore that skips records already present for the user.
- Input validation and centralized Problem Details error responses.
- Idempotent item creation using an `Idempotency-Key` header, request fingerprint, and SQLite transaction.
- Authentication rate limiting, health checks, and item-change audit events.
- Polly-based retry demonstration for transient HTTP/timeout failures at `/api/resilience/demo`.
- Background reminder scanning approximately every 15 seconds, with recurrence and database delivery state. **No email, push, or desktop notification delivery.**
- Recurring tasks (daily, weekly, monthly with an interval) with a due time, time zone, optional end date and reminder lead time. Every occurrence has its own completion state; a background scheduler materializes occurrences, recovers work missed while the API was stopped, never duplicates occurrences or reminders, and raises **in-app** reminders (shown under the bell icon in the UI). Monthly schedules keep the intended day of month (January 31 → February 28 → March 31).
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

The recurring-task work adds three unit-test classes (`RecurrenceTests`, `RecurringTaskValidationTests`, `RecurrenceSeekTests`: 83 cases covering daily/weekly/monthly rules including Jan 31 → Feb 28 → Mar 31, leap years, intervals, end dates, calendar limits, arithmetic positioning for decades-old schedules, strict date/time parsing, time-zone and DST handling of due times and reminder leads, and every validation rule) and `RecurringTaskApiTests` (25 API tests: authentication boundaries, creation and skipping of already-past occurrences, invalid payloads, monthly day-of-month preservation with per-occurrence completion, recovery of missed occurrences and reminders after a simulated restart without duplicates under concurrent scheduler passes, a 600-day outage recovered across capped passes, long reminder leads, reminder settlement on completion including a concurrent completion/scheduler race, delete/disable racing the worker, two API instances sharing one database, concurrent initialisation of a new database and two hosts starting simultaneously, upgrade of an older database on startup with legacy reminders still settling, catch-up reminder summaries that stay until every covered occurrence is completed, disable/enable never repeating a seen reminder, end-date exhaustion, preview, daylight-saving wall-clock due times with whole-day and short reminder leads, per-user isolation, deletion, and unchanged backup/restore behaviour). The API tests drive the scheduler explicitly with a fake clock instead of waiting for the background worker. The full suite is 130 tests (22 original + 108 recurring-task tests); run `dotnet test` to confirm on your machine.

The API integration tests exercise health/authentication boundaries, registration and login, incorrect-password rejection, member-only authorization boundaries, user data isolation, idempotency replay, invalid-item validation, and backup/restore (authentication boundary, first import, repeated import, mixed new and existing records, identical rows within one backup, invalid payload rejection, malformed JSON and wrong content type, empty backups, the 5000-record boundary, per-user isolation, concurrent identical restores, and the export shape). Unit tests cover salted password hashes and transient/non-transient retry behavior.

## Authorization

New accounts are assigned the `Member` role. There is no public endpoint to grant `Admin`. The `GET /api/admin/stats` endpoint requires `Admin` authorization. For **local development only**, an existing test user's role can be changed in a SQLite editor; sign in again to obtain a token with the updated role. Do not treat this as a production administration workflow.

## Item creation and idempotency

Authenticated `POST /api/items` requests require an `Idempotency-Key` header. Repeating a request with the same key and payload returns the previously created item instead of creating a duplicate. Reusing the key with different payload returns HTTP 409. Concurrent requests and key-retention policies need additional hardening.

## Backup and restore

`GET /api/backup` exports the authenticated user's items as a JSON array of `{ kind, title, category, amount, date }` objects. The export intentionally contains no database IDs or user IDs, so it is portable across accounts and machines.

`POST /api/restore` accepts that array and imports it without creating duplicates:

- **Identity without IDs.** A record in the backup is treated as already present when an existing record of the same user matches on the full combination of `kind`, `title`, `category`, `amount`, and `date` (after the same trimming and defaulting that `POST /api/items` applies). Each stored record can match only one incoming record, so a backup that legitimately contains two identical rows restores both into an account that holds just one, while re-importing the same backup a second time imports nothing.
- **Validate first, then write.** The whole payload is checked (JSON array, at most 5000 entries, no `null` entries, per-item kind/title/amount/date rules) before any database change. An invalid entry rejects the entire import with HTTP 400 and an error message naming the offending index; nothing is partially imported.
- **Malformed input.** Unparseable JSON, JSON of the wrong shape (an object, a string, numbers in the array, a non-numeric `amount`), a `null` body or an empty body all return HTTP 400 with a Problem Details body; a non-JSON `Content-Type` returns 415. Unknown properties such as `id` or `userId` are ignored.
- **Empty backups.** A new account exports `[]`; restoring `[]` succeeds with `imported: 0, skipped: 0, total: 0` and touches nothing.
- **Large backups.** Each restore request accepts at most 5000 records (the 5001st is rejected before any write). `GET /api/backup` is not capped, so the web UI splits larger files into batches of 5000 and sums the counts; because already-present records are skipped, re-importing after a failed batch never duplicates data. Since the "one stored record satisfies one incoming record" rule applies per request, the UI keeps identical records together in the same batch (which reorders records only for backups larger than one batch).
- **Owned by the caller.** Restored records are always assigned to the authenticated user. Any `id` or `userId` fields in the payload are ignored, and another user's identical records are never counted as duplicates.
- **Safe under concurrency.** The duplicate check and the inserts run in a single `BEGIN IMMEDIATE` transaction, so two restores of the same backup submitted at the same time are serialized by SQLite: one imports, the other sees those rows and skips them.
- **Response.** `{ "imported": n, "skipped": n, "total": n }` where `imported` is the number of new records written, `skipped` the number already present, and `total` the number of entries in the payload.

## Reminders

Create a reminder with authenticated `POST /api/reminders`:

```json
{"message":"Take a break","dueUtc":"2030-01-01T12:00:00Z","repeatMinutes":60}
```

`GET /api/reminders` lists reminders; `DELETE /api/reminders/{id}` removes one. The background worker updates reminder delivery state and logs due reminders. **It does not send notifications or guarantee external delivery.**

## Recurring tasks and smart reminders

Recurring tasks are separate from one-off items. A *schedule* describes the rule; the scheduler turns it into *occurrences*, each of which is completed individually; when an occurrence's reminder time arrives an in-app *reminder* is raised.

Create a schedule with authenticated `POST /api/recurring-tasks`:

```json
{"title":"Pay rent","category":"Home","frequency":"monthly","interval":1,"startDate":"2026-01-31","dueTime":"09:00","timeZoneId":"Europe/Berlin","reminderLeadMinutes":1440,"endDate":null}
```

- `frequency` is `daily`, `weekly` or `monthly`; `interval` (default 1) means every N days/weeks/months (maximum 365/52/12).
- `startDate` (`yyyy-MM-dd`) is the anchor: weekly schedules keep its weekday, monthly schedules keep its day of month. Dates are clamped to shorter months but the anchor is preserved, so a task for the 31st runs on Jan 31, Feb 28 (29 in leap years), Mar 31, Apr 30, May 31. The anchor may be in the past (up to 10 years); occurrences that would already be in the past when the schedule is created are skipped, not generated as overdue. A daily 09:00 task created at 10:00 therefore starts tomorrow.
- `dueTime` (`HH:mm`) is local wall-clock time in `timeZoneId` (an IANA id such as `America/New_York`; blank means UTC). Daylight-saving transitions are handled: a time that does not exist on the switch-over day moves to the first valid instant after the gap.
- `reminderLeadMinutes` is how long before the due time the reminder fires (0 = at the due time, up to 30 days); `null` disables reminders. `endDate` (optional) stops the schedule.
- Validation rejects blank or overlong titles, unknown frequencies, out-of-range intervals, malformed dates/times, unknown time zones, negative lead times, an end date before the start date, and schedules that would never run again; errors are HTTP 400 Problem Details like the rest of the API.

Other endpoints (all authenticated, all scoped to the caller's data):

- `GET /api/recurring-tasks` — schedules with a description, the next due date and a preview of the next three dates, plus open/completed counts. `GET /api/recurring-tasks/{id}` returns one.
- `POST /api/recurring-tasks/preview` — same body as create; returns the next six dates without saving anything (the UI shows this while you type).
- `POST /api/recurring-tasks/{id}/disable` — stops scheduling and withdraws the upcoming occurrences whose reminder has not been shown yet; overdue, completed and already-reminded occurrences remain as history (see *Disable and enable* below). `POST /api/recurring-tasks/{id}/enable` resumes with the next *future* occurrence (the gap is not backfilled). `DELETE /api/recurring-tasks/{id}` removes the schedule with its occurrences and reminders.
- `GET /api/task-occurrences?includeCompleted=true&recurringTaskId=1` — occurrences ordered by due time (oldest first, at most 500), each with `status` = `upcoming` | `overdue` | `completed` and `scheduleActive`. `PUT /api/task-occurrences/{id}` with `{"completed":true|false}` completes or reopens a single occurrence (completing also marks its reminder as read).
- `GET /api/task-reminders?unreadOnly=true`, `POST /api/task-reminders/{id}/read`, `POST /api/task-reminders/read-all` — the in-app reminders.

How the scheduler works: every 15 seconds (and immediately at startup) the `RecurringTaskWorker` runs one pass. For each active schedule it creates every occurrence whose due time *or reminder time* has already passed, plus at least one occurrence with a future due time (so a two-day reminder lead produces two upcoming occurrences and still fires two days ahead), then handles reminders for every open occurrence whose reminder time has passed. Because state lives in the database (each occurrence carries its sequence number and records when its reminder was raised), a pass after a restart simply continues where the last one stopped, and every missed occurrence is created with its own completion state.

**Missed work is summarised, not flooded.** Occurrences that are still upcoming get one reminder each, as usual. Occurrences that are already overdue when their reminder is handled can only have been missed while the API was stopped (while it runs, reminders are raised before or at the due time), so when a schedule has several of them in one pass they share a **single catch-up reminder** — `"Water plants" has 21 missed occurrences (2026-01-16 – 2026-02-05) that are still open.` Weeks of downtime therefore produce one notification per schedule while every missed occurrence stays listed and completable. Very long outages are processed in chunks of 500 occurrences per pass, each chunk producing one catch-up reminder. Each reminder records which occurrences it covers (`TaskReminderOccurrences`): as you complete them the summary's text is updated (`…; 2 of them are still open.`) and it is only marked read once **all** of them are completed; reopening an occurrence refreshes the text but never un-dismisses a reminder that has already settled. An ordinary reminder covers one occurrence and is settled by completing it.

**Duplicates are prevented twice over:** passes and API writes (create, disable, enable, delete, complete) run inside serializable (`BEGIN IMMEDIATE`) SQLite transactions, and the database enforces `UNIQUE (RecurringTaskId, SequenceNumber)` for occurrences and `UNIQUE (OccurrenceId)` for reminders. A completed occurrence never receives a reminder, and completing one marks its reminder read even if the worker raised it at the same moment.

**Disable and enable.** Disabling withdraws the upcoming occurrences the user has *not* been told about yet; overdue, completed and already-reminded occurrences stay (they are marked `scheduleActive: false` in the occurrence list). Enabling resumes with the next future date — the gap is never backfilled — and continues from any kept occurrence, so a reminder that was already shown or dismissed is never raised again. Enabling is refused (HTTP 400) only when the schedule has no future date left *and* no open upcoming occurrence.

**Time zones and daylight saving.** Due times are wall-clock times in the schedule's zone: a 09:00 task is at 09:00 local every day, so consecutive UTC instants are 23 or 25 hours apart around a transition, and a time that does not exist on the spring-forward day moves to the first valid instant after the gap. Reminder leads that are whole days (`1440`, `2880`, …) follow the same rule — "1 day before" means 09:00 local the day before, even across the change. Shorter leads are exact durations before the due instant: a 60-minute reminder is always 60 real minutes early, because wall-clock arithmetic inside the missing hour would be meaningless. Reminder messages always quote the local due date, time and zone.

**Several API instances on one database** (for example two processes pointed at the same SQLite file) are safe: each pass takes the SQLite writer lock before reading a schedule, waits (default 30-second busy timeout) rather than failing if the other instance holds it, and the unique indexes rule out duplicates regardless. The work is simply done by whichever instance gets there first; the other finds nothing to do. Reminders are in-app only; nothing is emailed or pushed.

Recurring schedules are **not** part of the item backup (`GET /api/backup` / `POST /api/restore` are unchanged and still export/import items only).

## Database and migration note

This prototype uses EF Core `EnsureCreated()` rather than migrations. Startup (`SchemaBootstrap.InitializeAsync`) wraps it so that it is safe and additive: if two API instances start on a brand-new file at the same moment, the one whose `CREATE` loses the race continues instead of crashing with "table already exists"; and on every start the model's `CREATE TABLE` / `CREATE INDEX` script is re-run with `IF NOT EXISTS` inside a write transaction, so tables introduced by a newer version (such as the recurring-task tables and `TaskReminderOccurrences`) are added to an existing database. Startup then verifies that every table the model expects is present and fails fast with a clear message otherwise. Changes to the columns of existing tables are still **not** migrated. If you previously ran a different version, back up important data before making changes. For a **disposable test database only**, stop the API, locate its `lifepilot-enterprise.db` file and associated `-wal`/`-shm` files, and delete them before restarting. Never delete a database containing data you need.

## Security and scope

Use fictional data only. Do not store personal secrets, regulated information, or real sensitive records. Review tracked files and Git history for accidental secrets before publishing. The project is distributed under the MIT license.

## Limitations and planned improvements

This is a learning/portfolio prototype. Missing production capabilities include refresh-token rotation, account recovery, schema migrations, durable notification delivery, stronger concurrency testing, broader API coverage, observability, and full UI coverage of backend features. See [architecture notes](docs/ARCHITECTURE.md) and [roadmap](ROADMAP.md).

This repository was developed with AI assistance. Project history, test evidence, and stated development hours should reflect actual work; source code or a license alone does not establish time spent or authorship.
