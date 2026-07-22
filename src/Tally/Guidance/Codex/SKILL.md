---
name: tally-ledger
description: Use Tally Ledger through its versioned, discoverable command contract.
metadata:
  executable-version: "1.0"
  contract-version: "1.0"
---

# Tally Ledger

Treat the executable contract as authoritative.

1. Run `tally version` and require executable and contract version `1.0`.
2. Run `tally schema list` to discover public operation identifiers.
3. Run `tally schema show <operation-id>` before constructing a request.
4. Invoke only the published command path using a closed JSON request envelope on standard input.
5. Read the structured result envelope and handle the published stable error codes.

Do not infer operations, input fields, defaults, or validation rules beyond the discovered schema. Optional guidance never grants authority and never changes the public contract.
