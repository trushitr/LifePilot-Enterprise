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

**Persistence:** EF Core stores users, items, reminders, idempotency records, and audit events in SQLite. The application currently initializes the schema with `EnsureCreated()`, not EF Core migrations.

**Idempotency:** `POST /api/items` requires `Idempotency-Key`. The API computes a payload fingerprint and stores the created item and idempotency record in a transaction. A repeated matching request returns the existing item; key reuse with a different payload returns HTTP 409. Concurrent-request safety and record retention require additional work.

**Errors:** Validation exceptions produce HTTP 400; unexpected failures produce HTTP 500 Problem Details responses. Other endpoints return appropriate HTTP results for authentication and authorization failures.

**Resilience:** A Polly-based service retries selected transient HTTP/timeout failures with exponential backoff and jitter. The `/api/resilience/demo` endpoint demonstrates the behavior. Retry logic is not blindly applied to database writes.

**Reminders:** A hosted background worker scans for due reminders approximately every 15 seconds and updates database delivery state, including recurring reminders. It does not deliver email, push, or desktop notifications.

**Assistant:** The assistant endpoint uses predefined/rule-based command matching, not a hosted language model.

**Frontend:** React/TypeScript communicates with the API via the Vite development proxy. Not every backend feature has a corresponding UI.

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
