# TradeFlow

![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16-4169E1?logo=postgresql&logoColor=white)
![Docker](https://img.shields.io/badge/Docker-Compose-2496ED?logo=docker&logoColor=white)
![CI](https://img.shields.io/badge/CI-GitHub%20Actions-2088FF?logo=githubactions&logoColor=white)

An event-driven automated trading platform that ingests trade alerts, evaluates them against a configurable risk policy, and executes approved orders through Interactive Brokers.

## Quick Facts

- Event-driven automated trading platform
- Built with .NET 10 and PostgreSQL 16
- Interactive Brokers integration via IB Gateway (TWS API)
- Xtrades ingestion through a real-time SignalR feed and REST polling
- Rule-based, configuration-driven risk engine
- Containerized with Docker; CI/CD via GitHub Actions
- Simulation, paper, and live trading modes (simulation by default)

## Architecture At A Glance

TradeFlow ingests trade alerts from Xtrades and screens each one through layered risk controls before routing approved orders to Interactive Brokers for execution. Open positions are protected by broker-side exit orders and persisted to PostgreSQL, so the system recovers its state after a restart and runs unattended through the trading day.

![Architecture Overview](docs/architecture/architecture-overview.png)

## Overview

TradeFlow receives trade alerts published by tracked traders on the Xtrades platform, decides whether each alert should be acted on, and, when it should, places and manages the corresponding order with Interactive Brokers. It runs unattended through the trading day and records every decision and outcome for later review.

Alerts arrive through two paths: a real-time SignalR feed for low latency and a REST polling interface as a backstop when the feed is unavailable. Both feed a single processing pipeline, so behaviour is identical regardless of source. Each alert is normalized, classified, and evaluated against the risk engine; approved entries are sized, screened by an account-level safety control, routed to the broker, and protected by broker-side exit orders until they close.

The system is organized around an execution core that owns the full path from alert to managed position, a read API over the same data store, and a separate reporting tool. Execution sits behind a broker abstraction, so the same workflow runs unchanged in simulation, paper, and live trading, and defaults to simulation so an unconfigured environment cannot place real orders.

## Architectural Highlights

- Dual-path ingestion (SignalR feed and REST polling) converging on one pipeline, keeping behaviour independent of the alert source
- Broker abstraction with simulation, paper, and live implementations selected at startup, defaulting to simulation
- PostgreSQL as the source of truth for open positions, enabling recovery of position state and broker callbacks after a restart
- OCA-based broker-side exit protection (trailing stop plus profit target) established once an entry fill is confirmed
- Layered risk policy: a configuration-driven rule engine as the admission gate, and an account-level trade guard as the final control before capital is committed
- Daily market-regime assessment that automatically tightens risk posture in unfavourable conditions
- A single serialized broker session that respects the single-threaded TWS client contract
- Multi-architecture container images with automated CI/CD

## Architecture

- **Alert ingestion** acquires alerts from the Xtrades REST API and the real-time feed and hands them to a shared pipeline.
- **Risk evaluation** runs each alert through an ordered rule set and returns the first failure or an approval; approvals and rejections are both persisted.
- **Position sizing** converts an approved alert into an order, deriving quantity, stop, target, and trailing behaviour from the instrument type and risk tier.
- **Trade execution** places the entry with immediate stop protection and, once filled, replaces it with a trailing stop and profit target in a One-Cancels-All (OCA) group.
- **Position management** reconciles positions the broker closes on its own (a stop or target fill) and restores position state and broker callbacks after a restart.

Full design detail, including the four architectural views and the architectural decision records, is in the [Software Architecture Document](docs/architecture/TradeFlow_Software_Architecture_Document.md).

## Key Features

- Dual-path alert ingestion into one processing pipeline
- Configurable, rule-based risk engine assembled from application settings at startup
- Risk-tiered position sizing with auto-classification of options by time to expiry
- Account-level trade guard enforcing exposure, position, balance, and daily loss limits
- Interactive Brokers execution through IB Gateway, with OCA-based exit protection
- Daily market-regime assessment that can tighten risk posture automatically
- Discord notifications across separate channels for signals, executions, health, and critical events
- PostgreSQL persistence with open positions held as the source of truth for recovery
- Simulation-first execution with a runtime toggle for paper and live trading
- Read API for querying ingested alerts, with output caching and health checks

## Technology Stack

| Area | Technology |
|------|------------|
| Language and runtime | C#, .NET 10 |
| Hosting | .NET Generic Host (background services), ASP.NET Core Minimal API |
| Database | PostgreSQL 16 |
| Data access | Entity Framework Core, Npgsql |
| Broker integration | Interactive Brokers TWS API (IBApi) via IB Gateway |
| Real-time feed | SignalR client |
| Resilience | Microsoft.Extensions.Http.Resilience |
| Logging | Serilog (console and file) |
| Notifications | Discord webhooks |
| API tooling | OpenAPI / Swagger |
| Testing | xUnit, Moq, FluentAssertions, Testcontainers |
| Packaging and CI/CD | Docker, Docker Compose, GitHub Actions, GitHub Container Registry |

## Repository Structure

The solution (`TradeFlow.sln`) contains five projects with distinct responsibilities.

- **TradeFlow.AlertPoC** — Shared domain library. Owns the alert model, the alert classifier, and the risk engine with its rules. Referenced by the execution core and the tests.
- **TradeFlow.Worker** — The execution core. Owns ingestion, risk evaluation, position sizing, trade execution, position management, scheduling, broker integration, and persistence (the EF Core `DbContext` and migrations live here).
- **TradeFlow.Api** — A read-only HTTP API over ingested alerts, with filtering, pagination, output caching, and health endpoints.
- **TradeFlow.Analytics** — A console application that produces performance reports from recorded trade outcomes.
- **TradeFlow.Tests** — Unit tests for the risk engine, position sizing, and execution, plus integration tests against a containerized PostgreSQL instance and the API host.

## Risk Controls

Risk management is layered across the architecture rather than concentrated in one place.

- **Risk engine** — An ordered rule set is the admission gate before any order is sized: entry-only filtering, trader approval and quality scoring, blocked symbols, an instrument-price floor, a 0DTE entry cutoff, and suppression of high-risk and lotto entries.
- **Trade guard** — The final account-level control before capital is committed. It blocks duplicated exposure on a single underlying, enforces a per-symbol position limit and a daily exposure cap, refuses to deploy more than available capital, and halts new entries once a daily loss limit is reached.
- **Market regime control** — A daily chop-score assessment can suppress higher-risk activity and apply a tighter loss limit for the session, independent of standing configuration.
- **Simulation-first execution** — The broker defaults to a no-op simulation implementation; live execution is enabled only by explicit configuration.

## Running Locally

### Prerequisites

- .NET 10 SDK
- Docker and Docker Compose
- An Xtrades API token
- IB Gateway, required only for paper or live trading; the default simulation mode does not need it

### Configuration

Runtime secrets are supplied through environment variables. Copy the example file and fill in your values:

```bash
cp .env.example .env
```

Expected keys:

```
XTRADES_TOKEN
DISCORD_WEBHOOK_URL
DISCORD_TRADE_EXECUTION_WEBHOOK_URL
DISCORD_HEALTH_WEBHOOK_URL
DISCORD_SUMMARY_WEBHOOK_URL
DISCORD_CRITICAL_WEBHOOK_URL
POSTGRES_PASSWORD
Ibkr__AccountId
IBKR_ENABLED        # false for simulation; true to enable Interactive Brokers
```

Non-secret settings (risk parameters, polling interval, IB Gateway host and port, logging) are in `TradeFlow.Worker/appsettings.json`.

### Database

Start PostgreSQL and apply the Entity Framework Core migrations:

```bash
docker compose up -d postgres
dotnet ef database update --project TradeFlow.Worker
```

### Running the services

Run the worker and the API with the .NET CLI:

```bash
dotnet run --project TradeFlow.Worker   # execution core
dotnet run --project TradeFlow.Api      # read API (separate terminal)
```

Or run the full stack in containers:

```bash
docker compose up --build
```

Through Docker Compose the API is exposed on `http://localhost:5141`, with Swagger available in the development environment.

## Testing

Tests use xUnit. Integration tests run against a real PostgreSQL instance via Testcontainers and the in-process API host; broker tests are skipped unless IB Gateway is available.

```bash
# Unit tests only
dotnet test TradeFlow.Tests --filter "FullyQualifiedName!~Integration"

# Integration tests
SKIP_IBKR_TESTS=true dotnet test TradeFlow.Tests --filter "FullyQualifiedName~Integration"

# Full suite
SKIP_IBKR_TESTS=true dotnet test TradeFlow.sln
```

Continuous integration (GitHub Actions) restores, builds, and runs the unit and integration suites against a PostgreSQL service container on every push and pull request, and publishes multi-architecture container images to the GitHub Container Registry on merges to `main`.

## Documentation

- [Software Architecture Document](docs/architecture/TradeFlow_Software_Architecture_Document.md) — system responsibilities, the four architectural views, security and reliability design, and the architectural decision records (Section 12).

## Disclaimer

This project is provided for educational and software engineering purposes. It is not financial advice and should not be relied upon for investment decisions.
