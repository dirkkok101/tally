using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Bootstrap;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Dimensions;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Reconciliation;
using Tally.Contracts.Ledger.Relationships;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Accounts;
using Tally.Domain.Ledger.Evidence;
using Tally.Domain.Ledger.Reconciliation;
using Tally.Domain.Ledger.Relationships;
using Tally.Domain.Ledger.Transactions;
using Tally.Features.Ledger.Reconciliation;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Accounts;
using Tally.Infrastructure.Storage.Categories;
using Tally.Infrastructure.Storage.Dimensions;
using Tally.Infrastructure.Storage.Evidence;
using Tally.Infrastructure.Storage.Reconciliation;
using Tally.Infrastructure.Storage.Relationships;
using Tally.Infrastructure.Storage.Transactions;
using Xunit;

namespace Tally.Tests.Integration;

[SupportedOSPlatform("linux")]
public sealed class ReconciliationOperationBundleTests
{
    private static readonly string[] ExpectedOperationIds =
    [
        "ledger.reconciliation.apply",
        "ledger.reconciliation.candidates",
        "ledger.reconciliation.coverage.complete",
        "ledger.reconciliation.coverage.get",
        "ledger.reconciliation.decision.confirm",
        "ledger.reconciliation.decision.get",
        "ledger.reconciliation.decision.reject",
        "ledger.reconciliation.decision.replace",
        "ledger.reconciliation.decision.revoke",
        "ledger.reconciliation.scope.register"
    ];

    [Theory]
    [MemberData(nameof(OperationContracts))]
    public void DD_LEDGER_CLI_OPERATION_CONTRACT_exposes_each_typed_operation_once(
        string operationId,
        Type requestType,
        Type resultType,
        string kind,
        bool requiresIdempotencyKey)
    {
        var descriptor = Assert.Single(CreateBundle().Descriptors, candidate => candidate.OperationId == operationId);

        Assert.Equal(requestType, descriptor.RequestTypeInfo.Type);
        Assert.Equal(resultType, descriptor.ResultTypeInfo.Type);
        Assert.Equal(kind, descriptor.Kind);
        Assert.Equal(requiresIdempotencyKey, descriptor.RequiresIdempotencyKey);
    }

    [Theory]
    [MemberData(nameof(OperationIds))]
    public async Task DD_LEDGER_CLI_OPERATION_CONTRACT_dispatches_each_descriptor_to_a_closed_typed_handler(
        string operationId)
    {
        var descriptor = Assert.Single(CreateBundle().Descriptors, candidate => candidate.OperationId == operationId);
        var handler = descriptor.HandlerFactory(LedgerServices.Create(), OperationRegistry.Create());

        var result = await handler.HandleAsync(
            new(JsonDocument.Parse("{}").RootElement.Clone(), null, null),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("validation.invalid_input", result.ErrorCode);
    }

    [Fact]
    public void DD_LEDGER_CLI_OPERATION_CONTRACT_inventory_is_exact_unique_and_ordinal()
    {
        var descriptors = CreateBundle().Descriptors;

        Assert.Equal(ExpectedOperationIds, descriptors.Select(descriptor => descriptor.OperationId));
        Assert.Equal(10, descriptors.Select(descriptor => descriptor.OperationId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(10, descriptors.Select(descriptor => descriptor.CliPath).Distinct(StringComparer.Ordinal).Count());
        Assert.All(descriptors, descriptor => Assert.StartsWith("Reconciliation", descriptor.HandlerTarget, StringComparison.Ordinal));
    }

    [Fact]
    public void DD_LEDGER_CLI_OPERATION_CONTRACT_schema_inventory_is_closed_and_discovers_statement_correction()
    {
        var schemas = CreateBundle().Descriptors.Select(descriptor => descriptor.ToSchema()).ToArray();
        var json = JsonSerializer.Serialize(schemas, LedgerJsonContext.Default.OperationSchemaArray);

        Assert.Contains("correct_existing_from_statement", json, StringComparison.Ordinal);
        Assert.DoesNotContain(typeof(JsonElement).FullName!, json, StringComparison.Ordinal);
        Assert.All(schemas, schema =>
        {
            Assert.Equal("1.0", schema.MinimumContractVersion);
            Assert.Equal("1.0", schema.MaximumContractVersion);
            Assert.Contains("\"additionalProperties\":false", schema.RequestSchema, StringComparison.Ordinal);
            Assert.NotEmpty(schema.Errors);
            Assert.Equal(
                schema.Errors.Count,
                schema.Errors.Select(error => error.Code).Distinct(StringComparer.Ordinal).Count());
        });
    }

    [Theory]
    [InlineData("agentmail")]
    [InlineData("mailbox")]
    [InlineData("mime")]
    [InlineData("whatsapp")]
    [InlineData("recipient")]
    [InlineData("rawpayload")]
    [InlineData("deliveryretry")]
    public void NFR_LEDGER_LOCAL_PRIVACY_schema_inventory_is_provider_and_transport_neutral(string forbidden)
    {
        var json = JsonSerializer.Serialize(
            CreateBundle().Descriptors.Select(descriptor => descriptor.ToSchema()).ToArray(),
            LedgerJsonContext.Default.OperationSchemaArray);

        Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_bundle_projects_exact_and_differing_candidates_then_confirms_once()
    {
        await using var seam = await ReconciliationSeam.CreateAsync();
        var accountId = await seam.SeedAccountAsync();
        var exact = await seam.SeedTransactionAsync(accountId, -1234, "2026-07-10");
        var differing = await seam.SeedTransactionAsync(accountId, -1300, "2026-07-10");
        var statement = await seam.SeedStatementAsync(accountId, -1234, "2026-07-10");

        var projection = await seam.ProjectAsync(statement);

        Assert.Equal(exact, Assert.Single(projection.ExactCandidates).TransactionId);
        Assert.Equal(differing, Assert.Single(projection.GuardCandidates).TransactionId);
        var applied = await seam.ApplyAsync(
            statement,
            projection,
            ReconciliationApplyDisposition.MatchExisting,
            exact,
            statementFact: null,
            key: "exact-match");
        var detail = await seam.GetDecisionAsync(statement.EvidenceId);
        Assert.Equal(exact, applied.ActiveTransactionId);
        Assert.Equal(exact, detail.ActiveTransactionId);
        Assert.Equal(ReconciliationDecisionCurrentState.OwnerConfirmedMatch, detail.CurrentState);
        Assert.Single(detail.History);
    }

    [Fact]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_bundle_applies_the_activated_exact_automatic_policy()
    {
        await using var seam = await ReconciliationSeam.CreateAsync();
        var accountId = await seam.SeedAccountAsync();
        var target = await seam.SeedTransactionAsync(accountId, -1234, "2026-07-10");
        var statement = await seam.SeedStatementAsync(accountId, -1234, "2026-07-10");
        var projection = await seam.ProjectAsync(statement);

        var applied = await seam.ApplyAsync(
            statement,
            projection,
            ReconciliationApplyDisposition.MatchExisting,
            target,
            statementFact: null,
            key: "automatic-exact",
            authorityKind: ReconciliationAuthorityKind.DeterministicPolicy);

        Assert.Equal(ReconciliationPolicyV1.PolicyId, applied.PolicyId);
        Assert.Equal(ReconciliationPolicyV1.PolicyVersion, applied.PolicyVersion);
        Assert.Equal(ReconciliationPolicyV1.ExactUniqueCandidateReason, applied.Reason);
        Assert.Equal(ReconciliationDecisionCurrentState.ConfirmedExisting, (await seam.GetDecisionAsync(statement.EvidenceId)).CurrentState);
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_bundle_corrects_owner_choice_and_reports_coverage_history()
    {
        await using var seam = await ReconciliationSeam.CreateAsync();
        var accountId = await seam.SeedAccountAsync();
        var first = await seam.SeedTransactionAsync(accountId, -1234, "2026-07-10");
        var second = await seam.SeedTransactionAsync(accountId, -1234, "2026-07-10");
        var statement = await seam.SeedStatementAsync(accountId, -1234, "2026-07-10");
        var projection = await seam.ProjectAsync(statement);
        Assert.Equal(ReconciliationProjectionOutcome.Ambiguous, projection.Outcome);

        var ambiguous = await seam.ApplyAsync(
            statement,
            projection,
            ReconciliationApplyDisposition.RecordAmbiguous,
            targetTransactionId: null,
            statementFact: null,
            key: "ambiguous");
        var confirmed = await seam.DispatchSuccessAsync(
            "ledger.reconciliation.decision.confirm",
            new ConfirmReconciliationDecisionInput(
                statement.EvidenceId,
                statement.ScopeId,
                ambiguous.DecisionId,
                first,
                ReconciliationAuthorityKind.Owner,
                "owner selected first candidate"),
            ReconciliationDecisionJsonContext.Default.ConfirmReconciliationDecisionInput,
            ReconciliationDecisionJsonContext.Default.ReconciliationDecisionMutationResult,
            "confirm-first");
        var replaced = await seam.DispatchSuccessAsync(
            "ledger.reconciliation.decision.replace",
            new ReplaceReconciliationDecisionInput(
                statement.EvidenceId,
                statement.ScopeId,
                confirmed.DecisionId,
                second,
                ReconciliationAuthorityKind.Owner,
                "owner corrected candidate selection"),
            ReconciliationDecisionJsonContext.Default.ReplaceReconciliationDecisionInput,
            ReconciliationDecisionJsonContext.Default.ReconciliationDecisionMutationResult,
            "replace-second");

        Assert.Equal(first, replaced.PriorTransactionId);
        Assert.Equal(second, replaced.ActiveTransactionId);
        var completed = await seam.CompleteCoverageAsync(statement, "complete-corrected-choice");
        var queried = await seam.GetCoverageAsync(statement.ScopeId);
        var decision = await seam.GetDecisionAsync(statement.EvidenceId);
        Assert.Equal(3, decision.History.Count);
        Assert.Equal(second, decision.ActiveTransactionId);
        Assert.Contains(queried.CurrentMembers, member =>
            member.StableId == first && member.Outcome == StatementCoverageOutcome.RecordedAbsentFromStatement);
        Assert.Contains(queried.CurrentMembers, member =>
            member.StableId == second && member.Outcome == StatementCoverageOutcome.StatementReconciled);
        Assert.Equal(
            JsonSerializer.Serialize(completed, ReconciliationCoverageJsonContext.Default.StatementCoverageSummary),
            JsonSerializer.Serialize(queried, ReconciliationCoverageJsonContext.Default.StatementCoverageSummary));
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_bundle_statement_only_replay_has_one_canonical_effect()
    {
        await using var seam = await ReconciliationSeam.CreateAsync();
        var accountId = await seam.SeedAccountAsync();
        var statement = await seam.SeedStatementAsync(accountId, -4321, "2026-07-11");
        var projection = await seam.ProjectAsync(statement);
        Assert.Equal(ReconciliationProjectionOutcome.NoCandidate, projection.Outcome);

        var first = await seam.ApplyAsync(
            statement,
            projection,
            ReconciliationApplyDisposition.CreateStatementOnly,
            targetTransactionId: null,
            statement.Fact,
            "statement-only");
        var replay = await seam.ApplyAsync(
            statement,
            projection,
            ReconciliationApplyDisposition.CreateStatementOnly,
            targetTransactionId: null,
            statement.Fact,
            "statement-only");

        Assert.Equal(first.DecisionId, replay.DecisionId);
        Assert.Equal(first.ActiveTransactionId, replay.ActiveTransactionId);
        Assert.Equal(1, await seam.CountAsync(
            "SELECT COUNT(*) FROM transaction_fact WHERE transaction_id = $id;",
            ("$id", first.ActiveTransactionId!)));
        Assert.Single((await seam.GetDecisionAsync(statement.EvidenceId)).History);
    }

    [Fact]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_bundle_applies_authoritative_correction_and_replays_exact_history()
    {
        await using var seam = await ReconciliationSeam.CreateAsync();
        var accountId = await seam.SeedAccountAsync();
        var prior = await seam.SeedTransactionAsync(accountId, -1200, "2026-07-12");
        var statement = await seam.SeedStatementAsync(accountId, -1234, "2026-07-12");
        var projection = await seam.ProjectAsync(statement);
        Assert.Equal(prior, Assert.Single(projection.GuardCandidates).TransactionId);

        var first = await seam.ApplyAsync(
            statement,
            projection,
            ReconciliationApplyDisposition.CorrectExistingFromStatement,
            prior,
            statement.Fact,
            "statement-correction");
        var replay = await seam.ApplyAsync(
            statement,
            projection,
            ReconciliationApplyDisposition.CorrectExistingFromStatement,
            prior,
            statement.Fact,
            "statement-correction");
        var decision = await seam.GetDecisionAsync(statement.EvidenceId);

        Assert.Equal(first.DecisionId, replay.DecisionId);
        Assert.NotNull(first.Correction);
        Assert.Equal(prior, first.Correction.PriorTransactionId);
        Assert.Equal(first.ActiveTransactionId, first.Correction.ReplacementTransactionId);
        Assert.Equal(ReconciliationDecisionCurrentState.CorrectedFromStatement, decision.CurrentState);
        Assert.Single(decision.History);
        Assert.Equal(1, await seam.CountAsync("SELECT COUNT(*) FROM statement_correction;"));
        Assert.Equal(1, await seam.CountAsync("SELECT COUNT(*) FROM transaction_lifecycle_event WHERE action = 'statement_authoritative_replacement';"));
    }

    [Theory]
    [InlineData("transfer")]
    [InlineData("refund")]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_bundle_rebinds_compatible_relationships(string relationshipKind)
    {
        await using var seam = await ReconciliationSeam.CreateAsync();
        var accountId = await seam.SeedAccountAsync();
        var counterpartAccountId = relationshipKind == "transfer"
            ? await seam.SeedAccountAsync()
            : accountId;
        var prior = await seam.SeedTransactionAsync(accountId, -1234, "2026-07-10");
        var counterpart = await seam.SeedTransactionAsync(counterpartAccountId, 1234, "2026-07-10");
        await seam.ConfirmRelationshipAsync(relationshipKind, prior, counterpart);
        var statement = await seam.SeedStatementAsync(accountId, -1234, "2026-07-10");
        var projection = await seam.ProjectAsync(statement);

        var applied = await seam.ApplyAsync(
            statement,
            projection,
            ReconciliationApplyDisposition.CorrectExistingFromStatement,
            prior,
            statement.Fact,
            "compatible-" + relationshipKind);

        Assert.Single(applied.Correction!.RelationshipLifecycleEventIds);
        Assert.Equal(2, await seam.CountAsync("SELECT COUNT(*) FROM financial_relationship;"));
        Assert.Equal(1, await seam.CountAsync("SELECT COUNT(*) FROM financial_relationship_current WHERE state = 'active';"));
        Assert.Equal(1, await seam.CountAsync(
            "SELECT COUNT(*) FROM financial_relationship_current WHERE state = 'active' AND (source_transaction_id = $id OR target_transaction_id = $id);",
            ("$id", applied.ActiveTransactionId!)));
    }

    [Theory]
    [InlineData("transfer")]
    [InlineData("refund")]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_bundle_rejects_incompatible_relationship_correction_atomically(string relationshipKind)
    {
        await using var seam = await ReconciliationSeam.CreateAsync();
        var accountId = await seam.SeedAccountAsync();
        var counterpartAccountId = relationshipKind == "transfer"
            ? await seam.SeedAccountAsync()
            : accountId;
        var prior = await seam.SeedTransactionAsync(accountId, -1234, "2026-07-10");
        var counterpart = await seam.SeedTransactionAsync(counterpartAccountId, 1234, "2026-07-10");
        await seam.ConfirmRelationshipAsync(relationshipKind, prior, counterpart);
        var statement = await seam.SeedStatementAsync(accountId, -1300, "2026-07-10");
        var projection = await seam.ProjectAsync(statement);
        var before = await seam.EffectCountsAsync();

        var result = await seam.ApplyRawAsync(
            statement,
            projection,
            ReconciliationApplyDisposition.CorrectExistingFromStatement,
            prior,
            statement.Fact,
            "incompatible-" + relationshipKind);

        Assert.False(result.IsSuccess);
        Assert.Equal(ReconciliationApplyErrors.ReviewRequired, result.ErrorCode);
        Assert.Equal(before, await seam.EffectCountsAsync());
        Assert.Equal(1, await seam.CountAsync("SELECT COUNT(*) FROM financial_relationship_current WHERE state = 'active';"));
    }

    [Fact]
    public async Task TC_LEDGER_RECONCILIATION_CRASH_ATOMICITY_bundle_retry_after_injected_writer_abort_has_one_effect()
    {
        await using var seam = await ReconciliationSeam.CreateAsync();
        var accountId = await seam.SeedAccountAsync();
        var target = await seam.SeedTransactionAsync(accountId, -1234, "2026-07-10");
        var statement = await seam.SeedStatementAsync(accountId, -1234, "2026-07-10");
        var projection = await seam.ProjectAsync(statement);
        var before = await seam.EffectCountsAsync();
        await seam.ExecuteDatabaseAsync("""
            CREATE TRIGGER bundle_injected_abort
            BEFORE INSERT ON reconciliation_decision_authority
            BEGIN
                SELECT RAISE(ABORT, 'bundle injected abort');
            END;
            """);

        var exception = await Assert.ThrowsAsync<SqliteException>(() => seam.ApplyRawAsync(
            statement,
            projection,
            ReconciliationApplyDisposition.MatchExisting,
            target,
            statementFact: null,
            "crash-retry"));
        Assert.Contains("bundle injected abort", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, await seam.EffectCountsAsync());

        await seam.ExecuteDatabaseAsync("DROP TRIGGER bundle_injected_abort;");
        var recovered = await seam.ApplyAsync(
            statement,
            projection,
            ReconciliationApplyDisposition.MatchExisting,
            target,
            statementFact: null,
            "crash-retry");
        var replay = await seam.ApplyAsync(
            statement,
            projection,
            ReconciliationApplyDisposition.MatchExisting,
            target,
            statementFact: null,
            "crash-retry");

        Assert.Equal(recovered.DecisionId, replay.DecisionId);
        Assert.Equal(1, await seam.CountAsync("SELECT COUNT(*) FROM reconciliation_decision;"));
        Assert.Equal(1, await seam.CountAsync("SELECT COUNT(*) FROM evidence_link_event WHERE role = 'confirming' AND action = 'link';"));
    }

    public static TheoryData<string, Type, Type, string, bool> OperationContracts => new()
    {
        { "ledger.reconciliation.candidates", typeof(GetReconciliationCandidatesInput), typeof(ReconciliationProjectionResult), "query", false },
        { "ledger.reconciliation.apply", typeof(ReconciliationApplyInput), typeof(ReconciliationApplyResult), "mutation", true },
        { "ledger.reconciliation.decision.get", typeof(GetReconciliationDecisionInput), typeof(ReconciliationDecisionDetail), "query", false },
        { "ledger.reconciliation.decision.confirm", typeof(ConfirmReconciliationDecisionInput), typeof(ReconciliationDecisionMutationResult), "mutation", true },
        { "ledger.reconciliation.decision.reject", typeof(RejectReconciliationDecisionInput), typeof(ReconciliationDecisionMutationResult), "mutation", true },
        { "ledger.reconciliation.decision.revoke", typeof(RevokeReconciliationDecisionInput), typeof(ReconciliationDecisionMutationResult), "mutation", true },
        { "ledger.reconciliation.decision.replace", typeof(ReplaceReconciliationDecisionInput), typeof(ReconciliationDecisionMutationResult), "mutation", true },
        { "ledger.reconciliation.coverage.complete", typeof(CompleteStatementCoverageInput), typeof(StatementCoverageSummary), "mutation", true },
        { "ledger.reconciliation.coverage.get", typeof(GetStatementCoverageInput), typeof(StatementCoverageSummary), "query", false },
        // TC-LEDGER-STATEMENT-SCOPE-REGISTRATION, DM-LEDGER-OPERATION-DESCRIPTOR
        { "ledger.reconciliation.scope.register", typeof(RegisterReconciliationScopeInput), typeof(ReconciliationScopeDetail), "mutation", true }
    };

    public static TheoryData<string> OperationIds => new(ExpectedOperationIds);

    private static ReconciliationOperationBundle CreateBundle() => new(
        new ReconciliationProjectionOperationModule(null!),
        new ReconciliationApplyOperationModule(null!),
        new ReconciliationDecisionOperationModule(null!, null!),
        new ReconciliationCoverageOperationModule(null!, null!),
        new ReconciliationScopeOperationModule(null!));

    private sealed class ReconciliationSeam(
        string root,
        LedgerDb database,
        LedgerConnectionFactory factory,
        AccountStore accountStore,
        EvidenceStore evidenceStore,
        TransactionStore transactionStore,
        TallyProcess process,
        ReconciliationOperationBundle bundle) : IAsyncDisposable
    {
        private static readonly SafeActor Actor = new("human", "dirk", "bundle-run");
        private int sequence;

        public static async Task<ReconciliationSeam> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"tally-reconciliation-bundle-{Guid.NewGuid():N}");
            var database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
            var factory = new LedgerConnectionFactory(new HostArtifactProtection());
            var executor = new LedgerMutationExecutor(database, factory, new IdempotencyStore());
            var accountStore = new AccountStore(database, factory);
            var evidenceStore = new EvidenceStore(database, factory);
            var transactionStore = new TransactionStore(database, factory);
            var projectionStore = new ReconciliationProjectionStore(database, factory, evidenceStore, transactionStore);
            var writeStore = new ReconciliationWriteStore(evidenceStore, transactionStore);
            var decisionStore = new ReconciliationDecisionStore(database, factory, evidenceStore, transactionStore);
            var relationshipStore = new RelationshipStore(database, factory);
            var effectWriter = new StatementCorrectionEffectWriter(
                writeStore,
                decisionStore,
                transactionStore,
                new CategoryAllocationStore(database, factory),
                new PaymentAttributionStore(),
                new PaymentIdentityStore(database, factory),
                new PoolAssignmentStore(),
                relationshipStore);
            var correction = new StatementAuthoritativeCorrectionCoordinator(
                executor,
                accountStore,
                projectionStore,
                writeStore,
                transactionStore,
                effectWriter);
            var coverageStore = new ReconciliationCoverageStore(database, factory, transactionStore);
            var process = new TallyProcess(OperationRegistry.Create(), LedgerServices.Create(database));
            var bundle = new ReconciliationOperationBundle(
                new ReconciliationProjectionOperationModule(new ReconciliationProjectionHandler(projectionStore)),
                new ReconciliationApplyOperationModule(
                    new ReconciliationApplyHandler(executor, accountStore, projectionStore, writeStore),
                    correction),
                new ReconciliationDecisionOperationModule(
                    new GetReconciliationDecisionHandler(decisionStore),
                    new ReconciliationDecisionMutationHandler(executor, decisionStore)),
                new ReconciliationCoverageOperationModule(
                    new CompleteStatementCoverageHandler(executor, coverageStore),
                    new GetStatementCoverageHandler(coverageStore)),
                new ReconciliationScopeOperationModule(
                    new RegisterReconciliationScopeHandler(executor, new ReconciliationScopeStore())));
            return new(root, database, factory, accountStore, evidenceStore, transactionStore, process, bundle);
        }

        public async Task<ReconciliationProjectionResult> ProjectAsync(StatementFixture statement) =>
            await DispatchSuccessAsync(
                ReconciliationProjectionOperationModule.OperationId,
                new GetReconciliationCandidatesInput(
                    statement.EvidenceId,
                    statement.ScopeId,
                    ManualReviewProjectionV1.PolicyId,
                    ManualReviewProjectionV1.PolicyVersion),
                ReconciliationProjectionJsonContext.Default.GetReconciliationCandidatesInput,
                ReconciliationProjectionJsonContext.Default.ReconciliationProjectionResult);

        public async Task<ReconciliationApplyResult> ApplyAsync(
            StatementFixture statement,
            ReconciliationProjectionResult projection,
            ReconciliationApplyDisposition disposition,
            string? targetTransactionId,
            AuthoritativeStatementFact? statementFact,
            string key,
            ReconciliationAuthorityKind authorityKind = ReconciliationAuthorityKind.Owner)
        {
            var result = await ApplyRawAsync(
                statement,
                projection,
                disposition,
                targetTransactionId,
                statementFact,
                key,
                authorityKind);
            Assert.True(result.IsSuccess, result.ErrorCode);
            return JsonSerializer.Deserialize(
                result.Value,
                ReconciliationApplyJsonContext.Default.ReconciliationApplyResult)!;
        }

        public async Task<CommandResult<JsonElement>> ApplyRawAsync(
            StatementFixture statement,
            ReconciliationProjectionResult projection,
            ReconciliationApplyDisposition disposition,
            string? targetTransactionId,
            AuthoritativeStatementFact? statementFact,
            string key,
            ReconciliationAuthorityKind authorityKind = ReconciliationAuthorityKind.Owner)
        {
            var candidates = projection.ExactCandidates
                .Concat(projection.GuardCandidates)
                .Select(candidate => candidate.TransactionId)
                .Order(StringComparer.Ordinal)
                .ToArray();
            return await DispatchAsync(
                ReconciliationApplyOperationModule.OperationId,
                new ReconciliationApplyInput(
                    statement.EvidenceId,
                    statement.Fingerprint,
                    statement.ScopeId,
                    projection.AdvisoryToken,
                    disposition,
                    authorityKind,
                    candidates,
                    targetTransactionId,
                    statementFact,
                    null,
                    "owner reviewed statement evidence"),
                ReconciliationApplyJsonContext.Default.ReconciliationApplyInput,
                key);
        }

        public async Task<ReconciliationDecisionDetail> GetDecisionAsync(string evidenceId) =>
            await DispatchSuccessAsync(
                "ledger.reconciliation.decision.get",
                new GetReconciliationDecisionInput(evidenceId),
                ReconciliationDecisionJsonContext.Default.GetReconciliationDecisionInput,
                ReconciliationDecisionJsonContext.Default.ReconciliationDecisionDetail);

        public async Task<StatementCoverageSummary> CompleteCoverageAsync(StatementFixture statement, string key) =>
            await DispatchSuccessAsync(
                ReconciliationCoverageOperationModule.CompleteOperationId,
                new CompleteStatementCoverageInput(
                    statement.ScopeId,
                    statement.AccountId,
                    "2026-07-01",
                    "2026-07-31",
                    statement.ManifestReference,
                    [statement.EvidenceId],
                    StatementCoveragePolicy.PolicyId,
                    StatementCoveragePolicy.PolicyVersion),
                ReconciliationCoverageJsonContext.Default.CompleteStatementCoverageInput,
                ReconciliationCoverageJsonContext.Default.StatementCoverageSummary,
                key);

        public async Task<StatementCoverageSummary> GetCoverageAsync(string scopeId) =>
            await DispatchSuccessAsync(
                ReconciliationCoverageOperationModule.GetOperationId,
                new GetStatementCoverageInput(scopeId),
                ReconciliationCoverageJsonContext.Default.GetStatementCoverageInput,
                ReconciliationCoverageJsonContext.Default.StatementCoverageSummary);

        public async Task<TResult> DispatchSuccessAsync<TInput, TResult>(
            string operationId,
            TInput input,
            JsonTypeInfo<TInput> inputType,
            JsonTypeInfo<TResult> resultType,
            string? key = null)
        {
            var result = await DispatchAsync(operationId, input, inputType, key);
            Assert.True(result.IsSuccess, result.ErrorCode);
            return JsonSerializer.Deserialize(result.Value, resultType)!;
        }

        public async Task<CommandResult<JsonElement>> DispatchAsync<TInput>(
            string operationId,
            TInput input,
            JsonTypeInfo<TInput> inputType,
            string? key = null)
        {
            var descriptor = Assert.Single(bundle.Descriptors, candidate => candidate.OperationId == operationId);
            var handler = descriptor.HandlerFactory(LedgerServices.Create(), OperationRegistry.Create());
            return await handler.HandleAsync(
                new(JsonSerializer.SerializeToElement(input, inputType), Actor, key),
                CancellationToken.None);
        }

        public async Task<string> SeedAccountAsync()
        {
            await using var connection = await OpenAsync();
            await using var transaction = connection.BeginTransaction();
            var accountId = LedgerId.New().ToString();
            var suffix = (1000 + Interlocked.Increment(ref sequence)).ToString("0000", System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(AccountDefinition.TryCreate(
                new("Bank", "Bundle " + suffix, AccountType.Cheque, "****" + suffix, "ZAR"),
                out var account,
                out var error), error);
            await accountStore.InsertAsync(
                connection,
                transaction,
                accountId,
                LedgerId.New().ToString(),
                account!,
                "test:bundle",
                At(0),
                CancellationToken.None);
            await transaction.CommitAsync();
            return accountId;
        }

        public async Task<string> SeedTransactionAsync(string accountId, long amountMinor, string transactionDate)
        {
            var input = new RecordTransactionInput(
                accountId,
                Money.FromMinorUnits(amountMinor).ToString(),
                "ZAR",
                transactionDate,
                null,
                "agent-captured transaction",
                null,
                null,
                new(EvidenceKind.AgentCapture, Digest('a'), null, null, null));
            Assert.True(TransactionFact.TryCreate(input, out var fact, out var error), error);
            var transactionId = LedgerId.New().ToString();
            await using var connection = await OpenAsync();
            await using var transaction = connection.BeginTransaction();
            await transactionStore.InsertFactAndDefaultsAsync(
                connection,
                transaction,
                transactionId,
                LedgerId.New().ToString(),
                null,
                LedgerId.New().ToString(),
                fact!,
                At(1),
                "ubuntu",
                "test:bundle",
                CancellationToken.None);
            await transaction.CommitAsync();
            return transactionId;
        }

        public async Task<StatementFixture> SeedStatementAsync(
            string accountId,
            long amountMinor,
            string transactionDate)
        {
            var fingerprint = Digest('b');
            var manifest = "statement:manifest:" + Interlocked.Increment(ref sequence);
            var observation = new EvidenceObservation(
                accountId,
                amountMinor,
                "ZAR",
                transactionDate,
                null,
                null,
                null,
                Digest('c'));
            var input = new RegisterEvidenceInput(
                EvidenceKind.StatementRow,
                Digest('d'),
                "statement:row:" + sequence,
                fingerprint,
                observation);
            Assert.True(EvidenceIdentity.TryCreate(input, out var identity, out var error), error);
            await using var connection = await OpenAsync();
            await using var transaction = connection.BeginTransaction();
            var evidence = await evidenceStore.RegisterInitialAsync(
                connection,
                transaction,
                identity!,
                input,
                "test:bundle",
                At(2),
                CancellationToken.None);
            var scopeId = LedgerId.New().ToString();
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO statement_scope(
                    scope_id, account_id, period_start, period_end, manifest_opaque_reference,
                    status, created_by, created_at)
                VALUES ($scopeId, $accountId, '2026-07-01', '2026-07-31', $manifest,
                        'completed', 'test:bundle', $at);
                INSERT INTO statement_scope_evidence(scope_id, evidence_id)
                VALUES ($scopeId, $evidenceId);
                """,
                ("$scopeId", scopeId),
                ("$accountId", accountId),
                ("$manifest", manifest),
                ("$at", At(2)),
                ("$evidenceId", evidence.EvidenceId));
            await transaction.CommitAsync();
            return new(
                evidence.EvidenceId,
                scopeId,
                accountId,
                fingerprint,
                manifest,
                new(
                    accountId,
                    Money.FromMinorUnits(amountMinor).ToString(),
                    "ZAR",
                    transactionDate,
                    null,
                    "statement-authoritative transaction"));
        }

        public async Task ConfirmRelationshipAsync(
            string relationshipKind,
            string sourceTransactionId,
            string targetTransactionId)
        {
            var result = relationshipKind switch
            {
                "transfer" => await RunProcessAsync(
                    "ledger.transfer.confirm",
                    JsonSerializer.SerializeToElement(
                        new ConfirmTransferInput(sourceTransactionId, targetTransactionId, "owner confirmed transfer"),
                        LedgerJsonContext.Default.ConfirmTransferInput),
                    "confirm-transfer-" + Interlocked.Increment(ref sequence)),
                "refund" => await RunProcessAsync(
                    "ledger.refund.confirm",
                    JsonSerializer.SerializeToElement(
                        new ConfirmRefundInput(sourceTransactionId, targetTransactionId, "owner confirmed full refund"),
                        LedgerJsonContext.Default.ConfirmRefundInput),
                    "confirm-refund-" + Interlocked.Increment(ref sequence)),
                _ => throw new ArgumentOutOfRangeException(nameof(relationshipKind))
            };
            Assert.True(result.ExitCode == 0, $"exit={result.ExitCode}; stdout={result.Stdout}; stderr={result.Stderr}");
        }

        public async Task<IReadOnlyDictionary<string, long>> EffectCountsAsync()
        {
            var tables = new[]
            {
                "transaction_fact",
                "transaction_lifecycle_event",
                "reconciliation_decision",
                "reconciliation_decision_authority",
                "evidence_link_event",
                "reconciliation_exception",
                "financial_relationship",
                "relationship_lifecycle_event",
                "statement_correction",
                "statement_correction_relationship_event",
                "idempotency_record",
                "logical_effect"
            };
            var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var table in tables) counts.Add(table, await CountAsync($"SELECT COUNT(*) FROM {table};"));
            return counts;
        }

        public async Task ExecuteDatabaseAsync(string sql)
        {
            await using var connection = await OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public async Task<long> CountAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = await OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            return ValueTask.CompletedTask;
        }

        private Task<SqliteConnection> OpenAsync() =>
            factory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);

        private async Task<ProcessResult> RunProcessAsync(string operationId, JsonElement input, string key)
        {
            var envelope = new RequestEnvelope("1.0", Actor, input, key);
            var body = JsonSerializer.Serialize(envelope, LedgerJsonContext.Default.RequestEnvelope);
            var arguments = OperationRegistry.Create().Find(operationId)!.CliPath
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Skip(1)
                .Concat(["--input", "-"])
                .ToArray();
            return await process.RunAsync(arguments, body, CancellationToken.None);
        }

        private static async Task ExecuteAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string sql,
            params (string Name, object Value)[] parameters)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            await command.ExecuteNonQueryAsync();
        }

        private static string At(int hour) => $"2026-07-22T{hour:D2}:00:00.0000000Z";
        private static string Digest(char value) => new(value, 64);
    }

    private sealed record StatementFixture(
        string EvidenceId,
        string ScopeId,
        string AccountId,
        string Fingerprint,
        string ManifestReference,
        AuthoritativeStatementFact Fact);
}
