# LifePilot Enterprise — Engineering roadmap

This roadmap separates features already implemented from work still planned. The project is a local-first portfolio prototype, not a production-ready service.

## Implemented and verified in automated tests

- [x] Salted PBKDF2 password hashing and password verification.
- [x] Registration/login and JWT-protected API access.
- [x] Member role denied access to an Admin-only endpoint.
- [x] Isolation of one user's items from another user.
- [x] Idempotent replay of item creation without duplication.
- [x] Invalid item input returns HTTP 400.
- [x] Polly retry recovery for a transient failure, and no retry for a non-transient failure.
- [x] Health endpoint and unauthenticated API rejection.
- [x] GitHub Actions backend tests and frontend build.

**Evidence:** 10 local .NET tests passed on October 30, 2026 (7 API integration + 3 unit); GitHub Actions **Build and test** passed on commit `d8e7f8e`.

## Recurring tasks & smart reminders (implemented)

- [x] Daily, weekly and monthly schedules with interval, due time, time zone, reminder lead time and optional end date.
- [x] Monthly recurrence preserves the intended day of month (Jan 31 → Feb 28 → Mar 31); leap years, intervals and end dates covered by unit tests.
- [x] Per-occurrence completion; disable/enable without backfilling the disabled gap; delete with cascade.
- [x] Scheduler recovers missed occurrences after a restart (one catch-up reminder per schedule instead of one per missed occurrence); duplicates prevented by serializable transactions plus unique indexes; safe with several API instances on one database.
- [x] Disabling keeps occurrences whose reminder was already shown, so re-enabling never repeats a seen or dismissed reminder.
- [x] Whole-day reminder leads follow the wall clock in the schedule's time zone across daylight-saving changes; shorter leads are exact durations.
- [x] In-app reminders (bell + panel) and React UI for creating schedules, previewing dates, viewing and completing occurrences.
- [x] Unit tests for the rule engine and validation; API tests with a fake clock for the scheduler and endpoints.
- [ ] External reminder delivery (email/push) and per-schedule editing of an existing rule remain future work.

## Implemented in source; needs broader automated verification

- [ ] Reminder scanning, due processing, and recurrence edge cases.
- [ ] Audit-event behavior for create/update/delete.
- [ ] Backup/restore correctness and error handling. Duplicate-aware restore (field-combination identity, whole-payload validation before writes, per-user scoping, imported/skipped counts) is implemented and has integration tests; the green run still needs to be recorded here once executed.
- [ ] Additional authorization boundaries for update/delete/reminders.
- [ ] Idempotency under concurrent requests and key expiration/retention.
- [ ] More frontend interaction and accessibility tests.

## Planned features and hardening

- [ ] Replace `EnsureCreated()` with EF Core migrations and safe schema upgrades.
- [ ] Add refresh-token rotation, token revocation/logout invalidation, and account recovery.
- [ ] Add external notifications for reminders (email/push/desktop); in-app reminders exist.
- [ ] Introduce durable reminder delivery, deduplication, and retry semantics.
- [ ] Improve production observability, audit retention, and security controls.
- [ ] Strengthen transaction handling, backup/restore validation, and database resilience.
- [ ] Expand automated test coverage and deployment documentation.

## Project integrity

Keep accurate Git history and describe AI assistance truthfully. Do not invent development hours, test outcomes, commits, or completed features.
