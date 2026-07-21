# OQ-LEDGER-15 spend-pool cardinality evidence

Status: blocked by incomplete owner evidence on 2026-07-21.

## Evidence available

- Three owner-supplied statement documents are present, readable, private, ignored, and untracked.
- No owner-confirmed mapping from representative transactions to company-paid discretionary, personal after-tax, or unassigned spend is present.
- No statement filename, identifier, description, row, amount, balance, or pool mapping was copied into this report.

## Evidence gap

Statements do not encode spend-pool intent and cannot prove whether one active pool or explicit unassigned state conserves exact totals for corrections, refunds, reversals, transfers, voids, supersessions, and mixed purchases. No owner-confirmed candidate mixed purchase is available to test whether simultaneous split pools are required.

The owner has confirmed one Spend Category per transaction and noted that BUDGET may reference categories. That does not resolve Spend Pool cardinality: category describes what the spending was for, while pool describes which funding context owns it.

## Gate result

OQ-LEDGER-15 remains open. One-active-pool cardinality is not approved, and pool catalogue, assignment, and pool-aware actuals implementation must not proceed from account, instrument, cardholder, category, description, or statement wording.

## Evidence needed to resume

Provide privacy-safe owner decisions for company-paid discretionary, personal after-tax, unassigned, correction, refund, reversal, transfer, void, supersession, and at least one mixed-purchase case. Confirm whether each case has exactly one active pool or explicit unassigned state, or identify a case that requires split-pool design.
