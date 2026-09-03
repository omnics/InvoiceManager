namespace InvoiceManager.Core;

/// <summary>
/// The JSON body <c>GenerateExpectedRecordsHttp</c> returns (always as HTTP 207 - see that
/// function for why): every configuration's expected-record-generation outcome and every due
/// record's processing outcome from one run. Shared between the Functions app (writer) and
/// AdminWeb (reader), since AdminWeb calls the endpoint over HTTP rather than in-process.
/// </summary>
public sealed record ExpectedRecordGenerationRunWire(
    IReadOnlyList<ConfigurationOutcomeWire> Generation,
    IReadOnlyList<RecordOutcomeWire> Processing);

/// <summary>One configuration's expected-record-generation outcome; <see cref="Error"/> is null except for a failure.</summary>
public sealed record ConfigurationOutcomeWire(string ConfigurationId, string Status, string? Error);

/// <summary>One record's due-invoice-processing outcome; <see cref="Error"/> is null except for a failure.</summary>
public sealed record RecordOutcomeWire(string RecordId, string Status, string? Error);
