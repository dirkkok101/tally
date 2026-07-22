# Ledger refund correction work review

Date: 2026-07-22  
Plan: `PLAN-LEDGER-V1`  
Reviewed bead: `bd-10w` / `TASK-LEDGER-REFUNDS`  
Reviewed implementation: `e14542ef305f05602500037a8ca61e60362b0ac4`

## Verdict

| Axis | Verdict | Residual findings |
|---|---|---:|
| Spec conformance | PASS | 0 |
| Code quality | PASS | 0 |

This was a deliberately narrow review of the full-amount refund correction and its implementation diff. It does not assert that the remaining Ledger plan is complete or archive-eligible.

## Bead satisfaction

| Contract | Implementation evidence | Test evidence | Status |
|---|---|---|---|
| Exact active same-account ZAR full refund | `RefundPolicy.TryFullAmount`; `ConfirmRefundHandler` | `FR_LEDGER_REFUND_CONFIRMATION_exact_full_amount_creates_active_refund`; boundary-amount test | PASS |
| Partial and over-refund rejection without retained effect | `RefundPolicy.TryFullAmount`; transactional failure rollback | `FR_LEDGER_REFUND_CONFIRMATION_partial_and_over_refunds_are_rejected_atomically` | PASS |
| Missing, incompatible, inactive, sign, account, and currency failures | `ConfirmRefundHandler`; `RefundErrors`; process error map | Missing-participant, inactive-participant, archived-account, sign, account, invalid-contract, and non-ZAR tests | PASS |
| One active relationship per participant | Existing `RelationshipStore.HasActiveRoleAsync` and `financial_relationship_roles_are_exclusive_before_insert` | Second-refund, credit-reuse, transfer-overlap, and direct-trigger tests | PASS |
| Stable request and logical-effect replay | `LedgerMutationExecutor.ExecuteAsync` with refund request and logical identities | `FR_LEDGER_REFUND_CONFIRMATION_replays_are_stable_and_changed_replay_conflicts` | PASS |
| Immutable source transactions and queryable relationship history | Existing immutable relationship storage plus refund detail retrieval | Source-immutability, relationship-get/history, and relationship-row immutability tests | PASS |
| Exclusivity trigger/store remain unchanged | No diff in either governing file | Direct duplicate insert is rejected by the existing trigger | PASS |

The graph coverage map links `FR-LEDGER-REFUND-CONFIRMATION` to `TC-LEDGER-REFUND-CONFIRMATION-CONTRACT`. A current passed verification record was harvested for that tuple and bead.

## Review findings and convergence

Iteration 1 found one test-proof gap: the currency test covered a ZAR/USD mismatch but did not explicitly exercise two matching non-ZAR values. The existing policy already rejected that case. The test was extended to prove both mismatch and same-currency non-ZAR rejection, then the focused and full suites were rerun successfully.

No bugs, silent failures, type-design defects, graph-intent violations, hygiene issues, scope creep, or residual test gaps survived verification.

## Refuted concerns

- Weakening the relationship-role trigger is not required. The current trigger and store guard already enforce one active relationship for either participant, and both files have a zero-line diff.
- A new persistence migration is not required for confirmation. Refund uses the existing immutable `financial_relationship` row, explicit refund roles, and append-only lifecycle model.
- Matching transaction IDs under a new idempotency key do not create a duplicate effect; the existing logical-effect identity returns the original result only for semantically identical input and conflicts on changed input.

## Quality-gate evidence

- Focused refund suite: 27 passed, 0 failed.
- Full test suite: 547 passed, 0 failed.
- `dotnet build --no-restore`: succeeded with 0 warnings and 0 errors.
- `dotnet format --verify-no-changes --no-restore`: succeeded.
- Native AOT publish for `linux-x64`: succeeded; the published executable exposes `ledger.refund.confirm` as a typed idempotent mutation with the documented stable errors.
- `git diff --check`: clean.
- `lex coverage --module LEDGER --json`: healthy, 25/25 requirements covered, no gaps.
- `lex decision path-check --module LEDGER --json`: healthy, including `DD-LEDGER-FULL-AMOUNT-REFUND-RELATIONSHIP`.

`lex endpoint suggest` and `lex external-dependency check` still report pre-existing module-wide informational warnings unrelated to this CLI refund diff. They were not treated as findings or changed.

## Strengths

- Financial eligibility is centralized in a small pure policy with exact minor-unit comparisons and stable failure codes.
- The handler follows the established transfer vertical slice while preserving transaction immutability and transactional idempotency.
- Tests exercise both the public process contract and the existing database trigger, so duplicate prevention is proven at both boundaries.

## Traceability and compounding

All implementation and graph changes in the reviewed commit trace to `bd-10w` and Dirk's full-refund-only correction. No untraced implementation files were found. The governing FR, use cases, assumption, data models, design decision, plan tasks, test case, and bead contract were corrected before implementation; the review found no further reusable pattern or open-question delta to compound.
