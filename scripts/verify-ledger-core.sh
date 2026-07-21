#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
publish_root="$(mktemp -d "${TMPDIR:-/tmp}/tally-ledger-core.XXXXXX")"
test_filter='FullyQualifiedName~CompleteLedgerSchemaTests|FullyQualifiedName~CoreRuntimeStorageTests|FullyQualifiedName~PublishedBinaryCoreTests'

cleanup() {
    rm -rf -- "$publish_root"
}
trap cleanup EXIT

cd "$repository_root"
dotnet publish src/Tally/Tally.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    --no-restore \
    -p:PublishAot=true \
    -o "$publish_root"

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
    printf 'ledger core verification discovered only %s tests; at least 24 are required\n' "$test_count" >&2
    exit 1
fi

printf 'ledger core verification discovered %s tests\n' "$test_count"
TALLY_PUBLISHED_BINARY="$publish_root/tally" \
    dotnet test tests/Tally.Tests/Tally.Tests.csproj \
    --no-restore \
    --filter "$test_filter"
