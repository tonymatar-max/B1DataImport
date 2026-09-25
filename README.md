# B1 Integrator

A B1if-class integration platform for SAP Business One: pull from any source (SQL Server,
Excel, CSV), map it to any Service Layer object, and write it — manually or on a schedule —
with validation, queued execution, per-row monitoring, and re-runnable failures.

Metadata-driven: the tool reads the live `$metadata` document, so every object works with no
per-object code — master data, marketing documents, UDFs (`U_*`) and UDO tables alike.

## The model

```
Connection ──┐
             ├──> Scenario ──> Run ──> RunItem (one per record)
Connection ──┘    (persisted    (queued,   (status, error, payload,
                   definition)   monitored)  re-runnable)
```

A **Scenario** is one integration: source + mapping + target + trigger. Running one creates a
**Run**, which a background worker executes; every record becomes a **RunItem** so you can see
exactly which rows failed and re-run only those.

> **Becoming a codeless integration platform.** SAP B1 is now the *first* target behind a generic
> `ITargetConnector` seam, not the only one. A second, **manifest-driven REST/OData connector** lets
> you add a new target system with a JSON manifest and no code. See
> [ARCHITECTURE.md](ARCHITECTURE.md) for the design and roadmap.

## Build & test

```bash
dotnet build B1DataImporter.sln
dotnet test  B1DataImporter.sln   # xUnit; covers the target-agnostic core
```

## What it does

| Capability | How |
|---|---|
| **Any source** | SQL Server (table/view/query), Excel, CSV |
| **Any B1 object** | Parsed from live `$metadata` — fields, types, mandatory flags, enums, line collections |
| **AI mapping** | Claude (`claude-opus-5`) reads your columns + sample rows and proposes the full mapping with transforms, confidence and reasoning; you approve or correct |
| **Real lookups** | `B1Lookup` resolves "Main Warehouse" → `WhsCode 01` by querying B1, cached per run |
| **Pre-flight validation** | Mandatory / max length / enum membership / numeric + date parsing, checked before posting |
| **Header + lines** | Group source rows by a key so many rows become one document with many `DocumentLines` |
| **Batching** | `$batch` with one changeset per row — one round trip, but failures stay isolated per record |
| **Scheduling** | Cron trigger (5- or 6-field), evaluated every 30s |
| **Incremental sync** | Watermark column; only rows newer than the last clean run are pulled |
| **Monitoring** | Run history, live progress, cancel, and **re-run only the failed rows** |
| **Secrets** | B1 passwords and connection strings encrypted at rest via ASP.NET DataProtection |

## Transforms

`Direct` · `Constant` · `Expression` (`{First} {Last|upper}`, with `upper/lower/trim/substr/pad`)
· `DateFormat` · `StaticLookup` (value→value) · `B1Lookup` (resolved against B1).

## Architecture

```
client/   React + Vite + TypeScript — dashboard, scenarios, mapper, run monitor
server/   .NET 8 Web API + SQLite (EF Core)
  Domain/       Scenario, Run, RunItem, ConnectionDef, MappingSpec
  Services/B1/  ServiceLayerClient (session recovery, $batch, PATCH, query)
                LookupResolver, PayloadValidator
  Services/Mapping/  RecordBuilder — transforms + type coercion
  Services/Jobs/     RunQueue, RunWorker (background), SchedulerService (cron),
                     ScenarioExecutor (the engine)
  Services/Ai/       AiMappingService — Claude mapping proposals
```

## Run it

```bash
cd server && dotnet run --urls http://localhost:5130
```

```bash
cd client && npm install && npm run dev
```

Open http://localhost:5173. To enable AI mapping, set `ANTHROPIC_API_KEY` before starting the
server (the sidebar shows whether it is active).

## Build for deployment

```bash
cd client && npm run build && cd ../server && dotnet publish -c Release
```

One process serves the UI and the API. Host behind IIS or as a Windows Service (nssm), same
pattern as your other B1 tools. State lives in `server/data/` (SQLite DB, DataProtection keys,
uploads) — back that folder up.

## Verified so far

- Backend builds; frontend type-checks; UI renders and drives the API.
- CSV/Excel schema detection and row preview.
- Connection CRUD with **confirmed** encryption at rest (ciphertext in SQLite, never plaintext).
- Scenario persistence, run queueing, background execution, status transitions, error capture.
- `ServiceLayerClient` reaches a live Service Layer and surfaces genuine B1 errors verbatim.

## Not yet exercised against a real company

`$batch` parsing, upsert/PATCH, lookup resolution, validation, and header+lines grouping are
implemented but have only been run against a Service Layer that rejected the login. They need a
pass with real credentials.

## Roadmap

- Upsert is defined on the scenario (`WriteMode`) but the executor currently always POSTs —
  wire `Upsert`/`UpdateOnly` through to existence-check + PATCH.
- Per-item retry with backoff (`MaxRetries` is stored but not yet applied).
- Bidirectional flows (B1 → external) and webhook/event triggers.
- Authentication on the tool itself before exposing it beyond a trusted network.
