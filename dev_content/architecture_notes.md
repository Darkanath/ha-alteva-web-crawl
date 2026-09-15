# Alteva Architecture & Engineering Notes

- **Project:** Alteva Web Crawler System
- **Tracking Directory:** `dev_content/`
- **Last Updated:** 2026-09-15

---

## 1. System Architecture Overview

The system is designed as an asynchronous, event-driven microservices architecture composed of two decoupled services, a message broker, a relational database, and a single-page React frontend.

```mermaid
graph TD
    User([User / Browser]) <--> ReactUI[React 19 Frontend - Vite]
    ReactUI <-->|HTTP REST| CrawlApi[Service A: Crawl API]
    CrawlApi <-->|EF Core / SQL| SqlServer[(SQL Server DB)]
    CrawlApi -->|Publish Job Event| RabbitMQ{RabbitMQ Exchange}
    RabbitMQ -->|Consume Message| CrawlWorker[Service B: Crawl Worker]
    CrawlWorker <-->|EF Core / SQL| SqlServer
    CrawlWorker -->|HTTP Fetch| TargetWebsites([Target Web Pages])
```

---

## 2. Component Boundaries & Responsibilities

| Component | Responsibility | Boundary / Invariants |
| :--- | :--- | :--- |
| **`Alteva.Domain`** | Pure domain logic and models | Zero external dependencies. Enforces URL normalization (RFC 3986), Domain Link Ratio calculation, and tree construction. |
| **`Alteva.Infrastructure`** | Data access & messaging | Entity Framework Core `AppDbContext`, database migrations, RabbitMQ publisher/consumer implementations. |
| **`Alteva.CrawlApi`** | Orchestrator & UI gateway | Validates client requests, writes initial `Pending` jobs to SQL, publishes crawl events to RabbitMQ, serves status & tree views. |
| **`Alteva.CrawlWorker`** | Background crawler | Consumes crawl tasks, executes BFS crawl traversal, extracts hyperlinks, computes metrics, and persists `Pages` and `Edges`. |
| **`frontend`** | User interface | Submits crawl requests, polls status, renders interactive page hierarchy tree and historical crawl runs. |

---

## 3. Event-Driven Design & Reliability Patterns

### Message Contract
- **Queue Name:** `alteva.crawl.jobs`
- **Exchange:** `alteva.crawl.exchange` (direct)
- **Routing Key:** `crawl.job.request`
- **Dead-Letter Exchange (DLQ):** `alteva.crawl.dlx` -> `alteva.crawl.jobs.dlq`

```json
{
  "jobId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "inputUrl": "https://example.com",
  "maxDepth": 2,
  "submittedAt": "2026-09-15T12:00:00Z"
}
```

### Idempotency Strategy
Under at-least-once message delivery, the same crawl message may be delivered more than once (e.g., consumer crash after processing but before acknowledgment).
To guarantee idempotency:
1. **Pages Idempotency:** The `Pages` table enforces a composite unique constraint on `(JobId, Url)`. Duplicate submissions of the same page in a job are safely ignored/upserted.
2. **Edges Idempotency:** The `Edges` table enforces a composite unique constraint on `(JobId, ParentUrl, ChildUrl)`.
3. **Job State Transitions:** State transitions are guarded (e.g. `Pending` -> `Running` -> `Completed`/`Failed`). Duplicate processing checks if the job is already `Completed`.

### Retry & Dead-Letter Handling
1. **Transient Failures:** Transient network glitches (e.g. temporary DNS failure, connection reset, HTTP 503) are retried with exponential backoff.
2. **Poison Messages:** Malformed messages or permanent errors are rejected (`basic.nack` with `requeue = false`) and routed by RabbitMQ to the Dead-Letter Queue (`alteva.crawl.jobs.dlq`).

---

## 4. Domain Link Ratio Specification

$$\text{Domain Link Ratio} = \frac{\text{\# outgoing links within the starting domain}}{\text{total \# outgoing links}}$$

### Rules & Edge Cases
1. **Starting Domain:** The host of the initial job URL (e.g., `example.com`).
2. **Subdomains:** URLs matching `*.example.com` are counted as internal.
3. **Ignored Schemes:** `mailto:`, `tel:`, `javascript:`, and non-HTTP(S) links are filtered out and excluded from the denominator.
4. **Zero Links:** If a page contains 0 valid outgoing links, the ratio evaluates to `0.0`.
5. **Anchor Fragments:** Anchor hashes (e.g., `/page#section`) are stripped during normalization to avoid duplicate pages.

---

## 5. Testing Architecture & Verification Strategy

All tests execute in isolation without requiring external network connectivity or running Docker containers.

```
tests/
├── Alteva.Domain.UnitTests/         # 36 tests: UrlNormalizer, DomainLinkRatio, JobTreeBuilder
├── Alteva.Infrastructure.Tests/     # 4 tests: DbContext unique constraints, SQLite in-memory
├── Alteva.CrawlWorker.Tests/        # 6 tests: Local HTML fixtures, multi-level crawl, safety caps
└── Alteva.CrawlApi.Tests/           # 10 tests: Request validation, DTO constraints
frontend/
└── src/utils/crawlerUtils.test.ts   # 9 tests: Vitest ratio formatting and status badges
```
