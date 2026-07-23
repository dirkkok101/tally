# Ledger refund and statement-scope correction work review

Date: 2026-07-23

Plan: `PLAN-LEDGER-V1`

Reviewed beads: `bd-1ge`, `bd-1ku`, and `bd-1jr`

Reviewed implementation range: `71a08e7..52c31b9`

## Verdict

| Axis | Verdict | Residual findings |
|---|---|---:|
| Spec conformance | PASS | 0 |
| Code quality | PASS | 0 |
| Relevant quality gates | PASS with one pre-existing warning | 0 introduced failures |

This review closes the targeted full-refund-only correction and the public statement-scope seam needed to resume Ledger execution. It does not assert that the whole Ledger plan is complete or archive-eligible: 12 downstream verification or module-gate beads remain open.

## Satisfaction evidence

| Contract | Implementation evidence | Test evidence | Status |
|---|---|---|---|
| Provider-neutral statement-scope registration is publicly discoverable and invocable | `LedgerServices`, `OperationRegistry`, `TallyProcess`, and `ReconciliationOperationBundle` publish `ledger.reconciliation.scope.register` with stable public error mapping | `PublicContractInventoryTests`; `PublishedReconciliationScopeContractTests` | PASS |
| Scope registration is atomic and idempotent | Existing scope handler and store are composed through the normal mutation pipeline | 39 focused reconciliation operation tests; 8 published-process scope tests | PASS |
| Refunds are full-amount only | Existing `RefundPolicy` and confirmation handler require exact same-account ZAR legs; no partial-refund accumulation was introduced | `UC010RefundWorkflowTests` rejects partial, over, unmatched, incompatible, and duplicate cases | PASS |
| One active refund relationship per eligible transaction | The existing relationship-role exclusivity trigger and store guard remain intact | UC-010 duplicate, role-reuse, and concurrent competing-refund cases | PASS |
| Refund facts and lifecycle remain immutable and replay-safe | Ordinary immutable transactions are linked by the canonical refund relationship and normal idempotency machinery | UC-010 source immutability, history, same-key/cross-key replay, changed replay, crash atomicity, and retry cases | PASS |
| Actuals remain dimensionally exact | Refund credit offsets external spend and budget actuals in the credit period while preserving account movement semantics | UC-010 category, pool, period, later-correction, and roll-up assertions | PASS |

Current passed verification records were harvested for:

- `FR-LEDGER-REFUND-CONFIRMATION` via `TC-LEDGER-REFUND-CONFIRMATION-CONTRACT` and `bd-1jr`.
- `FR-LEDGER-RECONCILIATION-COVERAGE` via `TC-LEDGER-STATEMENT-SCOPE-REGISTRATION` and `bd-1ge`.
- `FR-LEDGER-IDEMPOTENT-WRITES` via `TC-LEDGER-STATEMENT-SCOPE-REGISTRATION` and `bd-1ge`.

## Quality-gate evidence

- Build: succeeded with 0 warnings and 0 errors.
- Focused reconciliation operation bundle: 39 passed, 0 failed.
- Public contract inventory: 84 passed, 0 failed.
- Published statement-scope contract: 8 passed, 0 failed.
- UC-010 workflow: 19 passed, 0 failed, rerun successfully after strengthening concurrency and crash-atomicity assertions.
- Native AOT `linux-x64` publish: succeeded with 0 warnings and 0 errors.
- Formatting and `git diff --check`: clean.
- Full suite: 1444 passed, 1 failed, 0 skipped. The only failure is `ActualsPersonalScaleTests.Published_pool_category_actuals_path_meets_personal_scale_budget`, whose observed p95 was 2107.5 ms against a 2000 ms threshold.
- Detached pre-slice baseline at `71a08e7`: the same isolated performance test also failed, with p95 4776.4 ms. The failure is therefore pre-existing and outside this correction slice.
- `lex coverage --module LEDGER --json`: 25/25 active functional requirements covered.
- `lex decision path-check --module LEDGER --json`: healthy.

The existing `lex endpoint suggest` CLI-surface warnings and external-dependency citation warnings are module-wide and pre-date this diff; they are not regressions from the reviewed work.

## Review conclusions

No must-fix findings, silent failures, contract drift, provider coupling, untraced implementation files, or new test gaps remain in the reviewed slice. The current exclusivity trigger is correct for the full-refund-only model and was not weakened. No follow-up bead is required for this correction.

The broader plan stays active. Its remaining frontier consists of UC-005, UC-007, UC-009, UC-011 through UC-018 verification beads and the final module gate.
