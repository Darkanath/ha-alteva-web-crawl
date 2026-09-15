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
  - [x] Create unique composite index `(JobId, Url)` on `Pages` for idempotency.
  - [x] Create unique composite index `(JobId, ParentUrl, ChildUrl)` on `Edges` for duplicate suppression.
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
  - [x] `POST /api/jobs`: Persist new job and enqueue `CrawlJobRequestedMessage`.
  - [x] `GET /api/jobs/{id}`: Retrieve job status and assembled page tree.
  - [x] `GET /api/jobs`: Paginated history list sorted by `CreatedAt DESC`.
  - [x] `POST /api/jobs/{id}/cancel`: Mark pending or running job as `Canceled`.
- [x] **Task 3.3: Health Check & Swagger**
  - [x] Enable `/health` endpoint for Docker container health check.
  - [x] Enable CORS policy for frontend client communication.
  - [x] Document endpoints via Swagger UI.

---

## 4. Crawl Worker (`Alteva.CrawlWorker`)

- [x] **Task 4.1: Crawler Engine Core**
  - [x] BFS queue traversal with depth limiting.
  - [x] Visited URL deduplication set.
  - [x] Max pages safety cutoff (default 200).
  - [x] Error handling for HTTP status codes and timeouts.
- [x] **Task 4.2: RabbitMQ Background Consumer**
  - [x] Consume `CrawlJobRequestedMessage` from queue with prefetch limit (`BasicQos(0, 1, false)`).
  - [x] Update Job status to `Running` with `StartedAt` timestamp.
  - [x] Save discovered pages and edges to database in batches or transaction.
  - [x] Update Job status to `Completed` or `Failed` with `CompletedAt`.
  - [x] Implement retry policy for transient DB/network errors; forward to DLQ upon exhaustion.

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
- [ ] **Task 7.4: Submission Documentation**
  - [ ] Document run instructions, architectural decisions, idempotency strategy, and trade-offs.
