# Architecture

B1 Data Importer is evolving from a SAP Business One importer into a **metadata-driven, codeless
integration platform**. SAP B1 is the first target connector; the design deliberately keeps the
source side, the mapping engine, and the run orchestration **system-neutral** so new systems can be
added — ideally as configuration (a connector *manifest*), not code.

```
Source (SQL / Excel / CSV)          Mapping engine                 Target (via ITargetConnector)
──────────────────────────   ───────────────────────────────   ──────────────────────────────
ISourceReader.ReadAll   ->   GroupRows(GroupBy)             ->   ITargetSession
 uniform Dict<string,        RecordBuilder (transforms,          .GetEntitiesAsync (metadata)
 object?> per row            lookups, type coercion)             .Validate / .ExistsAsync
                             validation (metadata-driven)        .WriteAsync (create / upsert / batch)
                                                                 .Lookups (name → code)
```

Everything is driven by `ScenarioExecutor`, which is **target-agnostic**: it obtains an
`ITargetSession` from the `TargetConnectorRegistry` keyed by the connection's `Kind` and never
names a specific system.

## Layers

### Source (already generic)
`Services/SourceReaders.cs` — `ISourceReader { Inspect, ReadAll }` with Excel / CSV / SQL
implementations behind `SourceReaderFactory`. Everything downstream consumes a uniform
`Dictionary<string, object?>` per row, so adding a source type is one reader class.

### Mapping engine (generic)
- `Domain/MappingSpec.cs` — the mapping DSL: `MappingSpec` (header fields, line collections,
  `GroupBy`, `SourceKeyColumn`), `FieldSpec` (transforms: Direct, Constant, Expression, DateFormat,
  StaticLookup, B1Lookup), `B1LookupSpec` (name → code).
- `Services/Mapping/RecordBuilder.cs` — applies transforms + coercion to build a target-shaped
  payload. Depends only on `ITargetLookup` for name→code resolution, not on any system.

### Target connectors (the seam)
`Services/Connectors/`:
- **`ITargetConnector`** — a registered target system. `Kind` (the registry key), `DisplayName`,
  `OpenAsync(ConnectionDef) → ITargetSession`.
- **`ITargetSession`** — a live, authenticated connection. The only surface the executor uses:
  `GetEntitiesAsync`, `ExistsAsync`, `Validate`, `WriteAsync`, `FormatKeyPredicate`, `Lookups`.
- **`ITargetLookup`** — filtered name→code lookups during mapping.
- **`TargetWriteOp`** — one write the executor hands to the session (payload + POST/PATCH + key predicate).
- **`TargetConnectorRegistry`** — resolves a connector by `ConnectionKind`. Adding a system = register one connector.
- **`TargetType` / `TargetTypes`** — a neutral field-type vocabulary. Bridged onto the existing
  `Edm.*` tags that coercion/validation already understand, so connectors speak clean types while
  the engine is unchanged.

Metadata is currently expressed with the `B1Entity` / `B1Property` / `B1NavCollection` shape
(`Models/B1Models.cs`) — a generic *Name / Properties / Collections* descriptor, not B1-specific in
structure. (Renaming these to `TargetEntity` etc. is a planned cleanup; see Roadmap.)

#### SAP B1 connector — `Services/Connectors/B1/`
`B1TargetConnector` + `B1TargetSession` wrap the existing B1 code with **no behavior change**:
- `Services/B1/ServiceLayerClient.cs` — Service Layer HTTP: login/session (re-login on 401),
  `$metadata`, OData query, existence check, POST/PATCH, and `$batch` (each op in its own
  changeset for per-row isolation).
- `Services/MetadataService.cs` — parses B1's EDMX/CSDL `$metadata` into `B1Entity` list (incl.
  UDFs and both collection representations).
- `Services/B1/LookupResolver.cs` — `ITargetLookup` via OData `$filter`, cached per run.
- `Services/B1/PayloadValidator.cs` — pre-flight validation from live metadata.

#### REST / OData connector — `Services/Connectors/Rest/` (manifest-driven)
The first **codeless** connector: adding a REST/OData system needs only a JSON *manifest* in the
`server/connectors/` folder, no code.
- `ConnectorManifest` — declares auth (none / basic / bearer / apikey) and entities/fields; projects
  to the generic `B1Entity` metadata shape via the neutral `TargetType` vocabulary.
- `ManifestStore` — loads/caches manifests from the content-root `connectors/` folder.
- `RestApiClient` — OData-style HTTP (`{base}/{Entity}`, key predicate in parentheses,
  `$filter`/`$select`/`$top`), applies auth, surfaces API errors verbatim.
- `RestManifestConnector` (`ConnectionKind.Rest`) + `RestManifestSession` — the `ITargetConnector` /
  `ITargetSession` pair. A connection selects its system via `ConnectionDef.ConnectorManifestId`.

A sample manifest ships at `server/connectors/northwind-odata.json`.

### Orchestration (already generic)
`Domain/Entities.cs` + `Services/Jobs/`:
- `Scenario` = source + mapping + target + trigger. `Run` / `RunItem` = per-execution and per-row
  monitoring, which is what makes failures re-runnable.
- `RunQueue` (in-memory queue), `RunWorker` (drains it), `SchedulerService` (cron via Cronos),
  `ScenarioExecutor` (the engine: build → validate → upsert-route → batch write, watermark,
  retry-failed, dry-run, continue-on-error).

### AI mapping — `Services/Ai/AiMappingService.cs`
Proposes a `MappingSpec` from a source schema + target entity using Claude. Currently steeped in B1
vocabulary; parameterizing it per connector is on the roadmap.

## Persistence
.NET 8 minimal API + SQLite (EF Core) at `server/data/importer.db`. `EnsureCreated()` plus small
hand-rolled forward patches in `Program.cs` (no EF migrations) — e.g. `Runs.RetryRowNumbers`,
`Connections.ConnectorManifestId`. Secrets encrypted at rest via DataProtection (`ISecretProtector`).

## Testing
`tests/B1DataImporter.Tests` (xUnit) covers the target-agnostic core without a live system: the
neutral type mapping, manifest → metadata projection, manifest loading, the registry, and the
mapping engine running end-to-end against manifest metadata with a fake lookup. Run with
`dotnet test B1DataImporter.sln`.

### Client
`client/src/pages/`. The **Connections** page creates B1, SQL, and **REST/OData** connections
(the REST form picks a manifest from `GET /api/connectors` and sets base URL + auth). The
**scenario editor** lets any target connector (B1 or REST) be the target and discovers its entities
through the same endpoint. The **mapper** (`Mapper.tsx`) has two views over the same `MappingSpec`:
a **table** view and a **visual** connect-the-lines view (drag a source column onto a target field;
curved SVG connectors; click a field to edit its transform).

## Roadmap toward the codeless platform
1. **✅ Connector seam** — `ITargetConnector` / `ITargetSession`; B1 is the first connector.
2. **✅ Manifest-driven REST connector** — new systems via JSON, proven by tests.
3. **✅ Connection UI for REST/manifest connectors** — pick a manifest, set base URL + auth; REST
   connections are selectable scenario targets with entity discovery.
4. **✅ Visual mapping** — connect-the-lines drag-and-drop view alongside the table mapper.
5. **Neutralize metadata naming** — rename `B1Entity`/`B1Property` → `TargetEntity`/`TargetProperty`
   and drive coercion/validation off `TargetType` directly instead of the `Edm.*` bridge.
6. **Parameterize the AI mapper** — feed target-supplied vocabulary so AI mapping works per connector.
7. **Symmetric source connectors** — an `ISourceConnector` mirroring the target seam, so a flow is
   just *source connector → mapping → target connector*.
8. **Visual flow builder** — a node canvas over the scenario model once both sides are connector-driven.
