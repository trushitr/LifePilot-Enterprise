# LifePilot engineering roadmap

The repository includes initial implementations of JWT auth, role policies, exception handling, idempotency, Polly retry, background reminders, audit events and tests. These are **implemented in source but not independently validated in this generation environment**.

## Further work (not completed)
- Replace `EnsureCreated` with EF Core migrations and schema upgrades.
- Expand test coverage: two-user isolation, role policies, idempotency concurrency, retries, reminders, auth boundaries.
- Build reminders management UI, notifications, and reliable at-least-once delivery with deduplication.
- Add refresh token rotation, logout invalidation, account recovery, and session management.
- Implement production-grade audit retention, observability and accessibility.
- Improve resilience, transaction boundaries, and data backup/restore validation.

Do not represent a future-work estimate as completed hours. Keep genuine Git history; do not fabricate commits or activity.
