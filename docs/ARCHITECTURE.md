# LifePilot Enterprise — Architecture

## System overview

```text
Browser
  |
  v
React + TypeScript SPA (Vite development server, /api proxy)
  |
  v
ASP.NET Core 8 Minimal API
  |-- JWT authentication and Member/Admin authorization
  |-- Validation, exception handling, rate limiting
  |-- Item, reminder, audit, backup/restore and rule-based assistant endpoints
  |-- Polly transient-retry demonstration
  |-- Background reminder worker
  |-- Recurring task scheduler and worker (occurrences, in-app reminders)
  |-- Task activity history (who changed which task, when, old/new values)
  |
  v
Entity Framework Core
  |
  v
Local SQLite database
```

This is a single-machine prototype; it is not a distributed or multi-tenant production service.

## Implemented components

**API and security:** ASP.NET Core Minimal APIs use JWT bearer tokens. New accounts receive `Member`; `/api/admin/stats` requires `Admin`. Protected endpoints derive user identity from the validated token and restrict item access to the authenticated user. Passwords are stored as salted PBKDF2 hashes. Authentication endpoints use rate limiting.

**Persistence:** EF Core stores users, items, reminders, recurring tasks, task occurrences, task reminders, task activity history, idempotency records, and audit events in SQLite. The application initializes the schema with `EnsureCreated()`, not EF Core migrations, wrapped by `SchemaBootstrap.InitializeAsync`: a concurrent creator winning the race is tolerated, the model's create script is re-executed as `CREATE ... IF NOT EXISTS` on every start (adds new tables and indexes, cannot alter existing columns), and the presence of every expected table is verified before the API starts serving.

**Idempotency:** `POST /api/items` requires `Idempotency-Key`. The API computes a payload fingerprint and stores the created item and idempotency record in a transaction. A repeated matching request returns the existing item; key reuse with a different payload returns HTTP 409. Concurrent-request safety and record retention require additional work.

**Backup/restore:** `GET /api/backup` exports a user's items without database or user IDs. `POST /api/restore` validates the whole payload first, then, inside one serializable transaction, loads the caller's existing items, identifies each incoming record by the combination of kind, title, category, amount and date (`ItemKey`), skips records that are already present (each stored record satisfies at most one incoming record) and inserts the rest for the authenticated user. It returns imported, skipped and total counts. Because Microsoft.Data.Sqlite starts a serializable transaction with `BEGIN IMMEDIATE`, the writer lock is held across the read and the inserts, so concurrent restores (or a concurrent `POST /api/items`) cannot both observe the same record as absent.

**Errors:** Validation exceptions produce HTTP 400. `RouteHandlerOptions.ThrowOnBadRequest` is enabled in every environment so framework binding failures (unparseable JSON, wrong JSON shape, wrong content type) reach the central handler as `BadHttpRequestException` and keep their own 4xx status with a Problem Details body, rather than an empty response in Production and a different body in Development; unexpected failures produce HTTP 500 Problem Details responses. Other endpoints return appropriate HTTP results for authentication and authorization failures.

**Resilience:** A Polly-based service retries selected transient HTTP/timeout failures with exponential backoff and jitter. The `/api/resilience/demo` endpoint demonstrates the behavior. Retry logic is not blindly applied to database writes.

**Reminders:** A hosted background worker scans for due reminders approximately every 15 seconds and updates database delivery state, including recurring reminders. It does not deliver email, push, or desktop notifications.

**Recurring tasks:** `Recurrence.cs` holds the pure rule engine (`RecurrenceRule.DateAt(sequence)` derives every date from the anchor, so monthly schedules keep their intended day of month and nothing drifts; `Recurrence.ToUtc` converts local wall-clock times per time zone and handles DST gaps). `RecurringTaskModels.cs` holds the entities (`RecurringTask` schedule, `TaskOccurrence`, `TaskReminder`), request/response contracts and validation; it depends only on the BCL so it is unit-testable without a database. `RecurringTasks.cs` holds the `RecurringTaskScheduler` (reminders record the occurrences they cover in `TaskReminderOccurrences`, so a catch-up summary is settled only when all of them are completed) (scoped; per active schedule materializes every occurrence whose due or reminder time has passed plus at least one future occurrence, then raises in-app reminders for open occurrences whose reminder time has passed — one per upcoming occurrence, but a single catch-up reminder for the occurrences of a schedule that were missed while the API was stopped; the latest stored occurrence, not a counter, decides where to continue), the `RecurringTaskWorker` (a pass at startup for catch-up, then every 15 seconds), the minimal-API endpoints and the additive schema bootstrap. Time is read through `TimeProvider` so tests can control it. Concurrency: every pass and every API mutation of a schedule runs in a serializable (`BEGIN IMMEDIATE`) transaction, and unique indexes on `(RecurringTaskId, SequenceNumber)` and `(OccurrenceId)` make duplicate occurrences or reminders impossible even if two writers overlap. Reminders are database rows surfaced in the UI; there is no external delivery.

**Task activity history:** `TaskActivity.cs` holds the append-only `TaskActivity` entity (owner, actor, task type and id, title snapshot, optional detail, action, JSON change list, UTC time), the pure helpers (`ItemSnapshot` + `TaskActivityChanges.Diff` compute field-level old/new values for an item update; the JSON change list is stored in the same camelCase shape the API returns), the `TaskActivityLog` recorder the item, recurring-task and occurrence endpoints call inside their existing transaction / `SaveChanges`, and `GET /api/task-activity` (per-owner, newest first, filters by type/task/action, cursor paging, actor e-mail joined at read time). History rows reference tasks by id without a foreign key so they outlive deletion. Only user actions are recorded; the scheduler's own work is not. `PUT`/`DELETE /api/items/{id}` now run in a serializable (`BEGIN IMMEDIATE`) transaction, like `POST /api/items` and the recurring-task endpoints, so overlapping duplicates of one request cannot each record the same change (the second reads the already-updated row and is a no-op) and a second delete is a 404 rather than a concurrency error. The item endpoints read the clock through `TimeProvider` so tests can assert timestamps. `AuditEvents` remains as the minimal security log.

**Assistant:** The assistant endpoint uses predefined/rule-based command matching, not a hosted language model.

**Frontend:** React/TypeScript communicates with the API via the Vite development proxy. `history.tsx` renders the History tab (filters, per-task view, paging); item rows offer inline editing (only changed fields are sent) and a shortcut to their history. Not every backend feature has a corresponding UI.

## Verification

On October 30, 2026, 10 local .NET tests passed: 7 API integration tests and 3 unit tests. GitHub Actions **Build and test** also passed on commit `d8e7f8e`. Tests cover selected authentication, authorization, isolation, validation, idempotency, password-hashing, and retry scenarios. They do not prove production security or complete coverage.

## Current constraints

- SQLite and `EnsureCreated()` are intended for local prototyping; schema upgrades require migrations.
- JWT refresh/revocation, account recovery, and production account administration are not implemented.
- Reminder delivery is local database state/logging only, without external notification guarantees.
- Concurrent idempotency requests, retention/cleanup, backup/restore robustness, and more edge cases need testing.
- Logging, monitoring, deployment hardening, and accessibility need further work.

## Planned improvements

Introduce EF Core migrations, stronger domain boundaries, refresh-token/session management, durable notifications, concurrency and reminder-worker tests, more complete observability, and expanded frontend functionality. ASP.NET Core Identity may be considered for mature account management; it is not part of the current implementation.
