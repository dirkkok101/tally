# Layout A source-description absence

## Symptom

The authorized private-fixture test reports that no Layout A statement is structurally qualified, although the supplied statements are the fixed production inputs INGEST must accept.

## Reproduction

Run `LayoutAStatementAdapterTests` with `TALLY_INGEST_PRIVATE_FIXTURE_MANIFEST` pointing to the owner-only ignored manifest below `docs/statements`. The private-fixture selection test fails before producing an extracted statement or invoking Ledger.

## Isolation

- Private fixture containment and PDF extraction succeed; the failure is in Layout A row qualification.
- The row anchors and reconciliation controls are deterministic.
- Some otherwise complete rows contain no description glyph candidate in their vertical ownership band.
- The adapter currently treats both zero and multiple candidates as the same `no_match` outcome.
- Ledger's released `RecordTransactionInput.OriginalDescription` contract requires a non-empty control-free value and round-trips that value exactly.

## Five Whys

1. Layout A does not qualify because row extraction is marked incomplete.
2. Row extraction is marked incomplete because a row without an owned description line is rejected.
3. Source absence is rejected because the governing requirement grouped missing descriptions with ambiguous descriptions.
4. The prior resolution assumed the owner could supply a different export with extractable descriptions.
5. That assumption was false: the supplied statements are the only production formats, so the graph described an unavailable product path.

## Root cause and routing

This is a design defect, not a fixture-path or PdfPig defect. The prior contract conflated a provable absence of source description with ambiguous ownership. `DD-INGEST-SOURCE-DESCRIPTION-ABSENCE` now defines a typed source-absence state and a fixed truthful Ledger description marker while retaining fail-closed behavior for multiple candidates, preserving only source glyphs as original evidence, and forbidding OCR, neighboring-text borrowing, out-of-band facts, and blank Ledger values.

Implementation resumes only after the corrected requirement, data models, test cases, and plan task pass documentation review and bead reconciliation.
