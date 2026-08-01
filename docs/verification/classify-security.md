# CLASSIFY local data security

Status: verification gate for `TASK-CLASSIFY-RULEBOOK-GATE-SECURITY` / `bd-2igu` /
`NFR-CLASSIFY-LOCAL-DATA-PROTECTION` / `NFR-CLASSIFY-SELF-CONTAINED-LOCAL-OPERATION`.

This report is **metadata-only**. It records case inventory, permission invariants, canary
surfaces, and runner outcomes. It does **not** include descriptions, amounts, normalized tokens,
rule bodies, corpus rows, raw idempotency keys, request/response JSON, crash dumps, private
fixture paths, or other financial/private payloads.

## Gate command

```bash
bash scripts/verify-classify-security.sh
```

Expected: exit 0; non-vacuous discovery of the security matrix; zero canary leaks; exact `0700`
directories / `0600` files for `classify/` and recognized artifacts under the published
Native-AOT binary; effective-UID ownership enforced before trust; zero leftover processes;
store-free schema discovery.

## Evidence surface (`ClassifySecurityGateEvidence`)

| Artifact | Role |
|---|---|
| `tests/Tally.Tests/Classify/Security/ClassifySecurityGateTests.cs` | Security/privacy matrix against real `ClassifyStateStore`, `TallyProcess`, and published `tally` |
| `scripts/verify-classify-security.sh` | Build, NativeAOT publish, discovery, xUnit matrix, filesystem mode/ownership spot-check, network-denied schema probe, process inventory, non-interactive probe |
| `docs/verification/classify-security.md` | This metadata-only report |
| `src/Tally/Infrastructure/Storage/HostArtifactProtection.cs` | Shared Linux owner identity and owner-only mode guard (Hermes correction seam) |

## Case families

### Owner-only permissions (`TC-CLASSIFY-LOCAL-ARTIFACT-PROTECTION`)

| Family | Proves |
|---|---|
| Bootstrap modes | Data root and `classify/` (+ `tmp/`, `reports/`) are `0700`; `classify.db` is `0600` |
| WAL / SHM | Sidecars created during write are `0600` when present |
| Success / status workflow | Status + cleanup leave recognized artifacts owner-only |
| Validation failure | No orphan idempotency/tombstone rows; modes remain owner-only |
| Fail-closed modes | Permissive directory or database fails `RequireOwnerOnlyArtifacts` before trust |
| Wrong-owner file | Exact `0600` mode but `st_uid ≠ geteuid()` fails closed |
| Wrong-owner directory | Exact `0700` mode but `st_uid ≠ geteuid()` fails closed |
| Unknown files | Non-recognized files under `classify/` are left alone |
| Cleanup | Recognized temps removable; unknown temps retained; no raw corpus copies |

### Canary non-disclosure

| Surface | Seeded canaries must not appear in |
|---|---|
| Malformed JSON | stderr, error envelope, parser/stack text |
| Unknown fields | stderr, error codes, messages |
| Unsafe / symlink input paths | stderr, stdout, usage diagnostics |
| Oversized actor labels | stderr, stdout |
| Reason + idempotency key | stderr, error envelope (metadata codes only) |
| Status / abandon failures | descriptions, tokens, corpus, rule text, amounts |

### Hostile boundaries (fail before activation / Ledger mutation)

- Unsupported contract version → compatibility failure, no idempotency growth
- Malformed subject type → validation failure without canary echo
- Symlink / non-`@` input paths → usage rejection without path echo
- Symlink-shaped `classify.db` → reparse attribute detectable
- Outside-root paths → rejected by artifact protection containment
- Unknown temporary names → never staged/deleted
- Symlink temporaries → never staged/deleted
- Wrong-owner file/directory → mode-correct paths still rejected when `st_uid ≠ geteuid()`

### Self-contained local operation (`TC-CLASSIFY-OFFLINE-PROCESS-ISOLATION`)

- CLASSIFY composition sources contain no HTTP listeners, plugin loaders, `HttpClient`, cloud SDKs, or `Process.Start`
- Registry exposes exactly twelve CLASSIFY operations; no sync/watch/daemon/webhook/embed aliases
- Schema discovery is metadata-only (no `classify.db` paths or canary payloads)
- Published NativeAOT binary classify reads open no sockets or child processes
- Published invalid abandon does not echo reason / key / corpus canaries on stderr
- Schema list without `TALLY_DATA_ROOT` remains store-free

## Permission invariants

After every successful open and after the gate’s published-binary spot-check:

1. `classify/` directory mode is exactly `700` and owned by the invoking effective UID
2. `classify/tmp/` and `classify/reports/` are `700` when present
3. `classify.db` mode is exactly `600` and owned by the invoking effective UID
4. Present `-wal` / `-shm` / `-journal` / `.lock` sidecars are mode `600` and euid-owned
5. `RequireOwnerOnlyArtifact` / `RequireOwnerOnlyDirectory` reject wrong mode **or** wrong owner without repair-as-success

## Governing decisions and NFRs

- `DD-CLASSIFY-APPLICATION-ARCHITECTURE` — single-process vertical slices; one public-contract seam
- `DD-CLASSIFY-ARTIFACT-RETENTION` — fixed owner-only retention and recognized-artifact cleanup
- `DD-CLASSIFY-PRIVATE-VALIDATION` — memory-only private corpus; aggregate durability only
- `EXT-CLASSIFY-HOST-OS-SECURITY` — host process and storage security assumptions
- `NFR-CLASSIFY-LOCAL-DATA-PROTECTION` — zero private payload disclosure outside contracted channels; owner-only artifacts
- `NFR-CLASSIFY-SELF-CONTAINED-LOCAL-OPERATION` — offline, non-interactive, no daemon/watcher/network service

## How to re-run

```bash
dotnet build Tally.slnx --nologo
dotnet test tests/Tally.Tests/Tally.Tests.csproj \
  --filter 'FullyQualifiedName~ClassifySecurityGateTests' \
  --logger 'console;verbosity=normal'
bash scripts/verify-classify-security.sh
```

## Result

Record the runner exit code and discovered case count when the gate is executed. Do not paste
private payloads, corpus rows, descriptions, amounts, raw keys, or absolute secret paths into
this file.

## Latest run

Hermes correction (effective-UID ownership): `HostArtifactProtection` now requires
`st_uid == geteuid()` in addition to exact `0600`/`0700` mode bits. Focused wrong-owner
file and directory evidence added and executed. Full security gate not executed in this cadence.

| Check | Result |
|---|---|
| Host platform | Linux (owner-only modes + effective UID) |
| `HostArtifactProtection` | Mode 0600/0700 preserved; euid ownership enforced via `lstat` + `geteuid` |
| Focused wrong-owner file evidence | `TC_CLASSIFY_LOCAL_DATA_PROTECTION_wrong_owner_file_fails_closed` — Passed |
| Focused wrong-owner directory evidence | `TC_CLASSIFY_LOCAL_DATA_PROTECTION_wrong_owner_directory_fails_closed` — Passed |
| `git diff --check` | Clean |
| `dotnet build Tally.slnx -c Release --no-restore` | 0 warnings, 0 errors |
| Focused ownership filter (`~wrong_owner`) | 2 passed, 0 failed |
| Full security gate / broad suite | Not run (cadence) |
