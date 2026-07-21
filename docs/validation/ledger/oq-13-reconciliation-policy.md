# OQ-LEDGER-13 reconciliation policy evidence

Status: blocked by incomplete paired evidence on 2026-07-21.

## Evidence available

- Three owner-supplied statement documents are present, readable, private, ignored, and untracked.
- The owner confirmed that an approved statement is authoritative over earlier agent-capture facts for the reconciled current view.
- No paired agent-capture evidence or owner-confirmed match outcomes are present.
- No statement filename, identifier, description, row, amount, balance, notification text, or transport metadata was copied into this report.

## Required matrix coverage

The gate requires paired, privacy-safe cases for exact same-day matches, shifted dates, duplicate amounts, competing candidates, conflicting evidence, already-reconciled facts, and zero-candidate statement rows across every available account and institution.

## Evidence gap

Source precedence is settled, but statement rows alone cannot prove that a particular earlier agent capture represents the same economic event. Paired evidence is still required for safe date tolerances, uniqueness rules, conflict guards, stable match reasons, and the bounded cases where an automatic authoritative correction may replace differing provisional facts.

## Gate result

OQ-LEDGER-13 remains open only for automatic identity and compatibility policy. No automatic ReconciliationPolicyV1 is approved. Automatic matching, authoritative correction, and statement-only application remain disabled; an explicit owner-approved correction is attributable and unproven cases remain review-required.

## Evidence needed to resume

Provide redacted paired agent-capture and statement-row cases, together with the owner's confirmed outcome for every required matrix class. The evidence may expose only normalized provider-neutral fields needed to evaluate exact account, currency, signed amount, dates, uniqueness, and conflicts.
