# OQ-LEDGER-16 cash-withdrawal policy evidence

Status: awaiting one owner policy choice on 2026-07-21.

## Settled context

- Movements between owned bank accounts are transfers and contribute zero External Spend and Budget Actual.
- Separately recorded fees remain spend.
- Linked refunds or reversals offset the original spend.
- No raw statement or email content is needed for this choice.

## Open choice

A cash withdrawal can be modeled in either of two normal banking ways:

1. Immediate spend: the withdrawal contributes External Spend when cash leaves the bank account.
2. Transfer to tracked cash: the withdrawal contributes zero spend, and later recorded cash purchases contribute spend.

Mixing these policies would double-count or omit spend. OQ-LEDGER-16 therefore remains open and only cash-withdrawal-dependent actuals work stays gated.
