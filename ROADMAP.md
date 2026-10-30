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

## Implemented in source; needs broader automated verification

- [ ] Reminder scanning, due processing, and recurrence edge cases.
- [ ] Audit-event behavior for create/update/delete.
- [ ] Backup/restore correctness and error handling.
- [ ] Additional authorization boundaries for update/delete/reminders.
- [ ] Idempotency under concurrent requests and key expiration/retention.
- [ ] More frontend interaction and accessibility tests.

## Planned features and hardening

- [ ] Replace `EnsureCreated()` with EF Core migrations and safe schema upgrades.
- [ ] Add refresh-token rotation, token revocation/logout invalidation, and account recovery.
- [ ] Build fuller reminder management UI and external notifications.
- [ ] Introduce durable reminder delivery, deduplication, and retry semantics.
- [ ] Improve production observability, audit retention, and security controls.
- [ ] Strengthen transaction handling, backup/restore validation, and database resilience.
- [ ] Expand automated test coverage and deployment documentation.

## Project integrity

Keep accurate Git history and describe AI assistance truthfully. Do not invent development hours, test outcomes, commits, or completed features.
