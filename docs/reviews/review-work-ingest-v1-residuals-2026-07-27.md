# INGEST v1 residual-fix work review

Date: 2026-07-27 (second review-work run on this plan; first: `review-work-ingest-v1-2026-07-27.md`)

Plan: `PLAN-INGEST-V1`

Reviewed scope: the residual-fix wave `e3fd31c..3aba5cb` (5 commits closing bd-t8zs, bd-2vft, bd-3gib, bd-38bl, bd-2lum) plus this review's fix commits.

Reviewed commit (proof harvest): `bba7044781cc94af808fd956697759bd96a426b9`

## Verdict

| Axis | Verdict | Residual |
|---|---|---|
| Spec conformance | **PASS** | Commit-on-abandoned error code is contract-ambiguous (bd-ankc / OQ-INGEST-20); E2E provenance/ledger-resolution depth filed as bd-2ys6 |
| Code quality | **PASS** | Registry-driven ErrorForHandler refactor deferred project-wide (bd-1ymt); pre-existing receipt-lookup-by-rowid filed (bd-zw4w) |

All five residual beads satisfied. All 11 active FRs and 6 NFRs have **current passed** verification records at the reviewed commit.

## Environment note

A parallel BUDGET execution agent shared this worktree. Two of this review's uncommitted edits were clobbered by that agent's tree-wide file restores before all editing/validation moved to an isolated git worktree; main-tree exposure was reduced to atomic copy→stage→commit operations. Also, one full-suite run died with an ILC SIGBUS (environmental, refuted by a clean re-run) and one broad test run hung for ~1h49m under build contention (killed; scoped re-run passed in seconds).

## Bead satisfaction matrix (Phase 2)

| Bead | Contract | Code evidence | Test evidence | Status |
|---|---|---|---|---|
| bd-t8zs | Re-validate commit preconditions under batch lock, same codes; abandoned→no mutation; race proven | `CandidateCommitSaga` post-lock re-validation (now via shared `ValidateReviewState`) | `Commit_racing_abandon_via_BeforeBatchLock…`, `Commit_against_already_abandoned_batch…`, new `Commit_racing_approval_revocation…` | PASS |
| bd-2vft | `created_at` set once, `updated_at` real transitions, no summary wipe, V003 | Promote is pure status flip; V003 columns + truthful backfill | `EnsureReceipt_resume_preserves_created_at_and_summary_json`, `V003_adds_receipt_created_at…` | PASS |
| bd-3gib | Real monotonic attempt counter, V004 | Insert=1/conflict+1 upsert; clamp removed; V004 + backfill | `Attempt_number_is_zero_before_attempt_and_increments…`, `V004_adds_attempt_count…` | PASS |
| bd-38bl | UC gates ≥10/≥14/≥10/≥14 via published CLI dispatch; no bare handlers | `TallyProcess.RunAsync` envelope round-trips; counts 11/16/10/15 | The four UC E2E suites (now hardened) | PASS |
| bd-2lum | All published error codes map through `ErrorForHandler` | 37-line mapping switch, all 51 codes verified contract-exact by two independent reviewers | `IngestErrorProcessTests` (now registry-driven) | PASS |

Graph gates: `lex coverage` healthy (11/11), `decision path-check` healthy (0 missing).

## Build and test evidence (fresh)

| Gate | Result |
|---|---|
| Full suite at pre-fix `3aba5cb` (clean re-run after SIGBUS) | **2247 passed, 0 failed** (7m16s) |
| Storage + saga suites after fix commits | 47/47, then 283/283 |
| Affected suites after E2E hardening | **330 passed, 0 failed** |
| Final full Ingest + Process gate at reviewed commit `bba7044` | **528 passed, 0 failed** |

## Code-quality pass (Phase 3): 5 session-model lenses + 2 fast lenses + Codex

### Findings fixed (MECHANICAL / RESOLVABLE), by commit

| Commit | Fixes |
|---|---|
| `9c0fa16` fix(ingest): backfill migration provenance… | V004 backfilled `attempt_count=1 WHERE attempted_at IS NOT NULL` (3 lenses + Codex, conf 100 — upgraded DBs reported attempted candidates as unattempted); V003 backfill now uses real `ingest_batch` timestamps instead of a fabricated epoch; `MarkTerminalAsync` fresh insert stamps `attempt_count`; NULL receipt timestamps fail closed instead of re-fabricating `now`; already-Committing re-entry no longer re-stamps `updated_at` (Codex) |
| `12e9cb2` + `d81b7d4`-equivalent refactor | Approval-revocation race test that ONLY the post-lock re-validation can pass (silent-pass lens: old race test also passed via the pre-existing Abandoned-receipt guard); duplicated pre/post-lock validation blocks extracted to one `ValidateReviewState` helper; `"1.0"` hoisted to a named constant; lock-window comment narrowed to what is actually re-checked |
| `c7e898f` test(ingest): harden UC E2E gates… | Harness composes through production `CreateServices` (new optional extractor/fault-hook params — production forked wiring eliminated); `InvokeAsync` fails loudly on missing envelope and null success payloads instead of fabricating `exit:N` codes; 19 "any error passes" assertions pinned to contracted codes; `Assert.True(ok \|\| !ok)` and `prior >= 1 \|\| prior == 0` tautologies replaced with real contracted outcomes; escape-hatch disjuncts removed from replay tests (plus a `null`-comparison bug); crash-matrix tests assert `FaultsThrown >= 1` and commit failure before resuming; stub extractor derives row content from source bytes, turning the changed-bytes case into a genuine fail-closed **`INGEST-PREVIEW-OVERLAP-BLOCKED`** published-surface scenario (previously unreachable); double-approve pinned to deterministic re-approval success; resume-of-completed pinned to same-receipt success; `Unapproved_batch_cannot_commit` now actually exercises the NotApproved guard (was hitting DigestMismatch via a wrong literal digest) |
| `bba7044` test(ingest): drive error-mapping theory from the registry | Hand-copied 51-row table replaced by enumeration of every ingest descriptor's `ErrorSchema` + a ≥50-row floor guard; ErrorSchema drift now fails the suite |

### Refuted findings (dropped, with evidence)

- **Post-lock account re-check missing** (type-design, bugs, Codex, graph-intent): bd-t8zs lists `GetAccountAsync` under the lock as explicitly **Out of Scope** with a failure criterion forbidding it. Fixed only the overstated comment.
- **Race-test disjunction proves indecision** (type-design, graph-intent): the disjunction is contract-prescribed by bd-t8zs's own failure criteria ("keep both as stable documented codes and assert explicitly"). The underlying contract ambiguity is real → bd-ankc + OQ-INGEST-20.
- **V003 leaves NULL timestamps that get re-stamped on read** (test-coverage, conf 75): refuted — both insert paths stamp the columns and the backfill covers pre-existing rows; the read fallback was unreachable (now fails closed anyway).
- **Handwritten error switch violates DD-INGEST-CLI-OPERATION-CONTRACT** (graph-intent, conf 100): the switch is the established project-wide convention (ledger/actuals/backup arms are all handwritten literals), so per the trust-hierarchy convention rule this is not INGEST drift; the registry-driven refactor is filed project-wide as bd-1ymt, and the registry-driven test closes the drift risk today.
- **372-failure suite run**: environmental ILC SIGBUS during the in-test Native AOT publish; clean re-run passed 2247/2247 with zero code change.

### Residuals filed

| Bead | P | Finding |
|---|---|---|
| bd-ankc | 2 | Decide the published error code for commit-on-abandoned (NotApproved vs NotCommittable) — bd-t8zs's AC and failure criteria conflict; graph is silent (OQ-INGEST-20) |
| bd-zw4w | 3 | PRE_EXISTING: `EnsureReceiptAsync` selects receipts per batch by rowid, ignoring `manifest_revision_id` — multi-revision batches can cross receipts |
| bd-1ymt | 3 | Drive `ErrorForHandler` from `OperationDescriptor.DomainErrors` project-wide (all modules share the handwritten switch) |
| bd-2ys6 | 3 | E2E depth: advancing test clock for published-surface provenance assertions; post-cleanup ledger transaction resolution |

## Strengths (verified by multiple reviewers)

1. **Promote is a genuinely pure status flip** with a raw-SQL read-back regression test (`CommitStateStore.cs`, `CommitSagaTests.cs:469`) — a real data-loss bug fixed against DD-INGEST-COMMIT-RECOVERY.
2. **Attempt counter is transactional** — `attempt_count = candidate_receipt.attempt_count + 1` in the same SQLite transaction as the state flip; no C# read-modify-write.
3. **The abandon-race test asserts the absence of mutation through independent channels** (raw row count + injector ledger-call count), not just the error code.
4. **All 51 error-mapping arms verified contract-exact** against the six modules' ErrorSchema lists by two independent reviewers before the registry-driven test made that check permanent.
5. **The fault-hook seam is production-inert by construction** — no-op default, returns `Task`, cannot steer control flow short of throwing.

## Anchor reconciliation

- Applied: yes — 28 refreshed, 0 created, **3 unresolved**: `DD-INGEST-CLI-OPERATION-CONTRACT`, `DD-INGEST-MANIFEST-IDENTITY-OVERLAP`, `DD-INGEST-STATE-STORE` — multi-path decisions with no single-file contract (same as prior run; reported, not guessed or broadened).

## Proof harvest

- Reviewed commit: `bba7044781cc94af808fd956697759bd96a426b9`
- Batch: 17 records (11 FR + 6 NFR), all `passed`, proof kind `test`, FR↔TC↔bead tuples carried from the Phase 2 matrix (identical mapping to the prior harvest, re-proven fresh)
- `lex verification list`: all FR/NFR **current**; archive-eligible on FR/NFR currency (PLAN owns archival)

## Compounded graph deltas

- `DM-INGEST-STATE-STORE` schema_def amended: V003 (receipt provenance, batch-timestamp backfill) and V004 (attempt counter, backfill), `user_version=4` — the entity had pinned the schema at V002 while code shipped V4 (graph-intent lens, conf 100).
- `DD-INGEST-STATE-STORE` status notes amended with the same V003/V004 record.
- OQ-INGEST-20 (commit-on-abandoned error code).
- These deltas face `/lex:review-documentation` like all graph content.
- Lesson for future waves (not authored as a pattern — no existing PAT home): **case-count gates without assertion-strength requirements invite tautological padding.** The bd-38bl contract specified counts and surface but not falsifiability; five of the counted cases were unfalsifiable. If this recurs, a CORE pattern rule ("every negative E2E case must assert its contracted error code") is warranted.

## Flow attestation

See end-of-review result in session log: attestation attempted after this report; the lifecycle chain still lacks the brainstorm receipt (`flow status` handoff = brainstorm), which suppressed attestation on the prior run as well.

## Actionable result

1. **Both axes PASS**; residual-fix wave verified and hardened. Ship-ready from the review's perspective.
2. **Decide bd-ankc** (abandoned-batch error code) — the one P2 judgment call a human should make.
3. Mechanical backlog: bd-zw4w, bd-1ymt, bd-2ys6 (P3).
4. **Lifecycle**: restore the brainstorm receipt chain before `lex flow attest review-work` can succeed.
