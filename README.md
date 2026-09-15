# Alteva Web Crawler System

## Overview
Alteva is a distributed, event-driven web crawler system designed to traverse websites up to a specified depth and compute the **Domain Link Ratio** for each discovered page. 

The system leverages a decoupled architecture, separating the API orchestration layer from the background crawl execution engine.

## Architecture & Technology Stack
- **Backend (API & Worker)**: .NET 8 (C#)
- **Frontend**: React (TypeScript, Vite)
- **Message Broker**: RabbitMQ
- **Database**: SQL Server (Entity Framework Core)
- **Containerization**: Docker & Docker Compose

### System Components
1. **Crawl API (`Alteva.CrawlApi`)**: A RESTful API that accepts crawl requests, persists each new job with its root page, and publishes a `CrawlPageMessage` for the root page to RabbitMQ. It also serves job history, job details, and hierarchical crawl tree results to the frontend.
2. **Crawl Worker (`Alteva.CrawlWorker`)**: A background service that consumes messages from RabbitMQ. Each message is one page: the worker downloads it, extracts links, computes the domain ratio, persists the page and its edges, and publishes one message per newly discovered same-domain link until the job's maximum depth is reached. It processes one message at a time, so the FIFO queue yields a breadth-first crawl.
3. **Frontend SPA (`frontend`)**: A modern React interface providing real-time status updates, a paginated job history, and an interactive recursive tree view for inspecting the final assembled crawl topology.

---

## Run Instructions

### Prerequisites
- [Docker & Docker Compose](https://docs.docker.com/get-docker/)
- [Node.js (v18+) & npm](https://nodejs.org/) (To run the frontend separately)

### 1. Configuration
1. Clone the repository.
2. Ensure you have a `.env` file at the root. You can copy the template:
   ```bash
   cp .env.example .env
   ```
   *(Update credentials in `.env` if desired, or use the defaults for local testing)*

### 2. Start the Backend Infrastructure
The backend is fully containerized. To spin up SQL Server, RabbitMQ, the API, and the Worker:
```bash
docker compose up --build -d
```
*Note: The API container automatically applies Entity Framework migrations on startup to initialize the SQL Server database schema.*

### 3. Start the Frontend
In a separate terminal, navigate to the `frontend/` directory and run:
```bash
cd frontend
npm install
npm run dev
```
Open `http://localhost:5173` in your browser.

### 4. Health Endpoints
- API: `http://localhost:8080/health`
- Worker: `http://localhost:8081/health` — `Healthy` only while it is consuming from RabbitMQ and can reach the database (503 otherwise).

---

## Architectural Decisions & Patterns

### 1. Sequential Crawling & Politeness
- **No parallelism by design**: The worker uses a RabbitMQ `prefetchCount` of 1 and awaits each message to completion before the next is delivered. Within a crawl, pages are downloaded strictly one at a time.
- **Politeness delay**: A random delay between `CRAWLER_DELAY_MIN_SECONDS` and `CRAWLER_DELAY_MAX_SECONDS` (default 3–5 s) is applied between consecutive downloads, keeping the crawler well under typical target-site rate limits. This trades throughput for simplicity: a 200-page crawl takes roughly 13+ minutes.

### 2. Event-Driven Messaging (RabbitMQ)
The system employs reliable message queuing to prevent job loss during traffic spikes or worker crashes:
- **Dead-Letter Queue (DLQ)**: Only malformed messages (invalid JSON, missing fields, unknown contract) are dead-lettered, for later inspection.
- **Explicit Acknowledgments**: A page message is `Ack`ed only after the page is persisted and its child messages are confirmed by the broker.
- **HTTP retry policy**: Network errors, timeouts (15 s), HTTP 408, 429 and 5xx are **transient** and retried up to 3 attempts per page, waiting the politeness delay (or the server's `Retry-After`, up to 30 s) between attempts. Other 4xx responses are permanent and fail the page immediately.
- **Message retry**: An unexpected processing failure (e.g. a database error) requeues the message after 10 s; a second failure marks the page failed. If the page cannot even be marked failed because the database or broker is down, the message keeps being requeued every 10 s until the infrastructure recovers, so no job is left stuck.
- **Cancellation**: Cancelling a job discards its remaining messages without processing them and aborts the page in progress within about a second.

### 3. Idempotency Strategy
Idempotency and duplicate suppression are enforced strictly at the database layer using EF Core:
- **Pages**: A composite unique index on `(JobId, UrlHash)` (SHA-256 of the normalized URL) ensures a page is never recorded twice for the same job. Hashing keeps the index key within SQL Server's 1700-byte limit and compares URLs case-sensitively.
- **Edges**: Deduplicated in memory per page and written together with the page, so no unique index is needed.
- *In the event of duplicate processing (e.g. network partition during a DB commit), the unique constraints protect data integrity.*

### 4. Domain Logic: Domain Link Ratio
The formula implemented is: 
```
Domain Link Ratio = (# of outgoing links to the SAME domain) / (Total valid outgoing HTTP/HTTPS links)
```
- E.g., if a page has 10 outgoing links, and 4 point to the same starting host, the ratio is `40.0%`. 
- `mailto:`, `javascript:`, and other non-HTTP schemes are excluded entirely from both the numerator and denominator.
- URLs are normalized via `Uri` logic (fragments stripped, default ports removed, casing normalized).

---

## Trade-offs & Future Improvements

1. **Visited-URL Tracking & Throughput**
   - *Trade-off*: Visited URLs are tracked in SQL Server (`Pages` unique on `(JobId, UrlHash)`), and a single worker processes pages sequentially. Simple and consistent, but throughput is bounded by one download every few seconds.
   - *Improvement*: For large-scale crawling, allow multiple workers with per-domain rate limiting and move the visited set to a faster store (e.g. Redis).
2. **Rate Limiting (Politeness)**
   - *Trade-off*: Downloads are sequential with a fixed randomized delay, regardless of what the target site allows.
   - *Improvement*: Respect `robots.txt` (`Crawl-delay`) and back off on HTTP 429 / `Retry-After`.
3. **Database Write Performance**
   - *Trade-off*: Each page and its edges are persisted in one small EF Core transaction as the page is crawled.
   - *Improvement*: For pages with very many links, use bulk inserts (`SqlBulkCopy`) for edges.
