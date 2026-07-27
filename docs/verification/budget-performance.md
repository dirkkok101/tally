# BUDGET personal-scale performance

Status: verification gate for `TASK-BUDGET-GATE-PERFORMANCE` / `NFR-BUDGET-PERSONAL-SCALE-PERFORMANCE` / `TC-BUDGET-PERSONAL-SCALE-PERFORMANCE`.

This report is **metadata-only**. It records load parameters, sample counts, percentile timings, memory, environment fingerprint, and exact-check outcomes. It does **not** include plan amounts, category display names, raw idempotency keys, request/response JSON, or other financial payloads.

## Gate command

```bash
bash scripts/verify-budget-performance.sh
```

Expected: exit 0; non-vacuous discovery of `BudgetPersonalScalePerformanceTests`; ≥100 measured samples per operation after warm-up; exact-result reconciliation on every sample; p50/p95/max, peak resident memory, and mean output size reported; environment fingerprint recorded.

NFR p95 targets (release host, quiet):

| Operation | p95 budget |
|---|---:|
| `budget.position.get` | ≤ 3000 ms |
| `budget.insights.evidence.get` | ≤ 3000 ms |
| `budget.plan.draft.create` | ≤ 1000 ms |
| `budget.plan.revision.activate` | ≤ 1000 ms |
| `budget.plan.revision.get` | ≤ 1000 ms |
| `budget.plan.revision.list` | ≤ 1000 ms |

On contended shared hosts, set `BUDGET_PERF_ADVISORY_P95=1` (script default) to keep measurements blocking only on sample count, exact checks, and hang floors. Set `BUDGET_PERF_ADVISORY_P95=0` on a quiet release host to hard-fail on NFR p95 miss.

## Evidence surface

| Artifact | Role |
|---|---|
| `tests/Tally.Tests/Budget/Performance/BudgetPersonalScalePerformanceTests.cs` | Load generator, published-handler benchmark, exact-result guards (`BudgetPerformanceGateEvidence`) |
| `scripts/verify-budget-performance.sh` | Discovery + Release/Debug benchmark runner |
| `docs/verification/budget-performance.md` | This metadata-only report |

## Load condition (approved personal scale)

| Dimension | Count | Notes |
|---|---:|---|
| Active LEDGER transactions | 100000 | Synthetic bulk seed; no private fixture payloads |
| Transactions dated in selected period | 800 | Complete period snapshot drained (non-vacuous members); remaining actives load the store |
| Budget periods (plans) | 1000 | Consecutive ZAR months from selected period |
| Revisions in selected period | 1000 | 999 Superseded + 1 Active |
| Category budget entries in selected revision | 1000 | One row per synthetic category id |
| Insight `memberLimit` | 100000 | Maximum supported evidence members |
| Measured samples / op | 100 | After 3 warm-up invocations |
| Network | disabled | Offline in-process composition; no migration/backup overlap |

## Measurement method

1. Seed ledger + budget stores with synthetic bulk SQL (triggers reinstalled; no private amounts or PII).
2. Resolve the six published BUDGET operation handlers from `BudgetOperationBundle` (same factories as `TallyProcess`).
3. Warm up each operation, force GC, then record ≥100 timed invocations.
4. Each sample runs exact-result guards (status, identity, planned total, actual total, member counts, binding presence).
5. Report p50 (middle order statistic), p95 (`ceil(n*0.95)-1` after sort), maximum, mean output bytes, peak working set, environment fingerprint.
6. Delete ephemeral LEDGER query snapshots between position/insight samples so snapshot time remains inside the measured path without unbounded disk growth.

## Governing decisions and NFR

- `DD-BUDGET-APPLICATION-ARCHITECTURE` — six typed vertical slices; one public-contract seam; handlers under test are the published boundary.
- `DD-BUDGET-EXACT-POSITION-CALCULATION` — one complete LEDGER snapshot + pure calculator; exact totals reconciled every sample.
- `NFR-BUDGET-PERSONAL-SCALE-PERFORMANCE` — personal-scale load with p95 targets above.
- `NFR-BUDGET-SELF-CONTAINED-LOCAL-OPERATION` — offline; no network service required for the gate.

## How to re-run

```bash
dotnet build Tally.slnx --nologo
# Quiet release host (hard p95):
BUDGET_PERF_ADVISORY_P95=0 bash scripts/verify-budget-performance.sh
# Contended host (metadata + sample/exact gates):
bash scripts/verify-budget-performance.sh
```

## Result

Record runner exit code, sample counts, p50/p95/max per operation, peak memory, and whether each NFR p95 target passed. Do not paste financial payloads, category names, raw keys, or absolute secret paths into this file.

## Latest run

Executed on 2026-07-27 via `bash scripts/verify-budget-performance.sh` (~21 minutes wall clock on a 16-CPU shared host).

| Check | Result |
|---|---|
| Host platform | Linux Ubuntu 26.04; 16 CPUs; .NET 10.0.10 |
| `dotnet build Tally.slnx` | Passed (via script build of solution/tests) |
| Discovery | 1 case (`TC_BUDGET_PERSONAL_SCALE_PERFORMANCE_six_operations_meet_p95_targets`) |
| Load scale | 100000 active txns; 800 in-period members; 1000 periods; 1000 selected revisions; 1000 entries |
| Samples / op | 100 after 3 warm-up (exact=100/100 each) |
| Peak working set | 215887872 bytes (~206 MiB); baseline 166273024 bytes |
| `BUDGET_PERF_ADVISORY_P95` | 1 (advisory p95 on contended host) |
| Script exit | 0 |

### Per-operation timings (ms)

| Operation | n | exact | p50 | p95 | max | NFR p95 budget | vs NFR |
|---|---:|---:|---:|---:|---:|---:|---|
| `budget.plan.draft.create` | 100 | 100 | 1090.1 | 1241.4 | 1378.7 | 1000 | miss (advisory) |
| `budget.plan.revision.activate` | 100 | 100 | 1090.9 | 1285.2 | 1405.4 | 1000 | miss (advisory) |
| `budget.plan.revision.get` | 100 | 100 | 1095.3 | 1288.2 | 1362.5 | 1000 | miss (advisory) |
| `budget.plan.revision.list` | 100 | 100 | 2.6 | 3.9 | 5.6 | 1000 | pass |
| `budget.position.get` | 100 | 100 | 3806.9 | 4376.2 | 4884.2 | 3000 | miss (advisory) |
| `budget.insights.evidence.get` | 100 | 100 | 4802.5 | 5115.1 | 5609.9 | 3000 | miss (advisory) |

Notes: Category catalogue list of 1000 identities through the public LEDGER process seam dominates draft/activate/get (~1.1s). Position/insight include one complete LEDGER actuals snapshot drain over the 100k-row active store for the selected period (800 members) plus 1000 plan entries. Re-run with `BUDGET_PERF_ADVISORY_P95=0` on a quiet release host to hard-enforce NFR p95.
