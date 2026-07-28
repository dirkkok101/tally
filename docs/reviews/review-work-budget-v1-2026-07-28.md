# BUDGET v1 work review

Date: 2026-07-28

Plan: `PLAN-BUDGET-V1` (24 beads, executed 2026-07-27 by the parallel BUDGET session)

Reviewed commit (proof harvest): `aba0d54559e70e0930d5793582a7c3af8890a4d4`

Terminal receipt: `SR-BUDGET-REVIEW-WORK-400bbc5ff9d6616d` — review_passed, spec **passed**, quality **passed**.

## Verdict

| Axis | Verdict | Residual |
|---|---|---|
| Spec conformance | **PASS** (after owner-approved NFR renegotiation) | Perf optimization ambition tracked as bd-12td; structural residuals bd-2zge, bd-b5fl |
| Code quality | **PASS** (after fix wave) | bd-27ye, bd-nqp9, bd-fxa3, bd-2vne |

## Bead satisfaction (Phase 2): 23/24 SATISFIED, 1 AC_NOT_MET → resolved by renegotiation

Four parallel verifiers checked every bead against the actual implementation. 23 beads fully satisfied with strong evidence (real-SQLite fault-point matrices, property tests, canary scans, published-surface acceptance suites whose case counts all exceed their contracted floors with falsifiable assertions — notably better test discipline than the sibling INGEST wave). The exception:

- **bd-1w97 (performance gate): AC_NOT_MET + FC_VIOLATED.** The 2026-07-27 benchmark measured 5 of 6 operations over their p95 targets (position 4.38s / insights 5.12s vs 3s; draft/activate/get ~1.24–1.29s vs 1s), the gate passed only because `BUDGET_PERF_ADVISORY_P95=1` was the script default, the kill criterion was cleared on that advisory run, and in-period load was reduced to 800 transactions against an explicit "do NOT reduce approved load" failure criterion (with a stale comment claiming 100K). A fresh full-suite gate run during this review reproduced the miss exactly: 648/649 with only the enforcing p95 test failing.
- **Owner decision (Dirk, 2026-07-28): renegotiate.** `NFR-BUDGET-PERSONAL-SCALE-PERFORMANCE` target revised to measured v1 reality (position/insights ≤ 6s p95; draft/activate/get ≤ 2s; list ≤ 1s) with the revision note in the target text; `TASK-BUDGET-GATE-PERFORMANCE` and `TC-BUDGET-PERSONAL-SCALE-PERFORMANCE` aligned; the gate default flipped to **enforcing**; the enforcing benchmark re-run **passed 1/1** (20m34s). The original 3s/1s ambition is tracked as **bd-12td** (root cause identified: per-operation 1000-identity `ledger.category.list` over the process seam ≈ 1.1s/op, plus full snapshot drain for position/insights).

## Build and test evidence (fresh)

| Gate | Result |
|---|---|
| Full Budget suite (incl. enforcing perf, pre-fix) | 648/649 — only the enforcing p95 test failed, confirming the NFR miss |
| Combined regate after fix wave (minus perf) | **644 passed, 0 failed** |
| Enforcing benchmark at renegotiated targets | **passed 1/1** (20m34s) |
| Content review (fresh full run 29d54674) | published clean: 0 MECHANICAL, 0 JUDGMENT |

## Quality pass (Phase 3): 4 session-model lenses + 2 fast lenses + Codex (12 findings)

### Fixed (three commits: `fix(budget)`, `test(budget)`, `docs(budget)`)

- **Fail-closed hardening**: `BudgetMoney.TryParse` no longer throws on overflow; unknown LEDGER error codes map to `BUDGET-INTEGRITY` (fail-closed) instead of retryable `LedgerUnavailable`; integrity post-conditions in both mutation slices surface as exit 8 instead of `host.unexpected`; a vanished prior-active revision is an integrity failure, not a silent null; the replay reconciliation guard now runs on replay (it was disabled exactly where a mismatch means store corruption).
- **Contract truthfulness**: the phantom `revision.list` `nextCursor` removed per DM-BUDGET-OPERATION-CONTRACTS' bounded no-cursor design (the emitted cursor had no input field to receive it — >100-revision history was advertised but unreachable); per-operation `ErrorSchema` lists now declare every reachable code (five undeclared codes on revision.get alone); the budget error-mapping theory is registry-driven with a guard fact (mirroring the INGEST fix — an unmapped code now fails the suite).
- **Ledger drain integrity**: iteration cap, requested-contract-version equality, and per-item period-bounds checks added to `QueryBudgetActualsAsync` (a Ledger regression can no longer smuggle an out-of-period item past matching totals).
- **Determinism/hygiene**: identity ULIDs take the injected TimeProvider instant; ledger-failure and missing-category mapping deduplicated into the shared mapper; the private `TryNormalizeReason` copy deleted in favor of the domain method.
- **Gate falsifiability**: perf gate enforcing by default; security script canary check now seeds real canaries and gates on `Skipped: 0`; recovery cutpoint checks scoped per theory with the documented floor (22); module gate runs the perf gate (opt-out `BUDGET_MODULE_SKIP_PERF=1`, explicit skip line) and enforces per-suite discovery floors; published-binary self-neutered `Assert.True(true)` cases now emit explicit SKIPPED warnings with script-level skipped==0 guards; the three-way exit accept on the empty-store list pinned to 0; guard inventories assert set equality with the assembly; `AssertOfflineIsolation` asserts something real.

### Filed (structural residuals)

| Bead | P | Finding |
|---|---|---|
| bd-12td | 2 | Restore the 3s/1s perf ambition (catalogue-read dedup + snapshot drain cost) |
| bd-2zge | 2 | Replay idempotency: hoist the replay probe ahead of live revalidation (completed drafts re-fail after category archival/LEDGER outage — Codex crit 9); unify replay response shape (activation replay returns empty evidence) |
| bd-b5fl | 2 | Category-evidence unification: fabricated Unknown evidence on non-not-found failures; second category read in insights (DD forbids); binding fingerprint omits category evidence; DM-specified `categoryContractVersion` missing; zero-entry drafts fabricate provenance |
| bd-27ye | 2 | Storage: validate path safety before opening the DB; extend immutability triggers to lifecycle timestamps/replacement refs |
| bd-nqp9 | 3 | Position: one-snapshot binding for pointer+entries; structured integrity reasons; stop reconstructing period boundaries in activation |
| bd-fxa3 | 3 | Executor: rehydration read inside the committed transaction (post-COMMIT work can fail a committed mutation) |
| bd-2vne | 3 | Acceptance-layer test gaps (boundary-instant activation, zero-entry/archived-category positions, ledger-failure surfacing, concurrent draft, exact-page-boundary) |

### Refuted / adjudicated

- Cross-slice mapping duplication was partially sanctioned by DD-BUDGET-APPLICATION-ARCHITECTURE's vertical-slice philosophy; the dedup landed in the mapper home the DD itself names (pure mappers), not a new abstraction.
- The guard-fact floor of ≥22 declared error codes was corrected to 20: `BudgetErrors.NotFound` is declared in the constants class but attached to no operation — the old hand-copied table included it as an artifact.
- `revision.list` "pagination broken" resolved AGAINST building cursors: DM-BUDGET-OPERATION-CONTRACTS specifies a bounded, cursor-less list; the implementation had invented the cursor.

## Strengths (multi-reviewer consensus)

1. **The one-transaction mutation contract is genuinely honored** — replay lookup, mutation, event append, result hash, and idempotency insert under one `BEGIN IMMEDIATE`, with DB-level enforcement (partial unique one-Active index, status-transition triggers, no-update/no-delete triggers) so code bugs cannot corrupt the plan of record.
2. **The actuals drain is a rigorous "one complete snapshot or nothing"** — cross-page snapshot/generation/version/totals equality, dense-ordinal completeness, distinct transaction IDs, no partial results (now also period-bounded and iteration-capped).
3. **`BudgetPositionCalculator` never defaults past a gap** — checked arithmetic, exhaustive four-bucket assignment reconciled to the cited total, unknown identities throw.
4. **Acceptance suites are falsifiable** — exact exit + domain codes on every failure path, real canary constants scanned across stdout/stderr/argv, prior-or-complete recovery matrices with directed cutpoint expectations.

## Process notes

- Content review required two prepares: the first run froze inputs before I aligned `TASK-BUDGET-GATE-PERFORMANCE`/`TC-BUDGET-PERSONAL-SCALE-PERFORMANCE` with the renegotiated NFR (the lenses would rightly have flagged the 3s-vs-6s contradiction); it was aborted stale and re-prepared. One deterministic decision-gap leaf was written to the aborted run's directory by its agent; its FR/DD primaries were byte-identical across the two runs, so the empty leaf transferred.
- Anchor reconcile: 30 refreshed, 0 created, 7 unresolved multi-path DDs (no single-file contracts — reported, not guessed).
- Proof harvest: 18 records (11 FR + 7 NFR) at `aba0d54`; the perf NFR uses proof kind `benchmark` citing `docs/verification/budget-performance.md` (enforcing 2026-07-28 run).
- Stage receipts were restored for brainstorm/discovery/prd/design (module predates flow receipts); prd/design were re-attested twice as graph alignment landed. OQ-BUDGET-10 resolved per DD-BUDGET-EXACT-POSITION-CALCULATION; OQ-BUDGET-9 marked non-blocking.

## Compounding

- The **advisory-by-default performance gate** is the recurring lesson of this review (a green gate that cannot go red): gates must be strict by default and loosened by explicit opt-in. Applied here; recommend the same audit for any future gate script that takes a leniency env var.
- The **hand-copied error-table drift** recurred from INGEST (bd-2lum) within one day — the registry-driven pattern is now applied in both modules; a CORE pattern rule ("process error-mapping tests enumerate the registry, never hand-copy") is warranted when a PAT home exists.
