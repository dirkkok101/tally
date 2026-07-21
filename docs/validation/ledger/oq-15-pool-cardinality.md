# OQ-LEDGER-15 spend-pool cardinality evidence

Status: resolved by owner decision on 2026-07-21.

## Evidence available

- Three owner-supplied statement documents are present, readable, private, ignored, and untracked.
- No owner-confirmed mapping from representative transactions to company-paid discretionary, personal after-tax, or unassigned spend is present.
- No statement filename, identifier, description, row, amount, balance, or pool mapping was copied into this report.

## Owner-approved v1 rule

- Each active spend transaction has one active Spend Pool assignment or an explicit unassigned state.
- Pool is independent from account, payment instrument, cardholder, and Spend Category and is never inferred from those dimensions or provider wording.
- Corrections append history and replace the active assignment.
- Refunds and reversals follow the original transaction's current pool; transfer principal contributes zero spend.
- A mixed purchase remains unassigned until the owner assigns the whole transaction to one pool. Split-pool allocation is out of scope for v1.

This rule conserves exact totals because every active spend amount appears once in exactly one pool or in the explicit unassigned bucket. It does not silently force a mixed purchase into a pool.

## Privacy-safe validation matrix

| Case | Active pool state | Exact actuals consequence |
|---|---|---|
| Company-paid discretionary spend | One company-paid pool | Full active spend appears once in that pool and its category cell. |
| Personal after-tax spend | One personal-after-tax pool | Full active spend appears once in that pool and its category cell. |
| Pool intent not yet known | Explicit unassigned | Full active spend appears once in the unassigned bucket. |
| Pool correction | One replacement assignment | Prior assignment remains historical; only the replacement contributes to current actuals. |
| Linked refund or reversal | Follows the original transaction's current pool | The offset is applied once in the refund Effective Date period. |
| Owned-account transfer | Pool assignment does not create spend | Transfer principal contributes zero External Spend and Budget Actual. |
| Void | No active spend contribution | History remains queryable and current actuals exclude the voided transaction. |
| Supersession or statement correction | One explicit replacement pool state or unassigned state | The superseded fact contributes zero and the active replacement contributes once. |
| Mixed purchase | Explicit unassigned until one whole-transaction pool is chosen | No split is inferred and the full amount remains conserved in unassigned actuals. |

## Gate result

OQ-LEDGER-15 is resolved and A15 is validated. Pool catalogue, assignment, and pool-aware actuals may implement the one-active-pool-or-unassigned model. Any future requirement for simultaneous split pools must return to PRD and design rather than extending the v1 schema implicitly.

## Privacy outcome

No statement filename, identifier, description, row, amount, balance, or private pool mapping is retained in this report.
