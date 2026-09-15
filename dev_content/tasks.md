# Alteva Detailed Task Backlog

- **Project:** Alteva Web Crawler System
- **Tracking Directory:** `dev_content/`
- **Last Updated:** 2026-09-15

---

## 1. Domain Layer (`Alteva.Domain`)

- [x] **Task 1.1: Core Entities Definition**
  - [x] Create `Job` entity with lifecycle timestamps and `JobStatus` enum (`Pending`, `Running`, `Completed`, `Failed`, `Canceled`).
  - [x] Create `Page` entity with `JobId`, normalized `Url`, and `DomainLinkRatio`.
  - [x] Create `Edge` entity modeling directed links (`JobId`, `ParentUrl`, `ChildUrl`).
- [x] **Task 1.2: URL Normalization Service**
  - [x] Implement RFC 3986 relative path resolution against parent base URLs.
  - [x] Strip anchor fragments (`#section`).
  - [x] Lowercase schemes and hostnames.
  - [x] Filter out non-HTTP/HTTPS schemes (`mailto:`, `tel:`, `javascript:`, `data:`).
  - [x] Strip standard default ports (80/443).
- [x] **Task 1.3: Domain Link Ratio Calculation Engine**
  - [x] Implement formula: `(# internal links) / (total valid outgoing links)`.
  - [x] Handle edge cases: 0 outgoing links returns 0.0; non-HTTP schemes ignored from denominator.
- [x] **Task 1.4: Hierarchical Tree Builder**
  - [x] Model `JobTreeNode` with recursive `Children` list.
  - [x] Convert flat `Page` and `Edge` collections into cycle-safe tree.
- [x] **Task 1.5: HTML Link Extractor**
  - [x] High-performance compiled regex extractor for anchor tags.

---

## 2. Infrastructure & Data Layer (`Alteva.Infrastructure`)

- [x] **Task 2.1: AppDbContext Model Configuration**
  - [x] Define entity mappings for SQL Server with appropriate field lengths.
  - [x] Create unique composite index `(JobId, UrlHash)` on `Pages` for idempotency (was `(JobId, Url)`; see 8.1).
  - [x] ~~Unique index `(JobId, ParentUrl, ChildUrl)` on `Edges`~~ — removed (exceeded SQL Server's key size); edges are de-duplicated per page (see 8.1).
  - [x] Configure cascading foreign keys from `Job` to `Pages` and `Edges`.
- [x] **Task 2.2: Initial EF Core Migration**
  - [x] Generate initial migration for schema creation.
- [x] **Task 2.3: Message Broker Abstraction**
  - [x] Define `IMessagePublisher` interface.
  - [x] Implement RabbitMQ publisher using exchange/queue configuration with durable queues.
  - [x] Implement dead-letter exchange (`dlx`) and DLQ binding with persistent delivery.

---

## 3. Crawl API (`Alteva.CrawlApi`)

- [x] **Task 3.1: DTO Models & Validation**
  - [x] Create `CreateCrawlJobRequest` with URL format and depth validation (`[Range(1, 10)]`).
  - [x] Create `CreateCrawlJobResponse`, `CrawlJobDetailsResponse`, and `PaginatedListResponse`.
- [x] **Task 3.2: Jobs Controller Implementation**
  - [x] `POST /api/jobs`: Persist new job with its root page and enqueue the root `CrawlPageMessage` (see 8.3).
  - [x] `GET /api/jobs/{id}`: Retrieve job status and assembled page tree.
  - [x] `GET /api/jobs`: Paginated history list sorted by `CreatedAt DESC`.
  - [x] `POST /api/jobs/{id}/cancel`: Mark pending or running job as `Canceled`.
- [x] **Task 3.3: Health Check & Swagger**
  - [x] Enable `/health` endpoint for Docker container health check.
  - [x] Enable CORS policy for frontend client communication.
  - [x] Document endpoints via Swagger UI.

---

## 4. Crawl Worker (`Alteva.CrawlWorker`)

- [x] **Task 4.1: Crawler Engine Core** — superseded by the recursive page crawl (section 8)
  - [x] BFS queue traversal with depth limiting.
  - [x] Visited URL deduplication set.
  - [x] Max pages safety cutoff (default 200).
  - [x] Error handling for HTTP status codes and timeouts.
- [x] **Task 4.2: RabbitMQ Background Consumer**
  - [x] Consume crawl messages from queue with prefetch limit (`BasicQos(0, 1, false)`) — now `CrawlPageMessage` (see 8.3).
  - [x] Update Job status to `Running` with `StartedAt` timestamp.
  - [x] Save discovered pages and edges to database in batches or transaction.
  - [x] Update Job status to `Completed` or `Failed` with `CompletedAt`.
  - [x] Implement retry policy for transient DB/network errors — revised: HTTP retries in-process, infrastructure failures requeued, DLQ only for malformed messages (see 8.5).

---

## 5. Frontend Application (`frontend/`)

- [x] **Task 5.1: Setup & Utilities**
  - [x] Install and configure Vitest test runner.
  - [x] Implement ratio formatting and status badge utility functions.
- [x] **Task 5.2: API Service Layer**
  - [x] Implement typed HTTP client for Crawl API (`startJob`, `getJobDetails`, `getJobHistory`).
- [x] **Task 5.3: Core UI Screens**
  - [x] **Start Crawl Screen**: Form with URL input, max depth selector, and error handling.
  - [x] **Job Details Screen**: Status badge, timestamps, polling progress, and recursive interactive tree view.
  - [x] **History Screen**: Paginated list/table of past crawls with status chips.
- [x] **Task 5.4: Styling & UX Polish**
  - [x] Clean navigation between views.
  - [x] Loading spinners and friendly error banners.

---

## 6. Testing & Quality Assurance

- [x] **Task 6.1: Domain Unit Tests (`Alteva.Domain.UnitTests`)**
  - [x] 36 unit tests covering URL normalization, Link Ratio calculation, and tree construction.
- [x] **Task 6.2: Infrastructure Tests (`Alteva.Infrastructure.Tests`)**
  - [x] 4 tests verifying unique index idempotency and cascade delete constraints via in-memory SQLite.
- [x] **Task 6.3: Worker Tests (`Alteva.CrawlWorker.Tests`)**
  - [x] 9 tests covering integration-style crawling with local HTML fixtures, consumer state transitions, and idempotent persistence.
- [x] **Task 6.4: API Tests (`Alteva.CrawlApi.Tests`)**
  - [x] 10 tests validating request payload rules and constraints.
- [x] **Task 6.5: Frontend Tests (`frontend`)**
  - [x] 9 unit tests verifying formatting and status mappings.

---

## 7. Containerization & Documentation

- [x] **Task 7.1: Solution Organization**
  - [x] Update `Alteva.slnx` with organized `/src/` and `/tests/` solution folders.
- [x] **Task 7.2: Git Initialization & Secrets Pipelining**
  - [x] Initialize Git repository over `10-projects/Alteva`.
  - [x] Create comprehensive `.gitignore` ignoring `.env`, `bin/`, `obj/`, `node_modules/`, `dist/`.
  - [x] Parameterize `docker-compose.yml` to ingest credentials exclusively via environment variables.
  - [x] Remove all hardcoded fallback connection strings and passwords from source code (`Program.cs`).
  - [x] Create sanitized `.env.example` with placeholders and local `.env`.
- [x] **Task 7.3: Docker Compose End-to-End Run**
  - [x] Verify SQL Server, RabbitMQ, API, and Worker spin up and communicate cleanly.
- [x] **Task 7.4: Submission Documentation**
  - [x] Document run instructions, architectural decisions, idempotency strategy, and trade-offs.

---

## 8. Recursive Page Crawl Refactor (`feat/recursive-page-crawl`)

- [x] **Task 8.1: Contract & schema (Phase 1)**
  - [x] `CrawlPageMessage { jobId, url, depth, maxDepth, rootUrl }`; `PageStatus` (`Queued`, `Done`, `Failed`, `Skipped`).
  - [x] `Page.Depth`, `Status`, `FailureReason`, nullable `DomainLinkRatio`, `UrlHash`; migration `AddPageCrawlState` with backfill.
- [x] **Task 8.2: Sequential, polite crawling**
  - [x] Remove semaphores/parallelism; worker prefetch 1; random 3–5 s delay before every download.
- [x] **Task 8.3: State store, confirms, recursion (Phases 2–3)**
  - [x] `CrawlStateStore`: create job, page gate, transactional commit (guard, edges, claims, completion), queued children, cancel/fail.
  - [x] Publisher confirms; shared `RabbitMQTopology`.
  - [x] `PageCrawlHandler` / `PageCrawler`; cancellation aborts the in-flight page (1 s job-status poll).
- [x] **Task 8.4: API & UI (Phase 4)**
  - [x] Atomic cancel; 503 and `Failed` job when the root cannot be published.
  - [x] `pagesDiscovered` / `pagesProcessed` and a progress bar; breadth-first tree with page status; string enums.
- [x] **Task 8.5: Reliability & observability**
  - [x] HTTP retries (network, timeout, 408, 429, 5xx; `Retry-After`), no retry for other 4xx.
  - [x] Infrastructure failures requeue with a 10 s delay (Phase 5); DLQ only for malformed messages; dropped `Job.RetryCount`.
  - [x] Worker `/health` (RabbitMQ consumer + database, 3 s bound); quieter EF/HttpClient logs.
- [x] **Task 8.6: Hardening & delivery (Phase 6)**
  - [x] Resolve links against the fetched URL; decode href entities; preserve percent-encoding.
  - [x] README per requirements; architecture notes, milestones and tasks refreshed.
  - [x] End-to-end verification on Docker Compose with a fresh database.
