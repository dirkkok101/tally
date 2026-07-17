# Lex Cross-Repository Transfer Feedback

This document records defects and enhancement opportunities observed while transferring reusable architecture knowledge from the NxGN Actions Lex graph into a new Tally graph and then using that graph to frame Tally's first module.

## Environment

| Item | Value |
|------|-------|
| Date observed | 2026-07-17 |
| Lex version | 0.5.3 |
| Source repository | `/home/ubuntu/nxgn.actions/main` |
| Target repository | `/home/ubuntu/tally/main` |
| Source module | `CORE` (`Platform Core`) |
| Intended transfer | 29 ADRs, 33 patterns, 16 architecture documents |

The source material was exported with Lex 0.5.3 and imported with the same binary. The problems below are therefore same-version round-trip defects, not compatibility issues between different Lex releases.

## Summary

| ID | Type | Severity | Summary |
|----|------|----------|---------|
| LEX-XFER-001 | Bug | Medium | `lex init --name` does not create the named project |
| LEX-XFER-002 | Bug | High | ADR export/import prefixes the ref-code into the title and slug |
| LEX-XFER-003 | Bug | Medium | Pattern directory import creates a pattern from generated `README.md` |
| LEX-XFER-004 | Bug | High | Pattern export/import duplicates structure and loses classification metadata |
| LEX-XFER-005 | Bug | High | Architecture export/import duplicates generated content and loses category metadata |
| LEX-XFER-006 | Bug | Medium | Writes recommend sync conflict resolution when graph and DB are already in sync |
| LEX-SKILL-001 | Bug | High | The brainstorm skill emits a constraint type rejected by Lex 0.5.3 |
| LEX-XFER-E001 | Enhancement | High value | Add a canonical cross-repository graph transfer command |
| LEX-XFER-E002 | Enhancement | High value | Add dry-run and entity-diff support to every importer |
| LEX-XFER-E003 | Enhancement | High value | Support dependency-closure transfer for links and diagrams |
| LEX-XFER-E004 | Enhancement | High value | Enforce same-version export/import round-trip invariants |
| LEX-XFER-E005 | Enhancement | Medium value | Report structural deltas after import |

## Reproduction Baseline

The transfer used the supported export/import surface:

```sh
lex export --module CORE --type adr --output /tmp/lex-transfer/adr --layout flat
lex export --module CORE --type pattern --output /tmp/lex-transfer/pattern --layout flat
lex export --module CORE --type arch --output /tmp/lex-transfer/arch --layout flat

lex import --module CORE --from /tmp/lex-transfer/adr --type adr --json
lex import --module CORE --from /tmp/lex-transfer/pattern --type pattern --json
lex import --module CORE --from /tmp/lex-transfer/arch --type arch --json
```

The target graph was then re-exported and compared recursively with the source export. Entity counts, representative `show --json` payloads, and Markdown diffs were also compared.

## Confirmed Bugs

### LEX-XFER-001: `lex init --name` does not create the named project

**Command:**

```sh
lex init --providers codex,claude-code --name tally
```

**Observed:**

- Lex initialized the v21 database.
- The command ended with `No project detected. Run lex init from your project root.`
- `lex project list --json` returned `[]`.
- The command was already running from the Git repository root.

**Expected:**

`--name tally` should create or idempotently resolve the `tally` project, consistent with the command help and migration guide. If `--name` is not intended to create project metadata, the option and documentation should say so and the command should return a clear incomplete-initialization result.

**Workaround:**

```sh
lex project create --name tally
```

### LEX-XFER-002: ADR export/import prefixes the ref-code into the title and slug

**Source entity:**

```json
{
  "ref_code": "ADR-CORE-0001",
  "slug": "seed-first-party-application-baseline",
  "title": "Seed First-Party Application Baseline"
}
```

**Imported entity:**

```json
{
  "ref_code": "ADR-CORE-0001",
  "slug": "adr-core-0001-seed-first-party-application-baseline",
  "title": "ADR-CORE-0001: Seed First-Party Application Baseline"
}
```

The next export then produced this duplicated heading:

```markdown
# ADR-CORE-0001: ADR-CORE-0001: Seed First-Party Application Baseline
```

**Expected:**

The ADR importer should recognize the exporter-owned `ADR-<MODULE>-<NUMBER>:` heading prefix as metadata, not as part of the title or slug.

### LEX-XFER-003: Pattern directory import creates a pattern from generated `README.md`

The pattern exporter wrote 33 pattern documents plus a generated `README.md` index. Importing the exported directory reported 34 created patterns and created:

```text
PAT-CORE-README  Pattern Index
```

The generated index was not a source graph entity and had to be deleted explicitly.

**Expected:**

Directory import should ignore exporter-owned indexes, manifests, and other projections. At minimum, it should identify the generated-file banner and skip the file with an explicit diagnostic.

### LEX-XFER-004: Pattern export/import duplicates structure and loses metadata

Representative entity: `PAT-CORE-WEB-VALIDATION`.

| Field | Source | Imported |
|-------|--------|----------|
| Category | `validation` | `pattern` |
| Zone | `web` | `null` |
| Sections | 14 | 50 |
| Examples | 23 | 46 |

Imported sections included generated headings and examples that already existed as first-class children. Example names were duplicated with suffixes such as `Example 1 Example 1`.

The complete source set contains 370 sections, 497 examples, and 299 rules across 33 patterns, so manual repair is impractical and error-prone.

**Expected:**

A pattern exported by Lex should import back to the same top-level fields and the same ordered child structure. Generated presentation headings, indexes, and rendered examples must not become additional graph entities.

### LEX-XFER-005: Architecture export/import duplicates generated content and loses metadata

Representative entity: `ARCH-CORE-ASPIRE-APPHOST`.

| Field | Source | Imported |
|-------|--------|----------|
| Category | `reference` | `arch` |
| Content length | 9,702 | 9,855 |

The imported content contained a second generated-file banner and a second `Ref Code` block. Other architecture documents also gained duplicate `See Also` headings and repeated link lists.

**Expected:**

The architecture importer should strip exporter-owned wrappers and preserve the source category. Repeated export/import cycles must not grow document content.

### LEX-XFER-006: Writes recommend conflict resolution while graph and DB are in sync

Graph writes repeatedly emitted:

```text
Warning: Graph files have uncommitted changes; run 'lex sync resolve --files'
to adopt them or restore the committed files.
```

At the same time, `lex sync status --json` reported:

```json
{
  "drift_state": "in-sync",
  "db_ahead": [],
  "files_ahead": []
}
```

**Expected:**

Git working-tree status and graph/database drift should be reported separately. An uncommitted but internally synchronized new graph should not recommend an authority-resolution command intended for drift or conflict handling.

### LEX-SKILL-001: The brainstorm skill emits an invalid constraint type

The `lex:brainstorm` skill instructs agents to record the complexity budget with this command shape and repeats it in its worked example:

```sh
lex constraint create --module LEDGER --title "Single-process complexity budget" \
  --type complexity --description "One executable, one embedded store, and no daemon"
```

Lex 0.5.3 rejects the command:

```text
Error [LEX-006]: Invalid constraint type 'complexity'. Must be one of: technical, business, regulatory, timeline.
```

The machine-readable command schema confirms that only `technical`, `business`, `regulatory`, and `timeline` are accepted. This breaks the documented brainstorm workflow at the graph-write phase and is especially costly because a batch of otherwise valid constraints can fail on the shared invalid type.

**Expected:**

The distributed skill and the CLI schema must agree. Either add `complexity` to the constraint taxonomy or update the skill to use an accepted type and represent the complexity budget through the title and description. Contract-test all skill command examples against the packaged CLI before release.

**Workaround:**

Use `--type technical` and retain the complexity semantics in the constraint title and description.

## Enhancement Proposals

### LEX-XFER-E001: Canonical cross-repository graph transfer

Add a structured transfer surface that does not use generated Markdown as an interchange format. A possible interface is:

```sh
lex bundle export --module CORE --types adr,pattern,arch --output core.lexbundle
lex bundle import --project tally --module CORE --from core.lexbundle --preserve-refs
```

The bundle should preserve:

- Stable ref-codes and slugs
- All entity fields
- Ordered pattern sections, examples, and rules
- Architecture content and metadata
- ADR status and MADR fields
- Explicit links between transferred entities
- Provenance identifying the source project and graph revision

### LEX-XFER-E002: Dry-run and entity diff for every importer

`--dry-run` currently documents support only for plan import. ADR, pattern, architecture, PRD, and design imports should support a non-mutating preview that reports:

- Entities to create, update, skip, or reject
- Field-level changes
- Child-count changes
- Ref-code and slug transformations
- Unresolved and external references
- Generated files that will be ignored

### LEX-XFER-E003: Dependency-closure transfer

The source `CORE` module has 271 explicit links and 27 diagrams in addition to its ADRs, patterns, and architecture documents. A selective transfer should be able to include the dependency closure needed to avoid dangling references:

```sh
lex bundle export --module CORE --types adr,pattern,arch --include-dependencies
```

The preview should classify dependencies as included, external, unresolved, or deliberately excluded. This is particularly important for architecture documents that refer to first-class `DIAG-*` entities.

### LEX-XFER-E004: Same-version round-trip invariants

Add automated contract tests for each supported document type:

1. Create a graph containing all supported fields and child entities.
2. Export it.
3. Import the export into a clean graph with the same Lex version.
4. Compare normalized graph state.
5. Re-export and assert projection stability.

The invariant should ignore generated IDs and timestamps but require equality for semantic fields, ordering, ref-codes, and relationships.

### LEX-XFER-E005: Post-import structural delta report

After import, include a compact source-versus-target summary. For example:

```json
{
  "entity": "PAT-CORE-WEB-VALIDATION",
  "source": { "sections": 14, "examples": 23, "rules": 0 },
  "target": { "sections": 50, "examples": 46, "rules": 0 },
  "warnings": ["category changed", "zone cleared", "child counts differ"]
}
```

This would make structural corruption visible immediately instead of requiring a manual export/diff pass.

## Current Safe Workaround

Until a canonical transfer command exists, avoid using generated Markdown as a trusted cross-repository interchange format for these entity types. Use structured Lex commands instead:

- `lex adr batch` for ADR fields
- `lex arch batch` for architecture fields and raw content
- `lex pattern create`, followed by `pattern section add`, `pattern example add`, and `pattern rule add`
- `lex diagram batch` for architecture diagrams
- `lex link create` for validated relationships

Verify the target with normalized graph comparisons and an export diff before treating the transfer as complete.
