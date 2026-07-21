# OQ-LEDGER-16 cash-withdrawal policy evidence

Status: resolved by owner decision on 2026-07-21.

## Settled context

- Movements between owned bank accounts are transfers and contribute zero External Spend and Budget Actual.
- Separately recorded fees remain spend.
- Linked refunds or reversals offset the original spend.
- No raw statement or email content is needed for this choice.

## Owner-approved v1 rule

- A cash withdrawal is immediate External Spend and Budget Actual in its Effective Date period.
- It is not an owned-account transfer because v1 has no tracked-cash account.
- A separately recorded withdrawal fee remains ordinary spend.
- A linked reversal or refund offsets the withdrawal under the normal relationship rules.
- Later use of the same withdrawn cash is not recorded again as additional spend, preventing double counting.

This intentionally chooses the smaller v1 model. If the owner later needs a cash sub-ledger with individually recorded cash purchases, that requires an explicit design revision and migration rather than reinterpreting historical withdrawals.

## Gate result

OQ-LEDGER-16 is resolved. Cash-withdrawal-dependent account, relationship, and actuals work may implement immediate-spend semantics without introducing a tracked-cash account.
