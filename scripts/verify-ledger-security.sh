#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"

check_host_protection() {
    if [[ "$(uname -s)" != "Linux" ]]; then
        printf 'host-protection.host_unsupported\n' >&2
        return 1
    fi

    local evidence_path="${TALLY_HOST_PROTECTION_EVIDENCE_FILE:-}"
    local data_root="${TALLY_DATA_ROOT:-}"
    if [[ -z "$evidence_path" || -z "$data_root" ]]; then
        printf 'host-protection.evidence_missing\n' >&2
        return 1
    fi
    if [[ ! -f "$evidence_path" || -L "$evidence_path" ]]; then
        printf 'host-protection.evidence_invalid\n' >&2
        return 1
    fi
    if [[ "$(stat -c '%a' -- "$evidence_path")" != "600" || "$(stat -c '%u' -- "$evidence_path")" != "$(id -u)" ]]; then
        printf 'host-protection.evidence_permissions\n' >&2
        return 1
    fi

    local declared_root
    if ! jq -e '
        .schemaVersion == 1
        and .protection == "host-managed-encrypted-volume"
        and (.verifiedBy | type == "string" and length > 0)
    ' "$evidence_path" >/dev/null 2>&1; then
        printf 'host-protection.evidence_invalid\n' >&2
        return 1
    fi
    declared_root="$(jq -er '.dataRoot | select(type == "string" and startswith("/"))' "$evidence_path" 2>/dev/null)" || {
        printf 'host-protection.evidence_invalid\n' >&2
        return 1
    }
    if [[ "$declared_root" != "$(realpath -m -- "$data_root")" ]]; then
        printf 'host-protection.evidence_scope_mismatch\n' >&2
        return 1
    fi

    printf 'host-protection: verified host-managed encrypted volume for configured data root\n'
}

if [[ "${1:-}" == "--check-host-protection" ]]; then
    if (( $# != 1 )); then
        printf 'host-protection.usage_invalid\n' >&2
        exit 2
    fi
    check_host_protection
    exit
fi
if (( $# != 0 )); then
    printf 'ledger security verification accepts only --check-host-protection\n' >&2
    exit 2
fi

if [[ "${TALLY_REQUIRE_HOST_PROTECTION_EVIDENCE:-0}" == "1" ]]; then
    check_host_protection
else
    filesystem_type="$(findmnt -n -o FSTYPE -T "$repository_root" 2>/dev/null || printf 'unknown')"
    printf 'host-protection: deployment evidence not asserted; filesystem=%s is not accepted alone as encryption evidence\n' "$filesystem_type"
fi

publish_root="$(mktemp -d "${TMPDIR:-/tmp}/tally-ledger-security.XXXXXX")"
cleanup() {
    rm -rf -- "$publish_root"
}
trap cleanup EXIT

cd "$repository_root"
dotnet build Tally.slnx --no-restore
dotnet publish src/Tally/Tally.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    --no-restore \
    -p:PublishAot=true \
    -o "$publish_root"

test_filter='FullyQualifiedName~Tally.Tests.Security|FullyQualifiedName~BackupTests|FullyQualifiedName~RestoreTests|FullyQualifiedName~StorageEvolutionTests|FullyQualifiedName~DurableLedgerVerifierTests|FullyQualifiedName~MigrationCandidateBuilderTests|FullyQualifiedName~AuthoritativeStoreActivatorTests|FullyQualifiedName~EvidenceRegistryOperationTests|FullyQualifiedName~EvidenceLinkOperationTests|FullyQualifiedName~StatementAuthoritativeCorrectionTests|FullyQualifiedName~ReconciliationCoverageOperationTests|FullyQualifiedName~GuidanceCompatibilityTests|FullyQualifiedName~CliContractTests|FullyQualifiedName~PublishedBinaryCoreTests|FullyQualifiedName~PublishedLedgerContractTests'
test_list="$(dotnet test tests/Tally.Tests/Tally.Tests.csproj \
    --list-tests \
    --no-restore \
    --filter "$test_filter")"
test_count="$(printf '%s\n' "$test_list" | awk '
    /The following Tests are available:/ { listing = 1; next }
    listing && NF { count++ }
    END { print count + 0 }
')"
if (( test_count < 24 )); then
    printf 'ledger security verification discovered only %s tests; at least 24 are required\n' "$test_count" >&2
    exit 1
fi

printf 'ledger security verification discovered %s tests\n' "$test_count"
TALLY_PUBLISHED_BINARY="$publish_root/tally" \
    dotnet test tests/Tally.Tests/Tally.Tests.csproj \
    --no-restore \
    --filter "$test_filter"
