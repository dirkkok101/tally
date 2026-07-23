# Published actuals personal-scale variance

## Symptom report

`Tally.Tests.Performance.ActualsPersonalScaleTests.Published_pool_category_actuals_path_meets_personal_scale_budget` intermittently exceeds the accepted `TC-LEDGER-PERSONAL-SCALE-PERFORMANCE` wall-clock contract: p95 must remain below 2,000 ms for a published 100,000-transaction pool/category query.

The defect blocks `bd-3bx`, the LEDGER v1 module gate. The threshold is not changed or conditionally skipped.

## Reproduction

- Branch and commit: `main` at `79fdac6` on linux-x64, .NET SDK 10.0.109 and runtime 10.0.9.
- Command: `dotnet test tests/Tally.Tests/Tally.Tests.csproj -c Release --filter FullyQualifiedName~Tally.Tests.Performance.ActualsPersonalScaleTests`.
- First clean run: failed at p95 2,165.7 ms.
- Second clean run: passed, confirming timing variance.
- Diagnostic run: failed at p95 4,669.8 ms; 24 of 30 samples were below 2,000 ms and six outliers ranged from 2,406.6 to 4,964.1 ms.
- Repeated diagnostic run: failed at p95 3,937.4 ms.

The failure is reproducible but host-load-sensitive.

## Evidence

- Every query returned the exact 100,000-member result, exact totals, six conserved pool/category groups, and a valid second page. No correctness divergence occurred.
- Temporary `[DBG-5j2]` instrumentation, removed before implementation, measured about 94.8 MiB allocated and 9-11 Gen0 collections per query. Some runs also collected Gen1/Gen2, but multi-second outliers also occurred without higher-generation collection.
- The SQLite WAL did not accumulate between samples, ruling out periodic WAL growth as the trigger.
- Phase timings placed approximately 1.55-3.10 seconds in `ActualsProjectionStore.Sql(filter, compact: true)` row projection plus managed validation/serialization, 0.10-0.25 seconds in payload persistence, and about 0.01-0.03 seconds in setup/commit.
- A read-only execution of the compact SQL over all 100,000 rows took approximately 0.89-0.92 seconds. The remaining ordinary-case projection time is managed row materialization, validation, grouping, and JSON payload creation.
- The compact projection returns closed pool, attribution, reconciliation, relationship, and evidence states as repeated text or JSON. `QuerySnapshotStore` allocates and reparses those values for every row before encoding them into the retained payload.
- Host inspection during the largest outliers reported load averages of 16.23, 13.66, and 16.29 with concurrent Capstone, Identity, Pulse, Angular, browser, compiler, and Aspire workloads. Those unrelated workloads were preserved. Memory remained available, while CPU scheduling contention was visible.

Three bounded implementation attempts were measured and then reverted:

1. Numeric compact states and an evidence bit mask reduced allocation to about 84.9 MiB. Two Release runs passed at p95 1,689.9 ms and 1,744.2 ms, but a third failed at p95 3,161.1 ms.
2. A SQLite temp-table projection removed the managed 100,000-row serializer but added database writes and repeated scans. It regressed to median 3,449.8 ms and p95 3,726.5 ms.
3. A one-pass SQLite UTF-8 reader removed nearly all per-row string materialization while preserving validation, aggregation, and the retained payload. Median was 1,537.9 ms, but p95 still failed at 3,400.7 ms.

The production changes from all three attempts were removed. The test now reports transaction count, run count, median, p95, and the unchanged budget so future design work has durable gate evidence.

Code path:

`ledger.actuals.query` -> `ActualsQueryHandler.FirstPageAsync` -> `QuerySnapshotStore.CreateAsync` -> `CreatePoolCategorySnapshotInSqlAsync` -> compact SQL projection -> repeated textual state materialization/parsing -> payload serialization -> snapshot commit.

## Root cause analysis

1. Why does p95 cross 2,000 ms? The query rebuilds and persists all 100,000 immutable snapshot members on every first-page request, leaving little wall-clock margin for scheduler contention.
2. Why does reducing managed allocation not stabilize p95? The projection still performs the full relational reconciliation/category/attribution scan, sort, validation, aggregation, JSON construction, and snapshot persistence synchronously.
3. Why did the database-side materialization regress? Avoiding managed allocations by writing an intermediate table introduced additional I/O and full-table scans rather than reducing the amount of required work.
4. Why did direct UTF-8 consumption still fail? It reduced managed materialization but retained the same full synchronous projection and persistence workload; shared-host scheduling still stretched two or more of 30 samples above the contract.
5. Why can this not be resolved as a localized reader optimization? Three materially different implementations preserved the contract and all showed that per-query reconstruction, rather than one encoding detail, controls tail latency.

Root cause: the accepted query contract requires a complete 100,000-member immutable snapshot to be reconstructed synchronously for each first-page request. That architecture has insufficient wall-clock headroom to guarantee p95 below 2,000 ms under the repository's shared-host execution conditions.

Classification: design-level performance gap. The shared host amplifies the tail, but the gate correctly demonstrates that the current per-query reconstruction design does not reliably meet the accepted NFR. Allocation and textual encoding are contributing costs, not the controlling root cause.

## Triage decision

Escalate `bd-5j2` from localized implementation work to a narrow Ledger design correction. Do not weaken the p95 threshold, discard outliers, retry until passing, or suppress the gate under host load.

The design must reduce synchronous first-page work while preserving the published query contract, exact totals and groups, immutable pagination membership, generation and hierarchy invalidation, crash safety, and privacy boundaries. Candidate directions include transactionally maintained actuals projections or another reusable snapshot basis; selecting one requires explicit design and plan coverage because it changes persistence and recovery behavior.

## Resolution

Blocked at the design boundary after three failed implementation approaches. Resume only through the Lex design/plan correction for the actuals snapshot architecture, then recompile the affected bead contract before implementation.
