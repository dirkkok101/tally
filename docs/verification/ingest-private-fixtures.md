# INGEST private-fixture evidence gate (bd-3k6)

TASK-INGEST-GATE-EVIDENCE-PRIVATE-FIXTURES deliberately leaves **zero tracked
changes**: the private bank statements never enter the repository. This note is
the durable, payload-free record of the gate's outcome so the bead has commit
traceability.

Outcome (2026-07-25, owner worktree):

- Schema v1; 3 owner-local fixtures across 2 layout variants
- Record counts 16 / 76 / 13; all 3 statements reconciled exactly
  (opening + movements = closing)
- Owner-only strict paths enforced; fixture manifest gitignored
- 0 tracked private files; no private values or locators recorded anywhere

Verification: rerunning the private-fixture gate requires the owner-local
fixture set and is only possible on the owner's machine; the module gate
(bd-1vr) consumed this evidence via metadata-only fingerprints.

Re-attested with machine-parseable commit trailer on 2026-07-27.

## Archive expansion (2026-07-29, owner worktree)

- Inventory + expected-results manifest expanded to **27 unique owner PDFs**
  (13 FNB / Layout A, 7 Discovery Purple card + 7 transaction-account / Layout B)
- Adapter fixes: Layout A period resolution no longer fails when extra full dates
  appear outside the period line; incomplete yearless-date lines are skipped;
  Layout B allows trailer pages without Date/Details/Amount headers
- Loader requires `AuthorizedFixtureCount=27`, two product variant ids
  (`pdf-text-layout-a-v1`, `pdf-text-layout-b-v1`), inventory digest parity
- Enabled private suite proved **27/27** executed: per-file SHA-256, exclusive
  adapter selection, periods from parsed evidence, stable source identities,
  ordered records, reconciliation controls
- Pipeline on disposable roots: **previewed=27**, **committed=26**,
  **nonCommittable=1** (stopped before approval), **exactReplays=27**,
  **duplicateEffectReplays=26**; live ledger path untouched
- Private values, paths, and rows remain untracked and unlogged
