# OQ-LEDGER-6 category policy evidence

Status: resolved by direct owner policy on 2026-07-21; the flat-catalogue assumption is invalidated.

## Evidence available

- Three owner-supplied statement documents are present, readable, private, ignored, and untracked.
- The planned coverage target is all three accounts across both institutions.
- No statement filename, identifier, description, row, amount, balance, or category mapping was copied into this report.

## Owner decision

- Spend Categories require a hierarchy.
- A transaction links to at most one active category; split category allocation is not required for the first release.
- Whether a Budget Plan references a category is a BUDGET design decision and does not change LEDGER assignment cardinality.

The revised LEDGER design uses an acyclic parent hierarchy with append-only parent history, permits assignment to any active node, and defines exact direct and subtree roll-ups. Archive, reactivation, and move behavior are technical integrity rules rather than facts inferred from statements.

## Gate result

OQ-LEDGER-6 is resolved. A5 is validated; A10 is invalidated. Category work must not proceed under the old flat model and must be replanned against the hierarchical contracts.

## Evidence needed to resume

No further category-cardinality evidence is required before replanning. Later usability feedback may refine names or example trees without reopening the one-category or hierarchy decision.
