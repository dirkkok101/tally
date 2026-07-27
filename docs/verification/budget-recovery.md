# BUDGET mutation durability and restart recovery

Status: verification gate for `TASK-BUDGET-GATE-ATOMIC-RECOVERY` / `NFR-BUDGET-ATOMIC-DURABLE-MUTATIONS` / `NFR-BUDGET-ATTRIBUTABLE-HISTORY`.

This report is **metadata-only**. It records cutpoint inventory, inspection invariants, and runner outcomes. It does **not** include plan amounts, category display names, raw idempotency keys, request/response JSON, or other financial payloads.

## Gate command

```bash
bash scripts/verify-budget-recovery.sh
```

Expected: exit 0; every named draft and activation cutpoint is discovered; restart inspection reports prior-or-complete state, at most one Active, exact replay after post-commit interruption, owner-only artifacts, and 0 failures.

## Evidence surface

| Artifact | Role |
|---|---|
| `tests/Tally.Tests/Budget/Recovery/BudgetAtomicRecoveryTests.cs` | Failure-injection restart matrix against real `budget.db` via `BudgetMutationExecutor` fault hooks and `BudgetStateStore` |
| `scripts/verify-budget-recovery.sh` | Discovery + execution runner for the recovery gate |
| `docs/verification/budget-recovery.md` | This metadata-only report (`BudgetRecoveryGateEvidence`) |

## Named cutpoints

### Draft create

| Cutpoint | Injection | Expected restart observation |
|---|---|---|
| `before_validation` | Missing idempotency key rejected before `BEGIN IMMEDIATE` | Unchanged empty/pre-operation state |
| `after_validation` | Fault after identity validation, before durable writes | Unchanged pre-operation state |
| `replay_lookup` | Fault at start of mutate after Miss lookup | Unchanged pre-operation state |
| `revision_insert` | Fault after revision row insert | Rolled back; prior state |
| `entry_insert` | Fault after entry row insert | Rolled back; prior state |
| `events` | Fault after draft lifecycle event insert | Rolled back; prior state |
| `outcome_references` | `BudgetMutationFaultPoint.BeforeCommit` after outcome refs | Rolled back; prior state; key reusable |
| `commit` | `BudgetMutationFaultPoint.BeforeCommit` | Rolled back; prior state; key reusable |
| `result_delivery` | `BudgetMutationFaultPoint.AfterCommit` | Exactly one complete draft chain; retry exact event-time replay |

### Activate revision (with prior Active)

| Cutpoint | Injection | Expected restart observation |
|---|---|---|
| `before_validation` | Empty key rejected before writer | Unchanged prior Active |
| `after_validation` | Fault after identity validation | Unchanged prior Active |
| `replay_lookup` | Fault after Miss lookup | Unchanged prior Active |
| `prior_supersession` | Fault after prior supersession write | Rolled back; prior Active intact |
| `activation` | Fault after Draft→Active status update | Rolled back; prior Active intact |
| `active_pointer` | Fault after `active_revision_id` update | Rolled back; prior Active intact |
| `events` | Fault after activation lifecycle events | Rolled back; prior Active intact |
| `outcome_references` | `BeforeCommit` after outcome refs | Rolled back; prior Active; key reusable |
| `commit` | `BeforeCommit` | Rolled back; prior Active; key reusable |
| `result_delivery` | `AfterCommit` | Exactly one Active (new); prior Superseded; retry exact event-time replay |

## Restart inspection invariants

After every cutpoint, a **new** `BudgetStateStore` reopens the durable files (no in-process-only assertion):

1. **Prior-or-complete** — either the exact pre-operation fingerprint or one complete revision / lifecycle / idempotency outcome chain.
2. **At most one Active** — partial unique Active index and pointer agree; never multiple Active rows.
3. **No partial chains** — every revision has entries and lifecycle evidence; idempotency rows only exist with complete outcome refs.
4. **Exact replay** — post-commit retries rehydrate the original event-time snapshot (status, event IDs, hashes, actor/reason) without re-mutating.
5. **Attributable history** — sequences, prior/result statuses, replacement IDs, actor/reason, and active pointer reconcile.
6. **Owner-only artifacts** — `budget.db`, WAL, SHM, lock, and atomic sidecars remain mode `600` under owner-only directories; `PRAGMA synchronous=FULL` and WAL remain enforced.

## Governing decisions

- `DD-BUDGET-IDEMPOTENT-MUTATIONS` — transactional replay from immutable outcome references
- `DD-BUDGET-PLAN-REVISION-LIFECYCLE` — immutable revision payloads with atomic lifecycle transitions
- `DD-BUDGET-STATE-STORE` — separate owner-only raw-SQLite budget state

## How to re-run

```bash
dotnet build Tally.slnx --nologo
dotnet test tests/Tally.Tests/Tally.Tests.csproj \
  --filter 'FullyQualifiedName~BudgetAtomicRecoveryTests' \
  --logger 'console;verbosity=normal'
bash scripts/verify-budget-recovery.sh
```

## Result

Record the runner exit code and discovered case count when the gate is executed. Do not paste financial payloads, category names, or raw keys into this file.

## Latest run

Executed on 2026-07-27 via `bash scripts/verify-budget-recovery.sh` (with unrelated incomplete `Budget/Acceptance` WIP temporarily excluded so the test project can compile):

| Check | Result |
|---|---|
| `dotnet build Tally.slnx` | Passed, 0 warnings, 0 errors |
| Discovery (`BudgetAtomicRecoveryTests`) | 22 cases |
| Named draft cutpoints | 9 present (`before_validation` … `result_delivery`) |
| Named activation cutpoints | 10 present (`before_validation` … `result_delivery`, including `prior_supersession` / `activation` / `active_pointer`) |
| xUnit execution | 22 passed, 0 failed, 0 skipped |
| Script exit | 0 |

Note: concurrent untracked WIP under `tests/Tally.Tests/Budget/Acceptance/` currently fails to compile and is outside this bead’s file reservation; the recovery gate itself is green when that WIP is not compiled into the test assembly.
