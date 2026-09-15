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
- [ ] **Task 2.3: Message Broker Abstraction**
  - [ ] Define `IMessagePublisher` and `IMessageConsumer` interfaces.
  - [ ] Implement RabbitMQ publisher using exchange/queue configuration with durable queues.
  - [ ] Implement dead-letter exchange (`dlx`) binding.

---

## 3. Crawl API (`Alteva.CrawlApi`)

- [x] **Task 3.1: DTO Models & Validation**
  - [x] Create `CreateCrawlJobRequest` with URL format and depth validation (`[Range(1, 10)]`).
  - [x] Create `CreateCrawlJobResponse` and `CrawlJobDetailsResponse`.
- [ ] **Task 3.2: Jobs Controller Implementation**
  - [ ] `POST /api/jobs`: Persist new job and enqueue `CrawlJobRequestedMessage`.
  - [ ] `GET /api/jobs/{id}`: Retrieve job status and assembled page tree.
  - [ ] `GET /api/jobs`: Paginated history list sorted by `CreatedAt DESC`.
  - [ ] Optional: `POST /api/jobs/{id}/cancel`: Mark job as `Canceled`.
- [ ] **Task 3.3: Health Check & Swagger**
  - [ ] Enable `/health` endpoint for Docker container health check.
  - [ ] Document endpoints via Swagger UI.

---

## 4. Crawl Worker (`Alteva.CrawlWorker`)

- [x] **Task 4.1: Crawler Engine Core**
  - [x] BFS queue traversal with depth limiting.
  - [x] Visited URL deduplication set.
  - [x] Max pages safety cutoff (default 200).
  - [x] Error handling for HTTP status codes and timeouts.
- [ ] **Task 4.2: RabbitMQ Background Consumer**
  - [ ] Consume `CrawlJobRequestedMessage` from queue with prefetch limit.
  - [ ] Update Job status to `Running` with `StartedAt` timestamp.
  - [ ] Save discovered pages and edges to database in batches or transaction.
  - [ ] Update Job status to `Completed` or `Failed` with `CompletedAt`.
  - [ ] Implement retry policy for transient DB/network errors; forward to DLQ upon exhaustion.

---

## 5. Frontend Application (`frontend/`)

- [x] **Task 5.1: Setup & Utilities**
  - [x] Install and configure Vitest test runner.
  - [x] Implement ratio formatting and status badge utility functions.
- [ ] **Task 5.2: API Service Layer**
  - [ ] Implement typed HTTP client for Crawl API (`startJob`, `getJobDetails`, `getJobHistory`).
- [ ] **Task 5.3: Core UI Screens**
  - [ ] **Start Crawl Screen**: Form with URL input, max depth selector, and error handling.
  - [ ] **Job Details Screen**: Status badge, timestamps, polling progress, and recursive interactive tree view.
  - [ ] **History Screen**: Paginated list/table of past crawls with status chips.
- [ ] **Task 5.4: Styling & UX Polish**
  - [ ] Clean navigation between views.
  - [ ] Loading spinners and friendly error banners.

---

## 6. Testing & Quality Assurance

- [x] **Task 6.1: Domain Unit Tests (`Alteva.Domain.UnitTests`)**
  - [x] 36 unit tests covering URL normalization, Link Ratio calculation, and tree construction.
- [x] **Task 6.2: Infrastructure Tests (`Alteva.Infrastructure.Tests`)**
  - [x] 4 tests verifying unique index idempotency and cascade delete constraints via in-memory SQLite.
- [x] **Task 6.3: Worker Tests (`Alteva.CrawlWorker.Tests`)**
  - [x] 6 tests including integration-style crawling test using multi-level local HTML fixtures.
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
- [ ] **Task 7.3: Docker Compose End-to-End Run**
  - [ ] Verify SQL Server, RabbitMQ, API, and Worker spin up and communicate cleanly.
- [ ] **Task 7.4: Submission Documentation**
  - [ ] Document run instructions, architectural decisions, idempotency strategy, and trade-offs.
