# Ledger v1 work review

Date: 2026-07-23

Plan: `PLAN-LEDGER-V1`

Reviewed implementation range: `ee7c96bc2f8a645d9ccbe409c725a514b416ccf0..ecf6457f1339842fac4b11b0cd66f71885539cd2`

Proof-harvest commit: `1ae1f90`

## Verdict

| Axis | Verdict | Residual finding |
|---|---|---|
| Spec conformance | PASS for the Ledger CLI implementation; FAIL for the current sensitive-data deployment | `NFR-LEDGER-LOCAL-DATA-PROTECTION` lacks external proof that the intended data root is on a host-managed encrypted volume (`bd-64c`) |
| Code quality | PASS | None |

The Ledger v1 implementation is ready for synthetic or anonymized CLI field testing. The plan is not archive-eligible, and genuine bank data must not be loaded, until `bd-64c` proves the selected `TALLY_DATA_ROOT` is protected at rest.

## Strengths

- The public boundary is one provider-neutral, self-describing CLI contract. `src/Tally/Cli/OperationRegistry.cs`, `src/Tally/Cli/TallyProcess.cs`, and the operation modules publish exactly 74 stable operations without an AgentMail, WhatsApp, mailbox, MIME, or transport dependency.
- `src/Tally/Infrastructure/Storage/` keeps canonical transactions, evidence, reconciliation decisions, relationships, attribution, actuals, recovery, and idempotency in one durable SQLite authority with crash and recovery coverage.
- The refund implementation preserves ordinary immutable bank transactions, a single exact full-amount refund relationship, role exclusivity, duplicate rejection, replay safety, and exact actuals.
- The reconciliation implementation is match-first and fail-closed, with statement authority, explicit decisions, coverage exceptions, and crash-atomic activation.
- The complete public contract is exercised through the published linux-x64 NativeAOT executable, not only through in-process handlers.

## Bead satisfaction and durable proof

- `PLAN-LEDGER-V1` has 73 tasks, 73 compiled beads, 73 closed execution statuses, no blocked task, no plan warning, no dependency cycle, and no plan-coverage or plan-audit gap.
- All 25 active functional requirements have a current passed verification record tied to the graph-linked test case actually cited by the implementation tests and to the originating bead.
- Eight of nine required non-functional requirements have current passed evidence.
- `NFR-LEDGER-LOCAL-DATA-PROTECTION` has a durable failed record tied to `TC-LEDGER-LOCAL-DATA-PROTECTION` and follow-up `bd-64c`. Owner-only permissions, private diagnostics, and artifact controls pass; deployment encryption evidence does not.
- The failed deployment record keeps the plan correctly non-archive-eligible without weakening the product contract.

## Build and test evidence

Review iteration 1 passed the complete module gate before the traceability correction:

- Restore, build, and formatting: 0 warnings, 0 errors, clean format verification.
- NativeAOT `linux-x64` publication: passed.
- Full suite: 1,715 passed, 0 failed, 0 skipped across all 82 test classes.
- Public contract: 84 passed, including exact discovery of 74 operation IDs and CLI paths.
- Core gate: 27 passed.
- Security, recovery, and privacy gate: 339 passed.
- Published use cases: 322 passed across all 18 workflows.
- Personal-scale correctness and timing: 100,000 transactions, 30 runs, p50 1,495.0 ms and p95 1,637.4 ms; timing is advisory and exact-result assertions are blocking.
- Lex fast check, documentation lint, 25/25 coverage, plan coverage, plan audit, dependency-cycle check, and repository diff integrity: passed.

Review iteration 2 reran the same complete gate after the correction and also passed:

- Full suite: 1,715 passed, 0 failed, 0 skipped in 7 minutes 10 seconds.
- Public contract: 84 passed; core: 27 passed; security/recovery/privacy: 339 passed.
- Published use cases: 322 passed across all 18 workflows in 4 minutes 28 seconds.
- Personal-scale correctness and timing: 100,000 transactions, 30 runs, p50 1,549.0 ms and p95 1,686.9 ms; threshold advisory.
- Direct published-binary smoke: `system.version` returned contract compatibility 1.0 and `system.schema.list` returned exactly 74 operations.
- Build, format, NativeAOT, Lex integrity, lint, coverage, plan audit, cycle detection, and diff checks: passed.

## Finding resolved during review

| Finding | Classification | Resolution | Commit |
|---|---|---|---|
| Nineteen selected FR proof rows and four NFR proof sources exercised the required behavior but did not contain a durable graph test-case reference in their test source | Mechanical traceability gap | Added one comment-only `TC-LEDGER-*` trace marker to each affected test class; all 25 selected FR refs and all nine selected NFR refs are now present under `tests/` | `ee4da8d` |

No behavior or production-code defect was found. The fresh full gate passed after the trace-only change.

## Refuted or informational findings

- `lex endpoint suggest` proposed three HTTP-shaped endpoints. Ledger deliberately exposes typed CLI operations and has no HTTP endpoint model, so these heuristic suggestions do not apply.
- Provider names found in source are forbidden-vocabulary canaries used to reject transport coupling; they are not schema or integration dependencies.
- Broad catches at the executable boundary intentionally collapse unexpected failures into the privacy-safe process envelope. Focused process and security tests verify that sensitive input is not echoed.
- The External Orchestrator dependency remains `assumed` because Hermes is a separate consumer. Validating Hermes against the published contract is downstream integration evidence, not authority for adding Hermes, AgentMail, or WhatsApp behavior to Tally.
- Formal `lex review run prepare` could not establish a review run because `DD-LEDGER-APPLICATION-ARCHITECTURE` has no audit-history baseline. This review therefore records the direct machine gates and does not claim a formal review-run receipt.

## Deployment evidence and assumptions

The current VPS exposes `/home/ubuntu/tally/main` through `/dev/vda1` as `ext4`. `lsblk` shows no guest-visible dm-crypt or LUKS layer. That observation cannot prove or disprove provider-side disk encryption, so it is insufficient for the host-protection contract. The required resolution is provider-backed encryption evidence for the selected volume or a dedicated encrypted volume, followed by:

```bash
TALLY_DATA_ROOT=/path/on/encrypted-volume/tally \
TALLY_HOST_PROTECTION_EVIDENCE_FILE=/path/to/host-protection.json \
TALLY_REQUIRE_HOST_PROTECTION_EVIDENCE=1 \
bash scripts/verify-ledger-security.sh
```

The evidence file must be owner-matched, mode `0600`, schema version 1, declare `host-managed-encrypted-volume`, and name the canonical configured data root.

## Compounding and traceability

- No pattern, ADR, requirement, design, or plan correction was needed from this review. The traceability omission was a one-time mechanical proof-source annotation, not a recurring architecture rule.
- The proof harvest added 34 immutable verification records: 25 passed FRs, eight passed NFRs, and one failed NFR.
- The reviewed range contains 164 commits. Forty-one documentation, graph-schema, or bead-state commits lack a `Refs:` footer, including `c4b4646`, `a49820d`, `0ea76a4`, and `4618096`. None of the untraced commits touches `src/`, `tests/`, or `scripts/`; implementation commits are traced. This is a historical metadata warning, not a code-quality failure.

## Actionable result

- CLI implementation and code quality: PASS; synthetic or anonymized field testing may start.
- Sensitive financial-data deployment: blocked only by `bd-64c`.
- Plan archival: wait for a current passed record for `NFR-LEDGER-LOCAL-DATA-PROTECTION`.
- No other must-fix review item remains.
