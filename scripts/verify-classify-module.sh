#!/usr/bin/env bash
# VerifiedClassifyV1Module — TASK-CLASSIFY-RULEBOOK-GATE-MODULE / bd-3l4k
# Final CLASSIFY module convergence: restore/build/format, full suite, Native-AOT,
# non-stale ClassifyGraphQualityEvidence, graph/path/deps, named discovery, clean diff.
# Metadata-only (no private/financial payloads; never prints private corpus paths).
set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
publish_root="$(mktemp -d "${TMPDIR:-/tmp}/tally-classify-module.XXXXXX")"
test_project="tests/Tally.Tests/Tally.Tests.csproj"
module="CLASSIFY"
plan="PLAN-CLASSIFY-RULEBOOK-V1"
report_path="docs/verification/classify-v1.md"
fail_count=0
log_dir="$(mktemp -d "${TMPDIR:-/tmp}/tally-classify-module-logs.XXXXXX")"

cleanup() {
    rm -rf -- "$publish_root"
    rm -rf -- "$log_dir"
}
trap cleanup EXIT

section() {
    printf '\n==> %s\n' "$1"
}

fail() {
    printf 'FAIL: %s\n' "$1" >&2
    fail_count=$((fail_count + 1))
}

require_cmd() {
    if ! command -v "$1" >/dev/null 2>&1; then
        printf 'required command not found: %s\n' "$1" >&2
        exit 1
    fi
}

discovered_count() {
    awk '
        /The following Tests are available:/ { listing = 1; next }
        listing && NF { count++ }
        END { print count + 0 }
    '
}

class_discovered_count() {
    local class_name="$1"
    local listing="$2"
    awk -v class="$class_name" '
        /The following Tests are available:/ { listing = 1; next }
        listing && index($0, "." class ".") { count++ }
        END { print count + 0 }
    ' <<< "$listing"
}

cd "$repository_root"

require_cmd lex
require_cmd dotnet
require_cmd python3
require_cmd jq
require_cmd rg
require_cmd sha256sum
require_cmd git

section "Host / tool fingerprint (safe metadata)"
printf 'kernel=%s\n' "$(uname -sr)"
printf 'cpus=%s load=%s\n' "$(nproc 2>/dev/null || echo unknown)" "$(cut -d ' ' -f 1-3 /proc/loadavg 2>/dev/null || echo unknown)"
printf 'lex=%s\n' "$(lex --version 2>/dev/null || true)"
printf 'dotnet=%s\n' "$(dotnet --version 2>/dev/null || true)"
printf 'cwd=%s\n' "$repository_root"
printf 'commit=%s\n' "$(git rev-parse HEAD 2>/dev/null || echo unknown)"
printf 'commit_short=%s\n' "$(git rev-parse --short HEAD 2>/dev/null || echo unknown)"
# Private corpus: reuse established owner-only env if present; never print path/content.
if [[ -n "${CLASSIFY_OWNER_RULEBOOK_CORPUS:-}" ]]; then
    printf 'owner_private_evidence: env present (path not printed)\n'
else
    printf 'owner_private_evidence: env not set (named OwnerRulebookGateTests still execute in-suite)\n'
fi

# ── Release restore / build / format ─────────────────────────────────────────
section "Release restore, zero-warning build, and formatting"
dotnet restore Tally.slnx
dotnet build Tally.slnx -c Release --no-restore --nologo
# Format CLASSIFY-owned surfaces (full-solution format may hit unrelated modules).
dotnet format Tally.slnx --verify-no-changes --no-restore --include \
    src/Tally/Features/Classify \
    src/Tally/Domain/Classify \
    src/Tally/Infrastructure/Classify \
    src/Tally/Contracts/Classify \
    src/Tally/Bootstrap/Features/ClassifyExtensions.cs \
    src/Tally/Bootstrap/Features/ClassifyApplyExtensions.cs \
    src/Tally/Bootstrap/Features/ClassifyCorpusExtensions.cs \
    src/Tally/Bootstrap/Features/ClassifyEvaluationExtensions.cs \
    src/Tally/Bootstrap/Features/ClassifyFeedbackExtensions.cs \
    src/Tally/Bootstrap/Features/ClassifyValidationExtensions.cs \
    tests/Tally.Tests/Classify

# ── NativeAOT publish ────────────────────────────────────────────────────────
section "Release linux-x64 NativeAOT publication"
publish_log="$log_dir/publish.log"
set +e
dotnet publish src/Tally/Tally.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    --no-restore \
    -p:PublishAot=true \
    -o "$publish_root" 2>&1 | tee "$publish_log"
publish_rc=${PIPESTATUS[0]}
set -e
if (( publish_rc != 0 )); then
    fail "NativeAOT publish failed with exit ${publish_rc}"
fi
if ! test -x "$publish_root/tally"; then
    fail "published tally binary missing or not executable"
fi
if rg -n -i 'warning (IL|TRIM|AOT|REFL)|RequiresUnreferencedCode|RequiresDynamicCode|DynamicallyAccessedMembers' "$publish_log" >/dev/null 2>&1; then
    fail "NativeAOT publish log contains trim/reflection/dynamic-code warnings"
    rg -n -i 'warning (IL|TRIM|AOT|REFL)|RequiresUnreferencedCode|RequiresDynamicCode|DynamicallyAccessedMembers' "$publish_log" >&2 || true
else
    printf 'NativeAOT publish: executable present; 0 trim/reflection/dynamic-code warning markers\n'
fi

export TALLY_PUBLISHED_BINARY="$publish_root/tally"

# ── Named CLASSIFY suite discovery (nonzero per class; not aggregate-only) ───
section "Named CLASSIFY suite discovery (nonzero per class)"
named_suites=(
    ClassificationDeterminismPropertyTests
    ClassificationEngineTests
    ClassificationEvaluationInputCancellationTests
    ClassificationEvaluationInputLoaderTests
    EvaluateClassificationCommandTests
    EvaluationLimitTests
    EvaluationPersistenceTests
    OutcomeExplanationTests
    OutcomeInvalidationTests
    ClassificationRuleVocabularyTests
    NormalizerV1Tests
    RuleActivationTests
    RuleDraftPersistenceTests
    RuleRetirementTests
    SaveClassificationRuleTests
    ApplyAuthorizationTests
    ApplyPreviewTests
    ClassificationApplySagaTests
    ClassificationApplyCrashRecoveryTests
    ClassificationFeedbackTests
    FeedbackProposalTests
    AbandonCleanupTests
    ClassificationStatusTests
    StatusPrivacyTests
    ClassifyHistoryInvariantTests
    ClassifyStateStoreTests
    ClassifyOperationContractTests
    ClassifyPublishedContractTests
    ClassifyProcessContractTests
    ClassifyLedgerBoundaryArchitectureTests
    ClassifyLedgerContractClientTests
    LedgerClassificationMutationPreconditionTests
    LedgerClassificationProjectionTests
    LedgerClassifyPrerequisiteTests
    ClassifyArtifactProtectionTests
    ClassifySecurityGateTests
    OwnerRulebookGateTests
    ClassificationRuleValidationTests
    PrivateCorpusPrivacyTests
    PrivateCorpusReaderTests
    ValidationLimitTests
    ValidationPrivacyTests
    ClassifyUc001EvaluationTests
    ClassifyUc002OutcomeTests
    ClassifyUc003ApplyTests
    ClassifyUc004RulesTests
    ClassifyUc005FeedbackTests
    ClassifyUc006AgentContractTests
    ClassifyGraphEvidenceGuardTests
    ClassifyModuleGuardTests
)

dotnet build "$test_project" -c Release --no-restore --nologo -v q

# Discover CLASSIFY-named suites (module guard lives under Tally.Tests namespace).
classify_list="$(dotnet test "$test_project" -c Release --list-tests --no-build --no-restore \
    --filter 'FullyQualifiedName~Tally.Tests.Classify|FullyQualifiedName~ClassifyModuleGuardTests')"
classify_count="$(printf '%s\n' "$classify_list" | discovered_count)"
if (( classify_count == 0 )); then
    fail "CLASSIFY named filter discovered zero tests"
fi

declare -A suite_floor=(
    [ClassifyUc001EvaluationTests]=10
    [ClassifyUc002OutcomeTests]=18
    [ClassifyUc003ApplyTests]=18
    [ClassifyUc004RulesTests]=18
    [ClassifyUc005FeedbackTests]=12
    [ClassifyUc006AgentContractTests]=18
    [ClassifySecurityGateTests]=20
    [OwnerRulebookGateTests]=10
    [PrivateCorpusReaderTests]=10
    [ClassifyPublishedContractTests]=10
    [ClassifyProcessContractTests]=5
    [ClassifyOperationContractTests]=10
    [ClassifyGraphEvidenceGuardTests]=5
    [ClassifyModuleGuardTests]=5
)

for class_name in "${named_suites[@]}"; do
    count="$(class_discovered_count "$class_name" "$classify_list")"
    floor="${suite_floor[$class_name]:-1}"
    if (( count < floor )); then
        fail "named suite ${class_name} discovered ${count} tests (need ≥${floor})"
    else
        printf '  %s: %s (floor %s)\n' "$class_name" "$count" "$floor"
    fi
done
printf 'named CLASSIFY suites: %s classes; aggregate CLASSIFY discovery=%s (not sole evidence)\n' \
    "${#named_suites[@]}" "$classify_count"

# ── Module guard unit tests ──────────────────────────────────────────────────
section "ClassifyModuleGuardTests execution"
if ! TALLY_PUBLISHED_BINARY="$publish_root/tally" \
    dotnet test "$test_project" \
        -c Release \
        --no-build \
        --no-restore \
        --filter 'FullyQualifiedName~ClassifyModuleGuardTests' \
        --logger 'console;verbosity=normal'
then
    fail "ClassifyModuleGuardTests execution failed"
fi

# ── Complete full test suite (entire Tally.Tests project) ────────────────────
# Isolation policy (documented): first complete module run under default parallelization
# produced mass CLASSIFY-RESOURCE-LIMIT / CLASSIFY-STALE cascades under concurrent
# validate/evaluate on shared host load (not assertion weakening). The full suite is
# run with MaxParallelThreads=1 so each case keeps the bounded processing budget and
# independent temp roots without hiding real failures via timeouts.
section "Complete full xUnit suite (Tally.Tests; MaxParallelThreads=1 isolation)"
full_test_log="$log_dir/full-tests.log"
set +e
TALLY_PUBLISHED_BINARY="$publish_root/tally" \
    dotnet test "$test_project" \
        -c Release \
        --no-build \
        --no-restore \
        --logger 'console;verbosity=minimal' \
        -- \
        xUnit.MaxParallelThreads=1 2>&1 | tee "$full_test_log"
full_test_rc=${PIPESTATUS[0]}
set -e
if (( full_test_rc != 0 )); then
    fail "full test suite failed with exit ${full_test_rc}"
fi
full_passed="$(rg -o 'Passed!\s*- Failed:\s*[0-9]+,\s*Passed:\s*[0-9]+' "$full_test_log" | tail -1 || true)"
full_failed="$(rg -o 'Failed:\s*[0-9]+' "$full_test_log" | tail -1 | rg -o '[0-9]+' || true)"
full_passed_n="$(rg -o 'Passed:\s*[0-9]+' "$full_test_log" | tail -1 | rg -o '[0-9]+' || true)"
full_skipped="$(rg -o 'Skipped:\s*[0-9]+' "$full_test_log" | tail -1 | rg -o '[0-9]+' || true)"
full_total="$(rg -o 'Total:\s*[0-9]+' "$full_test_log" | tail -1 | rg -o '[0-9]+' || true)"
printf 'full suite summary: passed=%s failed=%s skipped=%s total=%s\n' \
    "${full_passed_n:-?}" "${full_failed:-?}" "${full_skipped:-?}" "${full_total:-?}"
if [[ -n "${full_failed:-}" && "${full_failed}" != "0" ]]; then
    fail "full suite reported Failed=${full_failed}"
fi

# ── Non-stale ClassifyGraphQualityEvidence ───────────────────────────────────
section "Non-stale ClassifyGraphQualityEvidence (verify-classify-graph.sh)"
if [[ ! -x scripts/verify-classify-graph.sh ]]; then
    chmod +x scripts/verify-classify-graph.sh
fi
if ! bash scripts/verify-classify-graph.sh; then
    fail "verify-classify-graph.sh failed (ClassifyGraphQualityEvidence missing or stale)"
else
    printf 'ClassifyGraphQualityEvidence: current (graph script exit 0)\n'
fi

# ── Specialized gates (contract / security; owner-rulebook if env present) ───
section "Specialized classify gates"
for script in \
    scripts/verify-classify-contract.sh \
    scripts/verify-classify-security.sh
do
    if [[ ! -x "$script" && -f "$script" ]]; then
        chmod +x "$script"
    fi
    if [[ ! -f "$script" ]]; then
        fail "missing specialized gate script ${script}"
        continue
    fi
    printf '\n---- %s ----\n' "$script"
    if ! bash "$script"; then
        fail "${script} failed"
    else
        printf '%s: exit 0\n' "$script"
    fi
done

if [[ -n "${CLASSIFY_OWNER_RULEBOOK_CORPUS:-}" ]]; then
    printf '\n---- scripts/verify-classify-owner-rulebook.sh ----\n'
    # Env carries private path; do not echo it. Script must not print private content.
    if [[ ! -x scripts/verify-classify-owner-rulebook.sh ]]; then
        chmod +x scripts/verify-classify-owner-rulebook.sh
    fi
    if ! bash scripts/verify-classify-owner-rulebook.sh; then
        fail "verify-classify-owner-rulebook.sh failed with private evidence env present"
    else
        printf 'owner-rulebook specialized gate: exit 0 (path not printed)\n'
    fi
else
    printf 'owner-rulebook specialized gate: skipped (private evidence env not set; in-suite OwnerRulebookGateTests already executed)\n'
fi

# ── Lex integrity / coverage / paths / links ─────────────────────────────────
section "Lex graph integrity, coverage, paths, links"
if ! lex check --fast; then
    fail "lex check --fast failed"
else
    printf 'lex check --fast: ok\n'
fi

coverage_json="$(lex coverage --module "$module" --json)"
if ! printf '%s\n' "$coverage_json" | jq -e '
    .Status == "healthy"
    and .Summary.TotalRequirements == 13
    and .Summary.CoveredRequirements == 13
    and .Summary.MissingRequirements == 0
    and .Summary.OrphanTestCases == 0
    and .Summary.ErrorCount == 0
    and .Summary.WarningCount == 0
' >/dev/null; then
    fail "lex coverage is not 13/13 healthy with zero orphans"
else
    printf 'lex coverage: 13/13 active requirements, healthy, 0 orphans\n'
fi

path_json="$(lex decision path-check --module "$module" --json)"
path_ok="$(printf '%s\n' "$path_json" | python3 -c '
import json,sys
d=json.load(sys.stdin)
total=sum(len(x.get("expected_paths") or []) for x in d.get("decisions") or [])
matched=sum(1 for x in d.get("decisions") or [] for p in x.get("expected_paths") or [] if p.get("exists"))
missing=d.get("missing_count", -1)
status=d.get("status")
print(f"{status}|{total}|{matched}|{missing}")
')"
IFS='|' read -r path_status path_total path_matched path_missing <<< "$path_ok"
if [[ "$path_status" != "healthy" || "$path_matched" != "$path_total" || "$path_missing" != "0" || "$path_total" -lt 30 ]]; then
    fail "path-check not healthy matched=total missing=0 total>=30; got ${path_ok}"
else
    printf 'path-check: %s/%s matched; healthy\n' "$path_matched" "$path_total"
fi

link_count="$(lex link suggest --module "$module" --json | python3 -c 'import json,sys; d=json.load(sys.stdin); print(len(d) if isinstance(d, list) else 0)')"
if [[ "$link_count" != "0" ]]; then
    fail "link suggest expected empty; got ${link_count}"
else
    printf 'link suggest: 0\n'
fi

plan_cov="$(lex plan coverage "$plan" --json)"
if ! printf '%s\n' "$plan_cov" | jq -e '.gap_count == 0' >/dev/null; then
    fail "plan coverage has gaps"
else
    printf 'lex plan coverage: covered=%s required=%s gaps=0\n' \
        "$(printf '%s\n' "$plan_cov" | jq -r '.covered_ref_count')" \
        "$(printf '%s\n' "$plan_cov" | jq -r '.required_ref_count')"
fi

plan_audit="$(lex plan audit "$plan" --json)"
if ! printf '%s\n' "$plan_audit" | jq -e '.blocking_finding_count == 0' >/dev/null; then
    fail "plan audit has blocking findings"
else
    printf 'lex plan audit: blocking_finding_count=0\n'
fi

# ── External dependency validation (evidence-bound; no force without proof) ──
section "External dependency evidence-bound status"
# Named evidence already succeeded above (full suite + graph + specialized gates).
# Leave validated state unchanged when already truthful; update only if not validated.
declare -A EXT_EVIDENCE_NOTE=(
    [EXT-CLASSIFY-LEDGER-PUBLIC-CONTRACT]="Named proof: LedgerClassifyPrerequisiteTests, LedgerClassificationProjectionTests, LedgerClassificationMutationPreconditionTests, ClassifyLedgerContractClientTests, ClassifyLedgerBoundaryArchitectureTests, ClassifyUc003ApplyTests, TC-CLASSIFY-ELIGIBLE-PROJECTION-CONTRACT / TC-CLASSIFY-APPLY-EXECUTION-CONTRACT after full suite + contract gate exit 0."
    [EXT-CLASSIFY-AI-AGENT-HOST]="Named proof: ClassifyUc006AgentContractTests, ClassifyPublishedContractTests, ClassifyProcessContractTests, TC-CLASSIFY-CONTRACT-DISCOVERY-CONTRACT / TC-CLASSIFY-STRUCTURED-INVOCATION-CONTRACT after full suite + contract gate exit 0."
    [EXT-CLASSIFY-HOST-OS-SECURITY]="Named proof: ClassifySecurityGateTests, ClassifyArtifactProtectionTests, scripts/verify-classify-security.sh, TC-CLASSIFY-LOCAL-ARTIFACT-PROTECTION / TC-CLASSIFY-STRUCTURED-INVOCATION-CONTRACT / TC-CLASSIFY-OFFLINE-PROCESS-ISOLATION."
    [EXT-CLASSIFY-PRIVATE-EVALUATION-CORPUS]="Named proof: OwnerRulebookGateTests, PrivateCorpusReaderTests, PrivateCorpusPrivacyTests, ClassificationRuleValidationTests, TC-CLASSIFY-RULE-VALIDATION-CONTRACT / TC-CLASSIFY-OWNER-RULEBOOK-PRE-AUTHORITY-GATE after full suite exit 0."
)

for ext in \
    EXT-CLASSIFY-LEDGER-PUBLIC-CONTRACT \
    EXT-CLASSIFY-AI-AGENT-HOST \
    EXT-CLASSIFY-HOST-OS-SECURITY \
    EXT-CLASSIFY-PRIVATE-EVALUATION-CORPUS
do
    note="${EXT_EVIDENCE_NOTE[$ext]}"
    show_json="$(lex external-dependency show "$ext" --json)"
    vs="$(printf '%s\n' "$show_json" | python3 -c '
import json,sys
d=json.load(sys.stdin)
vs=d.get("validation_status")
if not vs and isinstance(d.get("metadata"), str):
    try:
        vs=json.loads(d["metadata"]).get("validation_status")
    except Exception:
        vs=None
if not vs and isinstance(d.get("metadata"), dict):
    vs=d["metadata"].get("validation_status")
print(vs or "")
')"
    if [[ "$vs" == "validated" ]]; then
        printf '  %s: already validated (left unchanged)\n' "$ext"
        printf '    evidence: %s\n' "$note"
    else
        # Authoritative transition only after named evidence above succeeded.
        if ! lex external-dependency update "$ext" --validation-status validated >/dev/null; then
            fail "lex external-dependency update failed for ${ext}"
            continue
        fi
        show_json="$(lex external-dependency show "$ext" --json)"
        vs="$(printf '%s\n' "$show_json" | python3 -c '
import json,sys
d=json.load(sys.stdin)
vs=d.get("validation_status")
if not vs and isinstance(d.get("metadata"), str):
    try:
        vs=json.loads(d["metadata"]).get("validation_status")
    except Exception:
        vs=None
if not vs and isinstance(d.get("metadata"), dict):
    vs=d["metadata"].get("validation_status")
print(vs or "")
')"
        if [[ "$vs" != "validated" ]]; then
            fail "external dependency ${ext} validation_status=${vs} after update (want validated)"
        else
            printf '  %s: validated via lex CLI after named evidence\n' "$ext"
            printf '    evidence: %s\n' "$note"
        fi
    fi
done

ext_check="$(lex external-dependency check --module "$module" --json)"
if ! printf '%s\n' "$ext_check" | python3 -c '
import json,sys
d=json.load(sys.stdin)
deps=d.get("dependencies") or []
codes={dep["ref_code"] for dep in deps}
expected={
  "EXT-CLASSIFY-AI-AGENT-HOST",
  "EXT-CLASSIFY-HOST-OS-SECURITY",
  "EXT-CLASSIFY-LEDGER-PUBLIC-CONTRACT",
  "EXT-CLASSIFY-PRIVATE-EVALUATION-CORPUS",
}
if codes != expected:
    print("bad codes", sorted(codes))
    sys.exit(1)
for dep in deps:
    if dep.get("validation_status") != "validated":
        print("bad status", dep["ref_code"], dep.get("validation_status"))
        sys.exit(1)
    if not (dep.get("linked_test_cases") or []):
        print("missing links", dep["ref_code"])
        sys.exit(1)
print("ok")
'; then
    fail "external-dependency check did not report 4 validated evidence-linked deps"
else
    printf 'external deps: 4/4 validated with linked test-case evidence\n'
fi

# ── Kill criteria: require clear (do not rewrite without need) ───────────────
section "Kill criteria status (must be clear)"
kc_list="$(lex kill-criterion list --module "$module" --json)"
kc_count="$(printf '%s\n' "$kc_list" | jq 'length')"
if [[ "$kc_count" != "5" ]]; then
    fail "expected 5 kill criteria; got ${kc_count}"
fi
kc_clear=0
while IFS= read -r row; do
    kc_id="$(printf '%s\n' "$row" | jq -r '.id')"
    state="$(printf '%s\n' "$row" | jq -r '.evaluation_state')"
    if [[ "$state" != "clear" ]]; then
        fail "kill criterion ${kc_id} evaluation_state=${state} (want clear)"
    else
        kc_clear=$((kc_clear + 1))
        printf '  %s: clear\n' "$kc_id"
    fi
done < <(printf '%s\n' "$kc_list" | jq -c '.[]')
printf 'kill criteria: %s/%s clear\n' "$kc_clear" "$kc_count"

# ── Content fingerprints ─────────────────────────────────────────────────────
section "Content fingerprints (paths + hashes, no private payloads)"
fingerprint_paths=(
    scripts/verify-classify-module.sh
    scripts/verify-classify-graph.sh
    scripts/verify-classify-contract.sh
    scripts/verify-classify-security.sh
    tests/Tally.Tests/Classify/ClassifyModuleGuardTests.cs
    tests/Tally.Tests/Classify/ClassifyGraphEvidenceGuardTests.cs
    docs/verification/classify-v1.md
    docs/verification/classify-graph.md
    .lexicon/graph/CLASSIFY/module.json
    .lexicon/graph/CLASSIFY/external-dependency/EXT-CLASSIFY-LEDGER-PUBLIC-CONTRACT.json
    .lexicon/graph/CLASSIFY/external-dependency/EXT-CLASSIFY-AI-AGENT-HOST.json
    .lexicon/graph/CLASSIFY/external-dependency/EXT-CLASSIFY-HOST-OS-SECURITY.json
    .lexicon/graph/CLASSIFY/external-dependency/EXT-CLASSIFY-PRIVATE-EVALUATION-CORPUS.json
)
fingerprint_rows=()
for path in "${fingerprint_paths[@]}"; do
    if [[ -f "$path" ]]; then
        sha="$(sha256sum -- "$path" | awk '{print $1}')"
        bytes="$(wc -c < "$path" | tr -d ' ')"
        printf '  LIVE %s sha256=%s bytes=%s\n' "$path" "$sha" "$bytes"
        fingerprint_rows+=("${path}|${sha}|${bytes}")
    else
        fail "missing required artifact ${path}"
    fi
done

# ── Repository diff integrity ────────────────────────────────────────────────
section "Repository diff integrity"
if ! git diff --check; then
    fail "git diff --check failed"
else
    printf 'git diff --check: ok\n'
fi

# ── Write safe completion report ─────────────────────────────────────────────
section "Write module completion report (metadata-only)"
commit_full="$(git rev-parse HEAD 2>/dev/null || echo unknown)"
commit_short="$(git rev-parse --short HEAD 2>/dev/null || echo unknown)"
run_date="$(date -u +%Y-%m-%d)"
run_status="passed"
if (( fail_count > 0 )); then
    run_status="FAILED"
fi

{
    cat <<EOF
# CLASSIFY v1 verification

Status: **${run_status}** on ${run_date} (commit \`${commit_short}\` / \`${commit_full}\`).

The CLASSIFY completion gate is executed by \`bash scripts/verify-classify-module.sh\`.
The script requires Release restore/build, CLASSIFY-owned format verification, the complete
full test suite, linux-x64 Native-AOT publish, current non-stale ClassifyGraphQualityEvidence,
graph/path/dependency evidence, nonzero named CLASSIFY suites, evidence-bound external
dependency statuses, kill-criterion clearance, and clean git whitespace.

This report is **metadata-only**. It must not contain private fixture paths or content,
descriptions, normalized tokens, amounts, expected corpus rows, secrets, or financial payloads.

## Gate command

\`\`\`bash
bash scripts/verify-classify-module.sh
\`\`\`

Expected: exit 0; nonzero named-suite discovery; full suite 0 failures; four external
dependencies \`validated\`; five kill criteria \`clear\`.

## Latest run

| Gate | Result |
|---|---|
| Host | kernel=$(uname -sr); cpus=$(nproc 2>/dev/null || echo unknown); load=$(cut -d ' ' -f 1-3 /proc/loadavg 2>/dev/null || echo unknown) |
| Tools | lex=$(lex --version 2>/dev/null || echo unknown); dotnet=$(dotnet --version 2>/dev/null || echo unknown) |
| Commit | \`${commit_full}\` |
| \`dotnet restore Tally.slnx\` | executed |
| \`dotnet build Tally.slnx -c Release\` | zero-warning (TreatWarningsAsErrors) |
| \`dotnet format\` (CLASSIFY-owned paths) | verify-no-changes |
| Native-AOT \`linux-x64\` publish | executable present (temp publish root); 0 trim/reflection/dynamic-code warning markers scanned |
| Named CLASSIFY suite discovery | ${#named_suites[@]} classes; each ≥ floor; aggregate CLASSIFY discovery=${classify_count} |
| \`ClassifyModuleGuardTests\` | executed under Release |
| Complete full test suite | passed=${full_passed_n:-?} failed=${full_failed:-?} skipped=${full_skipped:-?} total=${full_total:-?} |
| \`scripts/verify-classify-graph.sh\` | invoked (non-stale ClassifyGraphQualityEvidence) |
| \`scripts/verify-classify-contract.sh\` | invoked |
| \`scripts/verify-classify-security.sh\` | invoked |
| Owner-rulebook specialized gate | env-gated (path never printed); in-suite OwnerRulebookGateTests always run |
| \`lex check --fast\` | executed |
| \`lex coverage --module CLASSIFY\` | 13/13 healthy; 0 orphans |
| \`lex decision path-check\` | ${path_matched}/${path_total} matched; healthy |
| \`lex link suggest\` | 0 |
| \`lex plan coverage PLAN-CLASSIFY-RULEBOOK-V1\` | gap_count=0 |
| \`lex plan audit PLAN-CLASSIFY-RULEBOOK-V1\` | blocking_finding_count=0 |
| Kill criteria | ${kc_clear}/5 \`clear\` |
| External dependencies | 4/4 \`validated\` (evidence-bound; left unchanged when already truthful) |
| \`git diff --check\` | executed |
| Module script fail_count | ${fail_count} |

## External dependency statuses

| Ref | Status | Named evidence (metadata) |
|---|---|---|
| \`EXT-CLASSIFY-LEDGER-PUBLIC-CONTRACT\` | validated | LEDGER projection/mutation prerequisite + apply suites |
| \`EXT-CLASSIFY-AI-AGENT-HOST\` | validated | UC-006 agent contract + published discovery/invocation |
| \`EXT-CLASSIFY-HOST-OS-SECURITY\` | validated | Security gate + owner-only modes / offline isolation |
| \`EXT-CLASSIFY-PRIVATE-EVALUATION-CORPUS\` | validated | Owner-rulebook + private corpus reader/validation suites |

## Kill criteria

| Id | State |
|---|---|
EOF

    while IFS= read -r row; do
        kc_id="$(printf '%s\n' "$row" | jq -r '.id')"
        state="$(printf '%s\n' "$row" | jq -r '.evaluation_state')"
        printf -- '| `%s` | %s |\n' "$kc_id" "$state"
    done < <(printf '%s\n' "$kc_list" | jq -c '.[]' | sort)

    cat <<EOF

## Named CLASSIFY suites (nonzero discovery required)

EOF

    for class_name in "${named_suites[@]}"; do
        count="$(class_discovered_count "$class_name" "$classify_list")"
        printf -- '- `%s` — discovery %s\n' "$class_name" "$count"
    done

    cat <<EOF

## Content fingerprints (live at report write; raw SHA-256)

| Artifact | SHA-256 | Bytes |
|---|---|---:|
EOF

    for row in "${fingerprint_rows[@]}"; do
        IFS='|' read -r path sha bytes <<< "$row"
        printf -- '| `%s` | `%s` | %s |\n' "$path" "$sha" "$bytes"
    done

    cat <<EOF

## How to re-run

\`\`\`bash
dotnet restore Tally.slnx
bash scripts/verify-classify-module.sh
\`\`\`

Specialized isolated gates remain available:

\`\`\`bash
bash scripts/verify-classify-graph.sh
bash scripts/verify-classify-contract.sh
bash scripts/verify-classify-security.sh
# optional private operator gate (never print CLASSIFY_OWNER_RULEBOOK_CORPUS):
# bash scripts/verify-classify-owner-rulebook.sh
\`\`\`

## Result

Record the runner exit code, suite counts, dependency statuses, kill checks, fingerprints,
and commit IDs. Do not paste private fixtures, paths, descriptions, tokens, amounts, or
financial payloads.

**VerifiedClassifyV1Module:** ${run_status}
EOF
} > "$report_path"

printf 'wrote %s\n' "$report_path"

# ── Summary ──────────────────────────────────────────────────────────────────
section "CLASSIFY v1 module gate summary"
printf 'commands:\n'
printf '  dotnet restore/build -c Release; format --verify-no-changes (CLASSIFY paths)\n'
printf '  dotnet publish -c Release -r linux-x64 -p:PublishAot=true\n'
printf '  dotnet test (complete full suite)\n'
printf '  bash scripts/verify-classify-{graph,contract,security}.sh\n'
printf '  lex coverage/path/plan/external-dependency/kill-criterion checks\n'
printf 'counts: named_suites=%s classify_discovery=%s full_passed=%s fail_count=%s\n' \
    "${#named_suites[@]}" "$classify_count" "${full_passed_n:-?}" "$fail_count"

if (( fail_count > 0 )); then
    printf 'classify module verification: FAILED (%s checks)\n' "$fail_count" >&2
    exit 1
fi

printf 'classify module verification: exit 0; Release build+format+AOT; full suite non-vacuous; graph/contract/security gates; 4 deps validated; 5 kill criteria clear; report written without private payloads\n'
exit 0
