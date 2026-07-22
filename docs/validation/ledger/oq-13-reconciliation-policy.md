# OQ-LEDGER-13 reconciliation policy evidence

Status: approved on 2026-07-22 for ReconciliationPolicyV1.

## Evidence and privacy boundary

- Three owner-supplied statement documents are present, readable, private, ignored, and untracked.
- The owner confirmed that an approved statement is authoritative over earlier agent-capture facts for the reconciled current view.
- The owner approved the conservative policy below: automatically match only one unique exact candidate and route every non-exact, ambiguous, conflicting, or already-reconciled case to review.
- This matrix uses symbolic provider-neutral observations. It records owner-confirmed policy outcomes, not inferred private statement facts.
- No statement filename, identifier, description, row, amount, balance, notification text, or transport metadata is retained here.

## ReconciliationPolicyV1 inputs

For statement observation `S` and candidate transaction `C`, automatic compatibility requires every condition below:

1. `S.accountId == C.accountId`, using the stable owned-account identity.
2. `S.currencyCode == C.currencyCode == ZAR`.
3. `S.signedAmountMinor == C.signedAmountMinor`, with exact signed `Int64` minor units and no amount tolerance.
4. `S.transactionDate == C.effectiveDate`, with a tolerance of zero calendar days.
5. `C` is active and recorded-but-unreconciled.
6. `S` has no active confirming link or conflicting reconciliation decision.
7. Exactly one compatible candidate and zero guard candidates survive the projection.

Posting date, Payment Instrument, cardholder, description text or fingerprint, Evidence Kind, provider identifiers, and candidate order are not compatibility fields or tie-breakers in v1.

## Owner-approved decision matrix

| Case | Normalized paired observations | Candidate state | V1 result | Stable reason | Financial effect |
|---|---|---|---|---|---|
| Exact facts | Account, ZAR currency, signed minor amount, and effective date are equal | One active unreconciled candidate; no guard or conflict | Automatic `match_existing` | `exact_unique_candidate` | Link statement evidence to the existing transaction; create no transaction |
| Differing authoritative facts | At least one account, currency, signed amount, or effective date differs | One otherwise plausible candidate | Review required; explicit owner correction remains available | `authoritative_fact_difference` | None until an owner-approved `correct_existing_from_statement` decision appends a replacement |
| Shifted date | Account, currency, and signed amount are equal; effective date differs | One active unreconciled candidate | Review required | `effective_date_mismatch` | None |
| Duplicate exact facts | All compatibility fields are equal | More than one compatible candidate | Record ambiguous for review | `multiple_compatible_candidates` | None |
| Competing guard | One exact candidate exists, but another guard candidate could represent the event | Compatible plus guard candidates | Record ambiguous for review | `guard_candidate_present` | None |
| Conflicting evidence | Compatibility fields may be equal | Evidence or candidate has a conflicting active confirmation or decision | Record exception for review | `conflicting_confirmation` | None |
| Already reconciled | Compatibility fields are equal | Candidate is not recorded-but-unreconciled | Record exception for review | `already_reconciled_candidate` | None |
| Zero candidate | No compatible or guard candidate exists | Empty projection | Review required; explicit statement-only approval remains available | `no_candidate` | None until an approved `create_statement_only` decision creates one transaction |
| Unsupported or stale policy | Any observations | Policy version, fingerprint, or projection is unsupported or stale | Record exception for review | `unsupported_or_stale_policy` | None |

Candidate-order permutations must return the same classification, ordered candidate identities, policy version, match basis, and reason.

## Authority and lifecycle rules

- An automatic exact match records deterministic-policy authority, `ReconciliationPolicyV1`, the four-field exact match basis, `exact_unique_candidate`, actor and trusted time, then appends one confirming evidence link. It never rewrites the existing transaction.
- Replaying the same logical evidence and decision is idempotent and returns the prior logical result. Reusing an identity with different normalized input is a conflict and applies no effect.
- The automatic correction subset is empty in v1. A differing statement fact can be applied only through an explicit owner-approved correction with attributable reason and append-only replacement history.
- Zero-candidate statement-only creation is likewise an explicit approved disposition, not an automatic match. It creates one statement-derived transaction only after approval.
- Multiple compatible or guard candidates, conflicts, stale projections, and already-reconciled candidates never use hidden ordering or provider data to choose a transaction.
- The approved statement is authoritative only through a typed reconciliation decision. Agent capture remains provisional; raw email, MIME, and statement payloads remain outside Tally.

## Gate result

OQ-LEDGER-13 is resolved for the bounded ReconciliationPolicyV1 subset: exact account, ZAR currency, signed minor amount, and effective date; zero tolerances; exactly one compatible active unreconciled candidate; zero guard candidates; and no conflicting state. Every other case is review-required, automatic correction is disabled, and explicit owner-approved statement correction or statement-only creation remains independently available.
