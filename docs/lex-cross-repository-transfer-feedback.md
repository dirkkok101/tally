# Lex Transfer and Workflow Feedback

This document records defects and enhancement opportunities observed while transferring reusable architecture knowledge from the NxGN Actions Lex graph into a new Tally graph and then using the packaged Lex skills and CLI to frame, discover, and formalize Tally's first module.

## Environment

| Item | Value |
|------|-------|
| Date observed | 2026-07-17 to 2026-07-18 |
| Lex version | 0.5.3 |
| Source repository | `/home/ubuntu/nxgn.actions/main` |
| Target repository | `/home/ubuntu/tally/main` |
| Source module | `CORE` (`Platform Core`) |
| Intended transfer | 29 ADRs, 33 patterns, 16 architecture documents |

The source material was exported and imported with the same Lex 0.5.3 binary, so the `LEX-XFER-*` findings are same-version round-trip defects rather than cross-version compatibility problems. The later `LEX-SKILL-*`, `LEX-CLI-*`, `LEX-GRAPH-*`, `LEX-FR-*`, and `LEX-SCHEMA-*` findings were reproduced with the same installed version during brainstorm, discovery, and PRD work.

## Summary

| ID | Type | Severity | Summary |
|----|------|----------|---------|
| LEX-XFER-001 | Bug | Medium | `lex init --name` does not create the named project |
| LEX-XFER-002 | Bug | High | ADR export/import prefixes the ref-code into the title and slug |
| LEX-XFER-003 | Bug | Medium | Pattern directory import creates a pattern from generated `README.md` |
| LEX-XFER-004 | Bug | High | Pattern export/import duplicates structure and loses classification metadata |
| LEX-XFER-005 | Bug | High | Architecture export/import duplicates generated content and loses category metadata |
| LEX-XFER-006 | Bug | Medium | Writes recommend sync conflict resolution when graph and DB are already in sync |
| LEX-SKILL-001 | Bug | High | Packaged skills emit constraint types rejected by Lex 0.5.3 |
| LEX-SKILL-002 | Bug | High | Discovery requires a goal description field that the goal CLI does not expose |
| LEX-CLI-001 | Bug | High | Invalid command arguments can print an error and still exit successfully |
| LEX-GRAPH-001 | Bug | Medium | Module statistics undercount explicit links |
| LEX-GRAPH-002 | Bug | High | Valid goal ref-codes cannot participate in explicit links |
| LEX-FR-001 | Bug | High | `fr batch` silently drops persona relationships |
| LEX-SCHEMA-001 | Bug | Medium | Test-case schemas omit enums enforced by the CLI |
| LEX-PLAN-001 | Bug | High | `task update` help and machine schema omit supported options |
| LEX-PLAN-002 | Bug | High | `task batch` claims update support but rejects existing tasks |
| LEX-DESIGN-E001 | Enhancement | High value | Add first-class CLI operation surfaces instead of forcing HTTP endpoints |
| LEX-DESIGN-E002 | Enhancement | High value | Distinguish planned greenfield decision paths from missing implemented paths |
| LEX-DESIGN-E003 | Enhancement | Medium value | Make feature areas group decisions, models, diagrams, and tests in workspace exports |
| LEX-GRAPH-E001 | Enhancement | High value | Count and project use-case primary actors as first-class persona relationships |
| LEX-BATCH-E001 | Enhancement | High value | Describe batch standard-input payloads in machine schemas |
| LEX-PLAN-E001 | Enhancement | High value | Accept transitive compile reachability for interface consumers |
| LEX-PLAN-E002 | Enhancement | High value | Classify intentionally loose foundation and gate tasks |
| LEX-PLAN-E003 | Enhancement | Medium value | Make bead-reference coverage warnings phase-aware |
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

### LEX-SKILL-001: Packaged skills emit invalid constraint types

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

The same defect recurs in `lex:discovery`. Its persistence instructions and CLI reference use:

```sh
lex constraint create --module LEDGER --title "Privacy regulation" \
  --type compliance --description "..."
```

Lex 0.5.3 rejects `compliance` because the corresponding accepted taxonomy value is `regulatory`. The workaround is `--type regulatory`. The contract test proposed above should validate command examples in every packaged skill, not only `lex:brainstorm`.

### LEX-SKILL-002: Discovery requires an unavailable goal description field

The `lex:discovery` skill requires:

> Every goal names its evidence class in the description.

It also makes omission of the evidence class a failure criterion. However, the Lex 0.5.3 schemas expose only these goal fields:

```json
{
  "goal create": ["module", "text", "target"],
  "goal update": ["module", "identifier", "text", "target", "clear-target", "code"]
}
```

There is no `--description` option and no first-class evidence field. An agent therefore cannot satisfy the skill literally, and a fresh reviewer cannot distinguish structured evidence metadata from prose conventions.

**Expected:**

Either add a goal description/evidence field to create, update, show, list, export, and import, or update the skill to name the supported field where evidence must be encoded. Prefer a constrained `evidence_class` value (`direct`, `analogous`, or `assumption`) plus an evidence note so readiness checks can validate it mechanically.

**Workaround:**

Append `Evidence: <class> — <basis>` to the goal text. For assumption-based goals, create the required assumption entity separately.

### LEX-CLI-001: Invalid command arguments can exit successfully

**Command:**

```sh
lex link list --module LEDGER --json
```

**Observed:**

```text
Argument '--module' is not recognized.
Usage: link list [options...] [-h|--help] [--version]
```

The process nevertheless exited with code `0`. A shell fallback guarded by `||` therefore did not run.

**Expected:**

Argument parsing and usage failures must return a stable non-zero exit code, preferably distinct from domain and infrastructure failures. This is essential for Lex's own agent-oriented workflows: automation must never interpret a rejected invocation as success.

**Workaround:**

Do not trust the exit code alone for this command surface; also validate the expected JSON shape. This is fragile and should only be temporary.

### LEX-GRAPH-001: Module statistics undercount explicit links

After discovery, `lex link list --ref-code OQ-LEDGER-8 --json` returned three distinct explicit links involving the open question:

- `EXT-LEDGER-HOST-OS-SECURITY -> OQ-LEDGER-8`
- `RISK-LEDGER-007 -> OQ-LEDGER-8`
- `OQ-LEDGER-8 -> UC-LEDGER-007`

All three source entities belong to LEDGER and the link projections are present in graph files. However:

```sh
lex stats --module LEDGER --json
```

reported:

```json
{
  "link_count": 1
}
```

**Expected:**

Define and document whether `link_count` means outgoing, incoming, internal, or incident links, then count that set consistently. For module statistics, the most useful default is unique explicit links whose source belongs to the module, with separate counts for incoming cross-module links if needed.

**Workaround:**

Query links from each relevant entity with `lex link list --ref-code <REF> --json` and deduplicate by link ID. There is currently no module-wide `link list` filter.

### LEX-GRAPH-002: Valid goal ref-codes cannot participate in explicit links

LEDGER goals were created through the supported goal commands and expose valid graph ref-codes `G1` through `G8`. Functional and non-functional requirements then referenced those goals in their descriptions and rationales. Attempting to make the traceability explicit failed:

```sh
lex link create \
  --source FR-LEDGER-ACCOUNT-MAINTENANCE \
  --target G1 \
  --rel motivated-by \
  --if-not-exists
```

```text
Error [LEX-006]: Unknown entity prefix in ref-code 'G1'.
```

`lex goal list --module LEDGER --json` and `lex goal show` both identify the goal as `G1`; there is no alternative canonical ref-code exposed to callers. `lex link suggest --module LEDGER --json` also returned no suggestions for requirement text containing `G1` through `G8`.

**Expected:**

Every ref-code emitted by a first-class graph entity should be accepted by `link create`, `link list`, `trace`, and `link suggest`. Either teach the link resolver that `G<n>` is a goal ref-code or generate goals with an unambiguous canonical prefix while preserving backwards compatibility.

**Workaround:**

Keep goal references in requirement descriptions and rationales, link FRs to their use cases, and link NFRs to the FRs they support. This preserves readable traceability but cannot produce a complete explicit goal-to-requirement graph.

### LEX-FR-001: `fr batch` silently drops persona relationships

A valid functional-requirement batch included the same persona value accepted by `fr create` and `fr update`:

```json
{
  "slug": "account-maintenance",
  "title": "Maintain owned bank accounts",
  "personas": ["P1"]
}
```

The batch succeeded and created all 17 requirements, but `lex stats --module LEDGER --json` still reported `"personas_linked": 0`, and `lex fr show FR-LEDGER-ACCOUNT-MAINTENANCE --json` contained no persona relationship. Running this explicit update for each requirement fixed the projection and changed the statistic to one linked persona:

```sh
lex fr update FR-LEDGER-ACCOUNT-MAINTENANCE --personas P1
```

**Expected:**

`fr batch` should accept every semantically equivalent create field, reject unsupported fields, and never report success after silently dropping relationship data. Its machine schema or example should define whether `personas` is a string or an array.

**Workaround:**

After every batch, inspect a representative requirement with `fr show` and apply `fr update <REF> --personas <CODE>` explicitly.

### LEX-SCHEMA-001: Test-case schemas omit enums enforced by the CLI

`lex schema test-case create --json` describes `level`, `surface`, `scenario`, `automation`, and `status` as unconstrained strings and emits an empty `enums` object. Runtime validation nevertheless rejects values and reveals hidden enum sets only through errors:

```text
Error [LEX-006]: Invalid level 'acceptance'. Must be one of: unit, integration, e2e.
Error [LEX-006]: Invalid surface 'cli'. Must be one of: browser, api, service, component, persistence, contract.
```

This is especially awkward for a self-documenting, agent-facing CLI: `cli` is a natural surface for Tally contract tests, while the accepted representation is the undiscoverable value `contract`.

**Expected:**

Expose every enforced enum through each command's `enum_values` and top-level `enums` projection. Add schema-versus-runtime contract tests for all constrained string options, including scenario, automation, and lifecycle status.

**Workaround:**

Use `integration` plus `contract` for public CLI contract intents, and treat runtime error messages as the temporary source for any other missing value set.

### LEX-PLAN-001: `task update` help and machine schema omit supported options

During LEDGER planning, `lex task update --help` documented only the
multi-value grammars and clear flags. It did not list supported scalar options
such as `--title`, `--description`, and `--objective`. The corresponding
machine schema was also empty:

```json
{
  "command": "task update",
  "required": [],
  "optional": [],
  "arguments": [],
  "options": []
}
```

This was not merely incomplete presentation. `--summary` was rejected while
the semantically corresponding `--description` was accepted, and neither the
help nor schema allowed an agent to discover that distinction before writing.
The same discoverability problem appeared in `task refs add`: help rendered
`--required <bool>`, but `--required true` rejected `true` as an unrecognized
argument; omitting the option created a required reference.

**Expected:**

`task create` and `task update` should expose every accepted scalar,
multi-value, clear, enum, and conflict rule in both human help and the
machine-readable schema. Generated help and command parsing should be tested
from the same option definition.

### LEX-PLAN-002: `task batch` claims update support but rejects existing tasks

The `lex task` command summary describes `task batch` as:

```text
Create or update task entries in a batch.
```

Re-submitting a complete `BACKUP-VERIFY` item to revise an existing plan task
instead returned:

```text
Error [LEX-002]: Item [0].slug: Task with ref-code
'TASK-LEDGER-BACKUP-VERIFY' already exists.
```

The task then had to be cleared and rebuilt through multiple `task update`
calls, with references and dependencies repaired through separate commands.
That creates a needless partial-update window and makes recovery harder if one
step fails.

**Expected:**

Either implement the advertised deterministic upsert behavior, or describe
the command as create-only. Prefer an explicit `--mode create|update|upsert`
with whole-item validation and atomic replacement semantics.

## Enhancement Proposals

### LEX-PLAN-E001: Accept transitive compile reachability for interface consumers

`lex plan audit` requires a direct compile edge from every consuming task to
the task that first produces the named interface, even when an existing
compile path already guarantees that producer completes first. For LEDGER,
this forced many redundant direct edges into the initial public-contract gate.
Its assembled context reached 4,018 tokens, primarily because 13 direct
dependencies and their summaries were included.

The plan was improved by introducing three real, independently testable
operation bundles, but that architectural split should be a design choice,
not the only way to satisfy a direct-edge checker. Audit should accept
transitive compile reachability where the consumed interface remains
available, or distinguish a true direct ABI handoff from ordinary execution
ordering. When it recommends a direct edge, it should report the projected
context-budget cost and whether a bundle/convergence task is the better remedy.

### LEX-PLAN-E002: Classify intentionally loose foundation and gate tasks

The final LEDGER plan contains 30 tasks without `implements` references: five
shared foundations, six evidence gates, seven convergence/security/module
gates, and twelve use-case verification tasks. Each task explains why it must
not claim delivery of a functional requirement, yet `lex plan coverage`
reports all 30 uniformly as loose-task warnings.

Add a constrained task kind such as `implementation`, `foundation`,
`evidence_gate`, `integration_gate`, and `verification`, plus a required
justification for non-implementation kinds. Coverage can then reject hollow
tasks while treating intentionally loose, fully specified gates as healthy.
This avoids fake FR links added solely to make the status green.

### LEX-PLAN-E003: Make bead-reference coverage warnings phase-aware

`lex:plan` explicitly ends before bead compilation, but `lex plan coverage`
warned that all 47 LEDGER tasks had no bead references. The warning is true but
not actionable at the planning gate and guarantees a warning status for every
new plan that correctly follows the workflow.

Separate plan readiness from execution compilation readiness. Before
`lex:beads`, missing bead references should be informational or suppressed;
after compilation, they should become warnings or errors according to task
kind and plan state.

### LEX-DESIGN-E001: Add first-class CLI operation surfaces

The `lex:design` workflow requires explicit external interfaces and runs
`lex endpoint suggest` as a design gate, but the only first-class interface
entity is HTTP-shaped:

```json
{
  "required": ["module", "slug", "method", "path"],
  "method": ["get", "post", "put", "delete", "patch"]
}
```

LEDGER is intentionally a local CLI with no listener or HTTP contract. Its
external surface consists of stable operation IDs, command-token paths, JSON
standard-input schemas, stdout result schemas, stderr rules, and exit
categories. Encoding these as fake GET/POST endpoints would misstate the
architecture. Omitting endpoints is semantically correct, but
`lex endpoint suggest --module LEDGER --json` then reports HTTP suggestions
because requirements contain generic words such as `detail`, `restore`, and
`filter`.

Add an interface/operation entity that can represent at least `cli`, `http`,
`event`, and `library` transports. A CLI operation should model:

- Stable operation ID and tokenized command path
- Read-only or mutating classification
- Versioned request and result schemas
- Standard-input and file-input behavior
- Structured errors and process exit categories
- Help/schema/compatibility metadata
- Links to tests, feature areas, and requirements

`endpoint suggest`, coverage, exporters, design import, and plan context should
reason over the selected transport instead of assuming HTTP. Until then, the
LEDGER design records its operation registry as `DM-LEDGER-OPERATION-DESCRIPTOR`
and explains why no endpoint entities exist.

### LEX-DESIGN-E002: Support planned expected paths in greenfield designs

The packaged `lex:design` skill requires decisions to record concrete expected
implementation paths before planning and treats `lex decision path-check`
warnings as a design gate. Lex 0.5.3, however, has only one unqualified
`expected_path` value: every path is checked for existence immediately.

For a greenfield module with no source project yet, every truthful path such as
`src/Tally/Infrastructure/Storage` is therefore reported missing. The agent
must choose between a permanently warning design, dishonest references to
unrelated existing files, premature implementation scaffolding during design,
or omission of the structured metadata.

Add lifecycle semantics to expected paths, for example:

```text
--expected-path src/Tally/Infrastructure/Storage --path-state planned
```

Design review should validate that planned paths are concrete, non-overlapping,
and assigned to implementation tasks. `path-check` should require existence
only for `implemented` decisions or after a linked plan task is complete. The
current LEDGER workaround keeps the planned path contract in
`MD-LEDGER-MASTER` and leaves decision path metadata empty so the graph remains
truthful.

The same lifecycle gap appears in the full composite check. Immediately after
design authoring, `lex check --json` passed with 546 drift warnings, including
one `unanchored` warning for every new LEDGER decision and data model even
though no implementation phase has begun. The pre-design baseline already had
514 inherited CORE warnings; the 32-warning delta is exactly the new greenfield
design surface. Drift should become actionable only when an entity reaches the
lifecycle stage where code anchors are expected.

### LEX-DESIGN-E003: Export complete feature-area membership

The design skill says a feature area groups decisions, endpoints, and models.
LEDGER created five feature areas and explicit links from each area to its
decisions, data models, requirements, and verification concerns. The workspace
export nevertheless groups only HTTP endpoints by feature area. The exporter
source and generated output confirm that its `belongs_to` grouping query is
limited to `api_endpoint -> feature_area`.

For a CLI module with no fake HTTP endpoints, this produces empty
`features/<area>/api-surface.md` and `ui-mockup.md` files while all test cases
land in `features/_ungrouped/test-plan.md`. Decisions and data models remain
global, so the required feature-by-feature design walk loses the membership
captured in the graph.

Support feature-area membership for decisions, data models, diagrams, mockups,
test cases, and interface/operation entities. The workspace exporter should
project each member into the owning area or emit an area-local index that links
to its canonical document. Empty HTTP/UI projection files should be omitted
when the module has no such surface.

### LEX-GRAPH-E001: Model use-case primary actors as persona relationships

Seven LEDGER use cases were created with `--primary-actor P1`. `lex use-case show UC-LEDGER-001 --json` correctly reconstructed:

```json
{
  "ref_code": "UC-LEDGER-001",
  "primary_actor": "P1"
}
```

But `lex stats --module LEDGER --json` still reported:

```json
{
  "personas_linked": 0,
  "use_cases": 7
}
```

The graph projection stores the actor inside an opaque JSON string:

```json
{
  "metadata": "{\"depth_tier\":2,\"primary_actor\":\"P1\",...}"
}
```

`lex use-case list --module LEDGER --json` also omits the primary actor. This makes the discovery readiness rule—every persona must be the primary actor in at least one use case—impossible to verify from stats or list output and forces one `show` call per use case.

Promote `primary_actor`, `trigger_event`, `depth_tier`, and status to first-class graph fields, validate the actor against project personas, count that relationship in `personas_linked`, and expose it in list output. Add an integrity check for missing or unknown persona codes.

### LEX-BATCH-E001: Describe batch standard-input payloads in machine schemas

`lex schema fr batch --json` exposes only `--module` and `--example`; it does not say that the command reads a JSON array from standard input, whether EOF is required, which properties are accepted, or how create-versus-update is selected. `lex fr batch --module LEDGER --example` is useful to a human but is not a schema an agent can validate mechanically.

Add a structured batch payload schema containing:

- Top-level input type and standard-input requirement
- Required and optional item properties
- Property types, enums, and nullability
- Create/update identity and conflict rules
- Relationship-field shapes such as `personas` and `related`
- Per-item atomicity and whole-batch failure semantics

The same contract should be available for every batch-capable entity and should be exercised by generated examples in release tests.

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
