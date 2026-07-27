# BUDGET local data security

Status: verification gate for `TASK-BUDGET-GATE-SECURITY` / `NFR-BUDGET-LOCAL-DATA-PROTECTION` / `NFR-BUDGET-SELF-CONTAINED-LOCAL-OPERATION`.

This report is **metadata-only**. It records case inventory, permission invariants, canary surfaces, and runner outcomes. It does **not** include plan amounts, category display names, raw idempotency keys, request/response JSON, crash dumps, or other financial payloads.

## Gate command

```bash
bash scripts/verify-budget-security.sh
```

Expected: exit 0; non-vacuous discovery of the security matrix; zero canary leaks; exact `0700` directories / `0600` files for `budget/` and recognized artifacts under the published Native-AOT binary; zero leftover processes.

## Evidence surface

| Artifact | Role |
|---|---|
| `tests/Tally.Tests/Budget/Security/BudgetSecurityGateTests.cs` | Security/privacy matrix against real `BudgetStateStore`, `TallyProcess`, and published `tally` |
| `scripts/verify-budget-security.sh` | Build, NativeAOT publish, discovery, xUnit matrix, filesystem mode spot-check, process inventory |
| `docs/verification/budget-security.md` | This metadata-only report (`BudgetSecurityGateEvidence`) |

## Case families

### Owner-only permissions (`TC-BUDGET-LOCAL-DATA-PROTECTION`)

| Family | Proves |
|---|---|
| Bootstrap modes | Data root and `budget/` are `0700`; `budget.db` is `0600` |
| WAL / SHM | Sidecars created during write are `0600` |
| Success workflow | Draft → get → activate → position leaves recognized artifacts owner-only |
| Validation failure | No orphan plan/revision/entry rows; modes remain owner-only |
| Fail-closed modes | Permissive directory or database fails `RequireOwnerOnlyArtifacts` before trust |
| Lock / atomic | Recognized temporary writer artifacts re-protected on open |
| Unknown files | Non-recognized files under `budget/` are left alone |

### Canary non-disclosure

| Surface | Seeded canaries must not appear in |
|---|---|
| Malformed JSON | stderr, error envelope, parser/stack text |
| Unknown fields | stderr, error codes, messages |
| Unsafe / symlink input paths | stderr, stdout, usage diagnostics |
| Oversized actor labels | stderr, stdout |
| Invalid amount + reason + key | stderr, error envelope (metadata codes only) |
| Idempotency conflict | raw key on stderr or error payload |
| Success amount | stderr only — structured stdout result is the sole payload channel |

### Hostile boundaries (fail before mutation)

- Unsupported contract version → compatibility failure, no row growth
- Over-limit list → `BUDGET-RESOURCE-LIMIT`, no stack echo
- Symlink / non-`@` input paths → usage rejection without path echo
- Symlink-shaped `budget.db` → reparse attribute detectable (not a normal durability file)

### Self-contained local operation (`TC-BUDGET-SELF-CONTAINED-LOCAL-OPERATION`)

- Budget composition sources contain no HTTP listeners, plugin loaders, `HttpClient`, or `Process.Start`
- Registry exposes exactly six BUDGET operations; no sync/watch/daemon/webhook aliases
- Schema discovery is metadata-only (no `budget.db` paths or sample amounts)
- Published NativeAOT binary budget reads open no sockets or child processes
- Published invalid draft does not echo amount / reason / key canaries on stderr

## Permission invariants

After every successful open and after the gate’s published-binary spot-check:

1. `budget/` directory mode is exactly `700` and owned by the invoking uid
2. `budget.db` mode is exactly `600` and owned by the invoking uid
3. Present `-wal` / `-shm` / `.lock` / `.atomic` sidecars are mode `600`
4. `RequireOwnerOnlyArtifacts` rejects group/other bits without repair-as-success

## Governing decisions and NFRs

- `DD-BUDGET-APPLICATION-ARCHITECTURE` — typed vertical slices; one public-contract seam; no transport plugins
- `DD-BUDGET-STATE-STORE` — separate owner-only raw-SQLite `budget.db` under the Tally data root
- `NFR-BUDGET-LOCAL-DATA-PROTECTION` — zero payload disclosure outside structured data results; owner-only artifacts
- `NFR-BUDGET-SELF-CONTAINED-LOCAL-OPERATION` — offline, non-interactive, no daemon/watcher/network service

## How to re-run

```bash
dotnet build Tally.slnx --nologo
dotnet test tests/Tally.Tests/Tally.Tests.csproj \
  --filter 'FullyQualifiedName~BudgetSecurityGateTests' \
  --logger 'console;verbosity=normal'
bash scripts/verify-budget-security.sh
```

## Result

Record the runner exit code and discovered case count when the gate is executed. Do not paste financial payloads, category names, raw keys, or absolute secret paths into this file.

## Latest run

Executed on 2026-07-27 via `bash scripts/verify-budget-security.sh`:

| Check | Result |
|---|---|
| Host platform | Linux (owner-only modes supported) |
| `dotnet build Tally.slnx` | Passed, 0 warnings, 0 errors |
| NativeAOT publish (`linux-x64`) | Passed; executable `tally` produced |
| Discovery (`BudgetSecurityGateTests`) | 25 cases |
| Required case families | Present (permissions, canaries, hostile, self-contained, published) |
| xUnit execution with `TALLY_PUBLISHED_BINARY` | 25 passed, 0 failed, 0 skipped |
| Filesystem spot-check | `budget/` mode `700`, `budget.db` mode `600`, owner uid matches invoker |
| Process inventory | No leftover published `tally` processes |
| Script exit | 0 |
