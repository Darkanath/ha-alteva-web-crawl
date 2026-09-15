---
id: agents-alteva
title: Alteva Project Entry Point
type: entrypoint
scope: project
authority: authoritative
status: active
tags:
  - meta/entrypoint
  - project/alteva
last_reviewed: 2026-09-15
---

# Alteva Project Scope & Multi-Agent Collaboration

Read root `AGENTS.md` and the applicable domain entry points before acting. Project-specific context, tasks, and architectural decisions in this directory take precedence over generic domain guidance where the root precedence rules permit it.

## Project Structure & Artifact Layout

All artifacts and code for this project reside inside `10-projects/Alteva/`:

- `dev_content/` — canonical home for development tracking:
  - `milestones.md`: High-level roadmap and delivery phases
  - `tasks.md`: Granular work breakdown structure and checklist
  - `architecture_notes.md`: C4 container topology, message schemas, idempotency strategy, and ratio formulas
- `requirements/` — formal requirements specifications and PDF briefs
- `src/` — implementation source projects:
  - `Alteva.Domain/`: Core business models and pure calculation engines
  - `Alteva.Infrastructure/`: EF Core `AppDbContext`, SQL migrations, RabbitMQ messaging
  - `Alteva.CrawlApi/`: ASP.NET Core REST API orchestrator
  - `Alteva.CrawlWorker/`: Background crawler worker service
- `tests/` — automated test suites:
  - `Alteva.Domain.UnitTests/`: Domain rules, URL normalization, link ratio calculation, tree builder
  - `Alteva.Infrastructure.Tests/`: Database unique constraints, idempotency verification
  - `Alteva.CrawlWorker.Tests/`: Local HTML fixture integration tests, crawler engine limits
  - `Alteva.CrawlApi.Tests/`: Request validation and DTO mapping
- `frontend/` — React 19 + Vite frontend application with Vitest test suite
- `docker-compose.yml` — multi-container deployment orchestration

---

## Multi-Agent Domain Perspectives & Subagents

When working on this project, agents must apply the appropriate perspective based on the task at hand:

### 1. Domain Entry Points
- **Solutions Architect (`01-solutions/AGENTS.md`)**:
  - Focus: Component boundaries, REST API design, RabbitMQ exchange/queue schemas, message contracts, and domain invariants.
  - Review rules: `[[00-meta/rules/solutions-architect]]`.
- **Cloud Architect (`02-cloud/AGENTS.md`)**:
  - Focus: Container configuration (`docker-compose.yml`), SQL Server & RabbitMQ connectivity, health checks, environment variables, and FinOps/resource limits.
  - Review rules: `[[00-meta/rules/cloud-architect]]`.
- **Tech Lead (`03-tech-lead/AGENTS.md`)**:
  - Focus: Code quality, testing standards, test suite maintenance, implementation pacing, and delivery checklists in `dev_content/tasks.md`.
  - Review rules: `[[00-meta/rules/tech-lead]]`.

### 2. Specialized Subagents (`.claude/agents/`)
- **`estate-archaeologist`**:
  - Use to inspect existing components, dependencies, and verify that actual code matches `dev_content/architecture_notes.md`.
- **`assumption-hunter`**:
  - Use to red-team proposed designs before implementing (e.g. crawler infinite loops, duplicate URLs, network timeout handling, poison messages).
- **`consistency-auditor`**:
  - Use before finalizing major architectural changes to verify that the implementation does not contradict vault rules, EF Core unique constraints, or idempotency contracts.

---

## Secrets & Configuration Policy

- Never commit secrets or connection strings with passwords to Git.
- Parameterize all credentials via `.env` (gitignored).
- Maintain sanitized `.env.example` templates with placeholders (`REPLACE_WITH_STRONG_PASSWORD`).
