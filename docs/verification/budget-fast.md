# BUDGET fast verification (inner loop)

Status: per-bead / edit-cycle gate for BUDGET work.

This is the **default validation command during bead execution**. It is intentionally
small and pure. It does **not** replace the module completion gate.

| Gate | Command | When |
|---|---|---|
| **Fast (inner loop)** | `bash scripts/verify-budget-fast.sh` | Every feature bead, local iteration, post-edit verify |
| **Module (ship)** | `bash scripts/verify-budget-module.sh` | Plan completion, release, `TASK-BUDGET-GATE-MODULE` only |

## Command

```bash
bash scripts/verify-budget-fast.sh
```

Extended (still no AOT / no personal-scale perf):

```bash
BUDGET_FAST_EXTENDED=1 bash scripts/verify-budget-fast.sh
```

## What it runs

| Suite | Floor | Role |
|---|---:|---|
| `BudgetContractShapeTests` | ≥4 | Additive contract fields; calculation schema `budget-position-v2` |
| `BudgetPositionCalculatorTests` | ≥39 | Flat/exact identity, buckets, overflow, reconciliation |
| `BudgetEnvelopeResolutionTests` | ≥10 | Nearest-ancestor resolution + partition |
| `BudgetEnvelopeIntegrityTests` | ≥5 | Ancestry integrity, overflow, refund, archived ancestor |

With `BUDGET_FAST_EXTENDED=1`:

| Suite | Floor | Role |
|---|---:|---|
| `GetBudgetPositionQueryTests` | ≥40 | Query wiring + ancestry required-ids |
| `BudgetEnvelopeProvenanceTests` | ≥3 | Process-surface ancestry / effective category / reparent |

## What it deliberately skips

- Native AOT publish
- Personal-scale performance (`BudgetPersonalScalePerformanceTests`, 100k txs / 100 samples × 6 ops)
- Full Budget xUnit surface (~685 tests)
- Specialized ship scripts: contract, recovery, security, graph, performance
- Full UC matrices (`BudgetUc001`–`005`) except when a bead names them explicitly

## Timing budget

| Limit | Default | Override |
|---|---:|---|
| Soft target | 60s | `BUDGET_FAST_SOFT_SECONDS` |
| Hard fail | 90s | `BUDGET_FAST_HARD_SECONDS` |

Cold first run may approach the soft target; warm runs should be well under 30s.

## Per-bead policy

`/lex:execute` and manual bead work must use:

1. The bead's own `## Verification` commands when they are **targeted** (single suite filters), or
2. `bash scripts/verify-budget-fast.sh` when a broad Budget check is needed

**Do not** run `scripts/verify-budget-module.sh` as the default per-bead verify step.
That script is reserved for `TASK-BUDGET-GATE-MODULE` / release completion.

## How to re-run

```bash
dotnet build Tally.slnx --nologo -v q
bash scripts/verify-budget-fast.sh
```
