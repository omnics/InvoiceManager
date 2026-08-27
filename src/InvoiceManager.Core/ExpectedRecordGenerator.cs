using System.Diagnostics;
using InvoiceManager.Core.Repositories;
using Microsoft.Extensions.Logging;

namespace InvoiceManager.Core;

/// <summary>
/// Generates the next expected invoice record for a configuration.
/// Reusable by both the timer trigger on startup and the post-processing step
/// after a successful invoice save.
/// </summary>
public sealed class ExpectedRecordGenerator(
    IInvoiceRecordRepository repository,
    IInvoiceConfigurationRepository configurationRepository,
    ILogger<ExpectedRecordGenerator> logger)
{
    /// <summary>
    /// Generates the next expected record for every active configuration.
    /// A failure for one configuration is logged and reported in the results
    /// without stopping generation for the others. Cancellation aborts the run.
    /// </summary>
    public async Task<IReadOnlyList<ExpectedRecordGenerationResult>> GenerateForAllActiveAsync(
        CancellationToken cancellationToken = default)
    {
        using var activity = Telemetry.ActivitySource.StartActivity("generate_expected_records");

        var configurations = await configurationRepository.ListActiveAsync(cancellationToken);
        var results = new List<ExpectedRecordGenerationResult>(configurations.Count);
        activity?.SetTag("invoice.active_configuration_count", configurations.Count);

        foreach (var configuration in configurations)
        {
            try
            {
                await GenerateAsync(configuration, cancellationToken);
                results.Add(new GenerationSucceeded(configuration.Id));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex,
                    "Expected record generation failed for configuration {ConfigurationId}.",
                    configuration.Id);
                results.Add(new GenerationFailed(configuration.Id, ex));
            }
        }

        return results;
    }

    public async Task GenerateAsync(InvoiceConfiguration configuration, CancellationToken cancellationToken = default)
    {
        using var activity = Telemetry.ActivitySource.StartActivity("generate_expected_record");
        activity?.SetTag("invoice.configuration_id", configuration.Id.Value);

        var mostRecent = await repository.GetMostRecentAsync(configuration.Id, cancellationToken);
        var nextDateResult = NextExpectedInvoiceDate.CalculateNext(configuration, mostRecent);

        if (nextDateResult is not NextExpectedDate nextExpectedDate)
        {
            // The most recent record has not reached a success state, so no next
            // expected record is created yet. Record why, so an operator does not
            // mistake the absence of a new record for a fault.
            activity?.SetTag("invoice.next_date", "in_progress");
            activity?.AddEvent(new ActivityEvent("next_record_in_progress"));
            logger.LogInformation(
                "No next expected record created for configuration {ConfigurationId}: the most recent record " +
                "has not reached a success state (still in progress).",
                configuration.Id);
            return;
        }

        activity?.SetTag("invoice.next_date", nextExpectedDate.Date.ToString("O"));

        var record = new InvoiceRecord(
            configuration.Id,
            nextExpectedDate.Date,
            new Expected(Option.None),
            InvoiceProcessingSnapshot.FromConfiguration(configuration));

        await repository.CreateIfNotExistsAsync(record, cancellationToken);
    }
}
