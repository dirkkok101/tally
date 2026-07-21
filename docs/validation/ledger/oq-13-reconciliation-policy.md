# OQ-LEDGER-13 reconciliation policy evidence

Status: blocked by incomplete paired evidence on 2026-07-21.

## Evidence available

- Three owner-supplied statement documents are present, readable, private, ignored, and untracked.
- No paired agent-capture evidence or owner-confirmed match outcomes are present.
- No statement filename, identifier, description, row, amount, balance, notification text, or transport metadata was copied into this report.

## Required matrix coverage

The gate requires paired, privacy-safe cases for exact same-day matches, shifted dates, duplicate amounts, competing candidates, conflicting evidence, already-reconciled facts, and zero-candidate statement rows across every available account and institution.

## Evidence gap

Statement rows alone cannot prove compatibility with an earlier agent capture or establish safe date tolerances, uniqueness rules, conflict guards, and stable match reasons. In particular, there is no evidence from which to distinguish a unique deterministic match from a duplicate-amount or competing-candidate ambiguity.

## Gate result

OQ-LEDGER-13 remains open. No automatic ReconciliationPolicyV1 is approved. Automatic matching and automatic statement-only application remain disabled; unproven cases must remain review-required.

## Evidence needed to resume

Provide redacted paired agent-capture and statement-row cases, together with the owner's confirmed outcome for every required matrix class. The evidence may expose only normalized provider-neutral fields needed to evaluate exact account, currency, signed amount, dates, uniqueness, and conflicts.
