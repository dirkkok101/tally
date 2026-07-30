# BUDGET graph and evidence quality

Status: verification gate for `TASK-BUDGET-GATE-GRAPH-QUALITY` / `PAT-CORE-IMPLEMENTATION-PLAN-QUALITY-GATES` / bead `bd-1iz3`.

This report is **metadata-only**. It records graph commands, exact counts, ref-codes, suite discovery, and content fingerprints. It does **not** include plan amounts, category display names, raw idempotency keys, request/response JSON, or other financial payloads.

## Gate command

```bash
bash scripts/verify-budget-graph.sh
```

Expected: exit 0; coverage is 11 of 11 with 30 linked tests and zero gaps; all 39 design paths match; link suggestions are empty; three CLI-only endpoint heuristics are explicitly matched as non-applicable; every named suite discovers tests; plan coverage/audit/context/dependency checks are clean; forbidden-surface and placeholder scans are empty.

## Evidence surface

| Artifact | Role |
|---|---|
| `tests/Tally.Tests/Budget/BudgetGraphEvidenceGuardTests.cs` | Named-suite presence + forbidden-surface + placeholder guards (`BudgetGraphQualityEvidence`) |
| `scripts/verify-budget-graph.sh` | Graph, plan, discovery, and forbidden-surface runner |
| `docs/verification/budget-graph.md` | This metadata-only report |
| `.lexicon/graph/BUDGET/module.json` | Canonical module (read-only for this gate) |

## Graph commands and expected counts

| Command | Expected |
|---|---|
| `lex coverage --module BUDGET --json` | Status `healthy`; 11/11 active FRs covered; 30 unique linked TCs; 0 orphans; 0 gaps |
| `lex decision path-check --module BUDGET --json` | Status `healthy`; 39/39 expected paths matched; `missing_count=0` |
| `lex link suggest --module BUDGET --json` | Empty list |
| `lex endpoint suggest --module BUDGET --json` | Exactly 3 heuristics (below), recorded as non-applicable |
| `lex endpoint list --module BUDGET --json` | Empty (no HTTP endpoint entities) |
| `lex external-dependency check --module BUDGET --json` | Four deps with linked TC evidence (statuses recorded; not force-validated without evidence) |
| `lex plan coverage PLAN-BUDGET-V1 --json` | `gap_count=0` |
| `lex plan audit PLAN-BUDGET-V1 --json` | `blocking_finding_count=0` |
| `lex plan status PLAN-BUDGET-V1 --json` | `planning_state=ready`; 29 tasks |
| `lex context <TASK> --max-tokens 2500 --json` | All 29 tasks within 2500 tokens |

## Endpoint heuristics (CLI-only, non-applicable)

The accepted design is **local structured CLI** with zero Lex endpoint entities. The three known heuristics must remain present and must **not** be silenced by adding HTTP:

| Rule | Source FR | Disposition |
|---|---|---|
| `detail-flow` | `FR-BUDGET-INSIGHTS-PROJECTION` | Non-applicable — INSIGHTS evidence is a CLI operation, not GET item-detail |
| `management-write-flow` | `FR-BUDGET-PLAN-ACTIVATION` | Non-applicable — activation is a typed CLI mutation, not POST |
| `search-flow` | `FR-BUDGET-POSITION-QUERY` | Non-applicable — position is a CLI query, not HTTP search |

## External dependencies (evidence-linked)

| Ref | Linked test cases (examples) | Recorded status |
|---|---|---|
| `EXT-BUDGET-LEDGER-PUBLIC-CONTRACT` | `TC-BUDGET-LEDGER-COMPOSITION-CONTRACT`, `TC-BUDGET-PUBLIC-CONTRACT-COMPATIBILITY` | evidence-linked (`assumed`/`partial` until production pairing marks validated) |
| `EXT-BUDGET-HOST-OS-SECURITY` | `TC-BUDGET-LOCAL-DATA-PROTECTION`, `TC-BUDGET-PUBLIC-CONTRACT-COMPATIBILITY` | evidence-linked |
| `EXT-BUDGET-AI-AGENT-HOST` | `TC-BUDGET-CONTRACT-DISCOVERY-CONTRACT`, `TC-BUDGET-STRUCTURED-INVOCATION-CONTRACT`, `TC-BUDGET-PUBLIC-CONTRACT-COMPATIBILITY` | evidence-linked |
| `EXT-BUDGET-INSIGHTS-CONSUMER-CONTRACT` | `TC-BUDGET-INSIGHTS-PROJECTION-CONTRACT`, `TC-BUDGET-PUBLIC-CONTRACT-COMPATIBILITY` | evidence-linked |

Validation status is **not** flipped to `validated` by this gate without executable consumer evidence (failure criterion).

## Named suites (nonzero discovery required)

Per-class discovery must be ≥1 before aggregate Budget totals are accepted:

| Family | Classes |
|---|---|
| Feature / domain / storage | `CreateBudgetDraftCommandTests`, `ActivateBudgetPlanRevisionCommandTests`, `BudgetPlanReadQueryTests`, `BudgetPeriodTests`, `BudgetPositionCalculatorTests`, `BudgetEnvelopeResolutionTests`, `BudgetEnvelopeIntegrityTests`, `GetBudgetPositionQueryTests`, `BudgetMutationExecutorTests`, `BudgetStateStoreTests`, `BudgetHistoryInvariantTests`, `BudgetProcessContractTests`, `BudgetOperationContractTests` |
| Ledger | `BudgetLedgerBoundaryArchitectureTests`, `BudgetLedgerContractClientTests`, `LedgerBudgetActualsProjectionTests`, `LedgerBudgetCategoryLifecycleTests`, `LedgerBudgetPrerequisiteTests` |
| Contract | `BudgetContractShapeTests`, `BudgetPublishedContractTests` |
| Recovery | `BudgetAtomicRecoveryTests` |
| Security | `BudgetSecurityGateTests` |
| Performance | `BudgetPersonalScalePerformanceTests` |
| INSIGHTS | `BudgetInsightsContractTests` |
| UC | `BudgetUc001DraftTests` … `BudgetUc005AgentContractTests`, `BudgetEnvelopeProvenanceTests` |
| Graph gate | `BudgetGraphEvidenceGuardTests` |

## Design path note (recovery)

`DD-BUDGET-STATE-STORE` expected paths include `tests/Tally.Tests/Budget/Recovery/**` rather than a Features CLI recovery surface. Budget recovery is **restart inspection + same-key mutation replay** against owner-only `budget.db` (Infrastructure Storage/idempotency). There is no public recovery operation vocabulary.

## Forbidden surfaces and placeholders

Scans over Budget `src` and `tests` must find **zero**:

- HTTP / FastEndpoints / AspNetCore / `HttpClient` / listeners
- EF / `DbContext` / Npgsql
- Hosted services / plugin loaders
- `TODO` / `FIXME` / `HACK` / `XXX` / `NotImplementedException`

## Plan quality

- Coverage: all required refs covered; gate/validation tasks may remain intentionally loose (no `implements`)
- Audit: zero blocking findings (informational optional-generic-ref notes allowed)
- Dependencies among `TASK-BUDGET-*` are acyclic
- Context budgets: ≤ 2500 tokens per task (`lex context --max-tokens 2500`)

## How to re-run

```bash
dotnet build Tally.slnx --nologo
lex coverage --module BUDGET --json | jq '.Summary'
lex decision path-check --module BUDGET --json | jq '{status, missing_count}'
bash scripts/verify-budget-graph.sh
```

## Result

Record the runner exit code, FR/TC/path counts, per-class discovery counts, endpoint-heuristic disposition, external-dep statuses, and content fingerprints when the gate is executed. Do not paste financial payloads.

## Latest run

Executed on 2026-07-29 via `bash scripts/verify-budget-graph.sh` after envelope-resolution plan extension (29 tasks, 30 linked TCs).

| Check | Result |
|---|---|
| `lex coverage` | 11/11 FRs; 30 unique linked TCs; 0 orphans; healthy |
| `lex decision path-check` | 39/39 paths; healthy |
| `lex link suggest` | 0 suggestions |
| `lex endpoint suggest` | 3 heuristics → non-applicable CLI-only |
| `lex endpoint list` | 0 entities |
| External deps | 4 evidence-linked (not force-validated without consumer pairing) |
| Plan coverage / audit | 178/178 covered; 0 gaps; 0 blocking findings |
| Context budgets | 29/29 core sections ≤ 2500 tokens |
| Dependencies | Acyclic (29 tasks) |
| Named suite discovery | 31 classes, each ≥1 (aggregate Budget discovery 685) |
| Guard tests | 7/7 passed |
| Forbidden / placeholder scans | 0 hits |
| Script exit | 0 |

### Content fingerprints (metadata)

| Artifact | SHA-256 prefix | Bytes |
|---|---|---:|
| `scripts/verify-budget-graph.sh` | `1fb997df79ec5912…` | 22028 |
| `docs/verification/budget-graph.md` | `8fb6e1fba3e84e36…` | 7064 |
| `tests/Tally.Tests/Budget/BudgetGraphEvidenceGuardTests.cs` | `4f0c4a994d1b824b…` | 9209 |
| `.lexicon/graph/BUDGET/module.json` | `41feba97a98cd3fb…` | 12619 |
