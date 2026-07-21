using System.Text.Json;
using Tally.Domain.Ledger;

namespace Tally.Contracts.Common;

public sealed record IdempotencyRequest(
    string ContractVersion,
    string OperationId,
    string CallerKey,
    string Actor,
    JsonElement Input,
    LogicalEffectIdentity? LogicalEffect);
