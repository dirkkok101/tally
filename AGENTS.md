<!-- LEX-MANAGED-BEGIN v1 -->
## Lexicon

This repo uses **lex** — a CLI that stores SDLC artifacts (PRDs,
technical designs, ADRs, patterns, architecture docs, plans) in a
graph. In a normal git repo, `.lexicon/graph/` is the durable, branch-level source of truth.
Each worktree has a gitignored
`.lexicon/lexicon.db`; the worktree database is the authoritative working index
and is rebuilt from committed graph files. Export baselines, migration
manifests, and other operational state stay under `.lexicon/`. Every entity has a stable ref-code
(`FR-AUTH-LOGIN`, `ADR-CORE-0004`, `PAT-CORE-CQRS`, ...). Markdown
under `docs/` is derived output (via `lex export`) or human-authored
companion documentation. Markdown under `docs/` is derived output for
lex-owned artifacts and is never an authoring or merge source.

### The pipeline (all skills run unsupervised — machine gates, no approval pauses)

    brainstorm -> [discovery] -> prd -> design -> plan -> review-documentation -> beads -> execute -> review-work
    Bug fix: diagnose -> fix / beads / brainstorm
    Brownfield docs: migrate scan/apply -> reconcile
    Companions: compound (route lessons to their governing artifact), spike (throwaway code answers a question, the graph keeps the answer), adr, pattern, architecture

Bead execution defaults to `/lex:execute`. `legion spawn` (parallel
swarm) is user-initiated only — never start or scale a Legion swarm
unless the user explicitly asks for it.

### Skills (recommended UX) and bare CLI (fallback)

| Stage | Skill | Bare CLI fallback |
|-------|-------|-------------------|
| Problem framing | `/lex:brainstorm` | `lex module create`, `lex goal create`, `lex non-goal create`, `lex kill-criterion create`, `lex risk create` |
| Requirements depth | `/lex:discovery` | `lex persona create`, `lex use-case create`, `lex assumption create`, `lex glossary create`, `lex external-dependency create` |
| Requirements | `/lex:prd` | `lex fr create`, `lex nfr create`, `lex use-case create`, `lex success-metric create`, `lex fr batch` |
| Technical design | `/lex:design` | `lex module-design create`, `lex decision create`, `lex endpoint create`, `lex data-model create`, `lex test-case create`, `lex coverage` |
| Implementation planning | `/lex:plan` | `lex plan create`, `lex sub-plan create`, `lex task batch`, `lex task dep add`, `lex plan coverage`, `lex plan audit` |
| Content review | `/lex:review-documentation` | `lex fr list --status active`, `lex review suppress list` (read-only) |
| Bead compilation | `/lex:beads` | `lex plan compile`, `lex bead-ref list`, `lex plan status`, `br ready` |
| Execution | `/lex:execute` | `lex context <bead-id>`, `br show`, `br close` |
| Work review | `/lex:review-work` | `lex bead-ref list`, `lex coverage`, `lex decision path-check` |
| Diagnosis | `/lex:diagnose` | `lex test-case list`, `lex open-question create`, `lex risk create` |
| Brownfield curation | `/lex:reconcile <RUN-ID>` | `lex migrate curate list|record|source-complete|verify|finalize` |
| Lesson capture | `/lex:compound` | `lex pattern rule add`, `lex adr create`, `lex constraint create` |
| Empirical spikes | `/lex:spike` | `lex open-question update`, `lex assumption update`, `lex decision create` |
| Architecture decisions | `/lex:adr` | `lex adr create`, `lex adr supersede`, `lex adr update` |
| Patterns | `/lex:pattern` | `lex pattern create`, `lex pattern section add`, `lex pattern example add`, `lex pattern rule add` |
| Architecture narrative | `/lex:architecture` | `lex arch create`, `lex link create` |

### Reading the graph (always `--json` for machine output)

- `lex stats [--module <CODE>] --json` — entity counts
- `lex fr list --module <CODE> --status active --json` / `lex fr show <REF> --json`
- `lex module show --name <CODE> --json` (module identity is the `--name` flag — code or name; NOT positional)
- `lex link list --ref-code <REF> --json` / `lex trace <REF> --json`
- `lex log <REF>` — audit trail; `lex diff --module <CODE>` — changes since export
- `lex coverage --module <CODE> --json` / `lex decision path-check --module <CODE> --json` / `lex endpoint suggest --module <CODE> --json` / `lex external-dependency check --module <CODE> --json`
- `lex plan coverage PLAN-<REF> --json` / `lex plan audit PLAN-<REF> --json` / `lex plan status PLAN-<REF> --json`
- `lex context (TASK-<MODULE>-<SLUG> | <bead-id>) [--max-tokens <n>] --json` — zero-context execution contract

Every entity type has `list` and `show`. Show/delete take the ref-code
positionally EXCEPT `module show --name <CODE>` and child-scoped
updates like `lex assumption update --module <CODE> --identifier <id>`.

### Command forms that differ from what you might guess

- `lex pattern rule add PAT-<REF> --rule-text "<t>" --rule-type <must|should|must_not>` (positional pattern ref; enum uses underscores)
- `lex pattern example add PAT-<REF> --name "<n>" --code "<c>" [--is-counter]` (`--is-counter` marks the bad example)
- `lex pattern section add PAT-<REF> --title "<t>" [--content <text>] [--order <n>]`
- `lex adr supersede ADR-<M>-<OLD> --by ADR-<M>-<NEW>` then `lex adr update ADR-<M>-<OLD> --status-notes "<why>"` (supersede takes no reason)
- `lex persona create --project <NAME> --code P<n> ...` (persona codes are P1, P2, ...)
- `lex use-case create ... --failure-paths '["<path 1>", "<path 2>"]'` (JSON string array — prose is rejected)
- `lex kill-criterion create --module <CODE> --condition "<c>" [--monitored]` and `lex success-metric create ... [--measured]` (valueless switches)
- `lex risk create --module <CODE> --description "<d>" --likelihood <l> --impact <i>` (likelihood and impact are required)
- br: `br create "Title" --type task -p 2`, `br label add <id> -l <LABEL>` (one label per `-l`), `br close <id> --reason "<text>"`

### Writing to the graph

All writes go through `lex <entity> create|update|delete` and are
audited. **Never edit exported markdown and expect lex to notice** —
edit the entity, re-export. Cross-references are the correctness
contract: `lex link create <src> <dst> --rel <relationship>`
(recommended: depends-on, supports, implements, motivated-by,
references, supersedes, see-also); `lex link suggest --module <CODE>
--json` finds orphan mentions.

### Adversarial content review

- `/lex:review-documentation --module <CODE>` — report-only default; four read-only sub-agents (vague-AC, decision-gap, contradiction, naming-drift) + consolidator.
- `--apply` runs unsupervised on BOTH platforms: MECHANICAL findings auto-applied via `lex <entity> update`, JUDGMENT findings filed as br beads.
- Suppressions are user-issued only: `lex review suppress <ref> --field <name> --agent <slug> --reason "<text>" --fingerprint <hex>` / `lex review unsuppress <id>`.

### Prerequisites

- `lex` binary on PATH (`lex --version`). Skills probe it and stop with one diagnostic if missing.
- Install skills: `lex init --providers codex,claude-code` from the target repo root (may also preflight brownfield `docs/`). If invoking `/lex:prd` fails with "skill not found", re-run that install. Manual fallback: Claude — `claude plugin marketplace add <lex-repo> --scope user` + `claude plugin install lex@imperium-lexicon --scope user`; Codex — `ln -sf <lex-repo>/lex-skills/.codex/skills ~/.agents/skills/lex`.
- Embedded docs, no clone needed: `lex docs list|user|migrate|agent`.

### Error codes

| Code | Meaning |
|------|---------|
| `LEX-001` | Entity not found |
| `LEX-002` | Uniqueness violation |
| `LEX-003` | Immutability violation |
| `LEX-004` | RESTRICT block (delete dependents first) |
| `LEX-006` | Schema / IO error or invalid arguments |

### Anti-patterns

- Do NOT author PRD / design / ADR / plan content as raw markdown — enter the graph via CLI (or the `/lex:*` skill); export generates the markdown.
- Do NOT edit `docs/**/*.md` that `lex export` owns — edit the entity, re-export.
- Do NOT skip `lex link create` for cross-entity references.
- Do NOT bypass the skills' availability checks.
<!-- LEX-MANAGED-END -->
