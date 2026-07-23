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

1. Why does the observed p95 cross 2,000 ms? The query runs concurrently with multiple CPU-intensive builds, test hosts, Angular development servers, browser processes, and Aspire services on a 16-core shared host.
2. Why does that invalidate the gate rather than fail the product NFR? `NFR-LEDGER-PERSONAL-SCALE-PERFORMANCE` explicitly requires the supported reference workstation, one writer, and no concurrent load.
3. Why did allocation reductions not stabilize the observed p95? They reduced ordinary work but could not control scheduling delays caused by unrelated processes outside the test.
4. Why did some unchanged or lightly optimized runs pass? When scheduling contention subsided, measured p95 fell below 2,000 ms, consistent with an environment-sensitive result.
5. Why should the snapshot design remain unchanged for now? No failing 30-run Release result has been captured under the NFR's declared no-concurrent-load condition, so the evidence does not prove a product or architecture defect.

Root cause: the module gate was executed on a heavily shared host that violates the accepted performance test's explicit no-concurrent-load precondition. The benchmark therefore cannot produce a valid pass or fail decision for `NFR-LEDGER-PERSONAL-SCALE-PERFORMANCE` in the current environment.

Classification: verification-environment blocker. The synchronous snapshot path has limited margin and may still warrant future optimization, but the current evidence cannot justify a design change. Allocation and textual encoding are measured costs, not a proven contract failure under the supported load condition.

## Triage decision

Do not change the Ledger design, weaken the p95 threshold, discard outliers, retry until passing, or suppress the gate under host load. Preserve all unrelated workloads as required by the execution scope.

Run the existing 30-sample Release gate on a supported reference workstation with no concurrent load and record median, p95, environment, and correctness. Only if that valid run fails should `bd-5j2` return to Lex design for a snapshot-architecture correction.

## Resolution

Blocked on a valid performance verification environment. The Lex design review found the graph already defines the missing precondition, so no design or plan entity was changed. Resume when the declared no-concurrent-load Release measurement can be run without disrupting unrelated work.
