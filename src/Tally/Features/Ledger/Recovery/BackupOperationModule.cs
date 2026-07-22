using System.Text.Json;
using System.Runtime.Versioning;
using Tally.Application;
using Tally.Cli;
using Tally.Contracts.Ledger.Recovery;
using Tally.Contracts.System;
using Tally.Infrastructure.Recovery;

namespace Tally.Features.Ledger.Recovery;

[SupportedOSPlatform("linux")]
public sealed class BackupOperationModule(BackupService service)
{
    public const string CreateOperationId = "ledger.backup.create";
    public const string VerifyOperationId = "ledger.backup.verify";

    public IReadOnlyList<OperationDescriptor> Descriptors { get; } =
    [
        new(
            CreateOperationId,
            "tally ledger backup create",
            "mutation",
            true,
            BackupJsonContext.Default.CreateBackupInput,
            BackupJsonContext.Default.BackupReceipt,
            "BackupOperationModule.Create",
            (_, _) => new BackupOperationHandler(service, CreateOperationId),
            "tally ledger backup create --input -",
            Errors),
        new(
            VerifyOperationId,
            "tally ledger backup verify",
            "query",
            false,
            BackupJsonContext.Default.VerifyBackupInput,
            BackupJsonContext.Default.BackupReceipt,
            "BackupOperationModule.Verify",
            (_, _) => new BackupOperationHandler(service, VerifyOperationId),
            "tally ledger backup verify --input -",
            Errors)
    ];

    public Task<CommandResult<JsonElement>> HandleAsync(
        string operationId,
        OperationRequest request,
        CancellationToken cancellationToken) => DispatchAsync(service, operationId, request, cancellationToken);

    internal static async Task<CommandResult<JsonElement>> DispatchAsync(
        BackupService backupService,
        string operationId,
        OperationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return operationId switch
            {
                CreateOperationId => JsonSerializer.Deserialize(request.Input, BackupJsonContext.Default.CreateBackupInput) is { } input
                    ? await backupService.CreateAsync(input, request.Actor, request.IdempotencyKey, cancellationToken)
                    : CommandResult<JsonElement>.Failure(BackupErrors.Invalid),
                VerifyOperationId => JsonSerializer.Deserialize(request.Input, BackupJsonContext.Default.VerifyBackupInput) is { } input
                    ? await backupService.VerifyAsync(input, cancellationToken)
                    : CommandResult<JsonElement>.Failure(BackupErrors.Invalid),
                _ => CommandResult<JsonElement>.Failure("operation.not_found")
            };
        }
        catch (JsonException)
        {
            return CommandResult<JsonElement>.Failure(BackupErrors.Invalid);
        }
    }

    private static readonly IReadOnlyList<ErrorSchema> Errors =
    [
        new(BackupErrors.Invalid, "validation", 3),
        new(BackupErrors.NotFound, "not_found", 4),
        new(BackupErrors.TargetExists, "conflict", 5),
        new(LedgerMutationExecutor.ConflictCode, "conflict", 5),
        new(BackupErrors.Busy, "conflict", 5),
        new(BackupErrors.ChecksumMismatch, "integrity", 8),
        new(BackupErrors.Integrity, "integrity", 8),
        new(BackupErrors.Incompatible, "compatibility", 7),
        new(BackupErrors.HostProtection, "host", 9),
        new(BackupErrors.Permission, "host", 9),
        new(BackupErrors.Disk, "host", 9)
    ];
}

[SupportedOSPlatform("linux")]
internal sealed class BackupOperationHandler(BackupService service, string operationId) : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken) =>
        BackupOperationModule.DispatchAsync(service, operationId, request, cancellationToken);
}
