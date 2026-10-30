# Architecture notes

React SPA (Vite dev proxy) -> ASP.NET Core Minimal API -> EF Core -> local SQLite.

Authentication: login issues a short-lived JWT; API uses bearer validation. Authorization: `/api` item endpoints require a token and filter by the authenticated user ID.

Idempotency: POST `/api/items` requires an Idempotency-Key, records the request fingerprint and created item in one database transaction. The same key and payload returns the existing item. Key reuse with a different payload returns 409. This is an early implementation and needs concurrency and retention tests.

Exceptions: validation errors become 400 responses; unhandled errors become 500 Problem Details responses.

**Planned:** ASP.NET Core Identity, refresh tokens, role-based authorization, Polly for transient outbound calls, hosted reminder workers, domain boundaries, migration-based database management, comprehensive tests.
