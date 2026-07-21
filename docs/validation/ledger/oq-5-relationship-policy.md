# OQ-LEDGER-5 relationship policy evidence

Status: resolved by direct owner policy on 2026-07-21; cash-withdrawal treatment moved to OQ-LEDGER-16.

## Evidence available

- Three owner-supplied statement documents are present, readable, private, ignored, and untracked.
- The planned coverage target is all three accounts across both institutions.
- No statement filename, identifier, description, row, amount, balance, or exact total was copied into this report.

## Owner decision

- A movement between two owned bank accounts is a transfer, remains visible on both accounts, and contributes zero External Spend and Budget Actual.
- A separately recorded bank fee is spend rather than transfer principal.
- A linked refund or reversal offsets the original spend under the normal banking relationship semantics.
- The phrase "cash withdrawal" does not by itself determine whether cash is immediately spent or moved to another owned account. That choice is isolated in OQ-LEDGER-16.

## Gate result

OQ-LEDGER-5 is resolved. Transfer implementation may proceed after the revised design and plan are reviewed. Refund/reversal and fee semantics remain explicit financial relationship rules. Cash-withdrawal actuals remain blocked only by OQ-LEDGER-16.

## Evidence needed to resume

Resolve OQ-LEDGER-16 by choosing whether cash withdrawal is immediate External Spend or a transfer to a tracked cash account. No raw row or description is required.
