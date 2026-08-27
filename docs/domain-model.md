# Domain Model

This document defines the shared vocabulary for InvoiceManager.

## Invoice Configuration

A configuration entry describing an invoice that the service expects to retrieve
on a recurring basis.

Expected fields include:

- Integration configuration (a union, keyed by integration type — see below).
- Invoice name.
- Expected frequency.
- Expected amount and currency when needed for matching.
- OneDrive folder.
- FreeAgent matching information.
- Active/inactive state.

Integration-specific settings are modelled as `IntegrationConfiguration`, a union
of `MicrosoftBillingIntegrationConfiguration` (a billing account ID) and
`GraphEmailIntegrationConfiguration` (a sender email address and a body regular
expression used to find the candidate email). A configuration cannot carry
fields meaningful only to a different integration type — there is no
always-present-but-sometimes-empty field. `IntegrationType` is a read-only value
derived from which union case is present.

The configuration ID and integration type are immutable after creation. New
configurations are inactive drafts; activation changes state only and never runs
invoice processing. Deactivation preserves configuration and invoice history and
causes outstanding records to be skipped until reactivation.

The OneDrive folder (`OneDriveFolder`) always contains a stable drive ID, drive
name, folder item ID, and a human-readable folder path — there is no legacy
path-only mode. Configuration mutations append complete, immutable audit
revisions. Restoring a revision restores editable business fields but retains
identity, integration type, and the current activation state.

Edits are future-only. Every newly generated expected invoice snapshots all values
needed for matching and routing, so existing records never partially combine old
criteria with newly edited live configuration.

Two configurations of the same integration type must not share the same
type-specific matching fields — the same sender email address and body pattern
for `GraphEmail`, or the same billing account ID *and* the same (or equally
absent) expected amount for `MicrosoftBilling`. The amount is part of the
`MicrosoftBilling` comparison because one billing account routinely bills more
than one distinct product — see the Microsoft 365 Business Basic/Copilot
example in [Invoice Name](#invoice-name) — so billing account ID alone is not
enough to call two configurations duplicates. Creating or editing a
configuration is rejected if it would duplicate another configuration's
criteria (active or inactive), even when the OneDrive destination differs: two
configurations that would pick up the same source files is treated as a
mistake to reject rather than a way to route one set of files to multiple
destinations, which should be a first-class feature if ever needed. This check
is concurrency-safe: two admins creating/editing/restoring different
configurations at the same time can't both slip past it and reintroduce the
same duplicate, because it's guarded by an ETag-protected sentinel document
rather than being a plain read-then-write - see docs/data-model.md's
"Duplicate-validation sentinel" section for the mechanism.

Configurations can be exported to a file and imported into another environment
as a new inactive draft, to promote a configuration between environments
without re-entering every field by hand. Imported OneDrive folder and billing
account values are still re-verified against that environment's own Microsoft
Graph/discovery data before saving, the same as a manually entered value — never
trusted outright from the file.

## Integration

A provider-specific component that interacts with an external system.

Examples:

- Microsoft Azure invoice source.
- Microsoft 365 invoice source.
- Microsoft 365 email attachment source.
- OpenAI invoice source.
- OneDrive file destination.
- FreeAgent bill destination.

## Integration Type

The configured identifier used to select the correct integration for an invoice
configuration.

Examples:

- `MicrosoftBilling`
- `OpenAI`
- `GraphEmail`

`MicrosoftBilling` and `GraphEmail` are implemented (`IntegrationType`).
`MicrosoftBilling` covers both personal (Microsoft 365) and company (Azure)
billing accounts — they differ only in which billing account ID is configured,
not in integration behaviour, so they share one integration type. `OpenAI` is
blocked pending a provider API (see the tracking issue referenced from
[product.md](product.md#invoice-sources)).

## Invoice Name

The human-readable name of the expected invoice.

Examples:

- `ChatGPT Plus`
- `Copilot 365`

The invoice name may be used in generated filenames, logs, and user-facing
status records.

For providers such as Microsoft 365, more than one expected invoice may share
the same integration type and period. In that case the invoice name identifies
the configured expectation and the generated filename, while date and amount are
used to match the source invoice before the PDF contents are available.

## Expected Frequency

The recurrence pattern for an expected invoice.

Examples may include:

- Monthly.
- Annual.
- Custom schedule.

The first implementation should only support frequencies that are needed by the
configured invoices.

## Expected Invoice

A record representing an invoice that should exist by a particular date.

Expected invoice records allow the service to detect missing invoices, retry
failed retrievals, and keep history separate from configuration.

An expected invoice can include selection criteria used to find the matching
source invoice or an existing OneDrive file:

- Expected invoice date.
- Expected amount.
- Expected currency.
- Date tolerance or matching window from configuration or stored data.
- Invoice name or category for filenames and reporting.

Expected metadata should be preserved separately from actual metadata collected
after the invoice is retrieved or reconciled.

## Invoice Search Criteria

The provider-independent criteria passed from the core workflow to an invoice
source integration when requesting an invoice.

Search criteria may include expected date, date tolerance, expected amount and
currency.
Source and OneDrive integrations translate those criteria into provider-specific
search and matching behavior without exposing those details to the core
workflow.

See [workflow.md#search-criteria](workflow.md#search-criteria)
for the workflow rules that use these criteria.

## Retrieved Invoice

An invoice that has been found by an integration.

Retrieved invoice metadata may include:

- Invoice date.
- Invoice total.
- Currency.
- VAT inclusive/exclusive indicator.
- Source integration.
- Source identifier.
- File content or file reference.

## Reconciled Invoice

An expected invoice that has been matched to a file already present in OneDrive
before the source integration retrieves a new copy.

Reconciliation allows the workflow to account for files that were downloaded
manually, uploaded as a manual repair, or saved by a previous partial run. A
reconciled invoice should record the OneDrive location, when reconciliation
occurred, and the reason the match was accepted.

Reconciled invoices should continue through the downstream workflow where
appropriate, including FreeAgent attachment once that integration behavior is
defined in detail.

## Invoice Match

The result returned by a source or OneDrive integration after applying invoice
search criteria to provider-specific candidates.

An invoice match should indicate whether no match was found or an accepted match
was found. Accepted matches should include the metadata needed by the core
workflow to update invoice state, such as actual invoice date, amount, currency,
source invoice ID where available, OneDrive location where available, and the
reason the match was accepted.

Provider-specific matching behavior is defined in
[workflow.md#source-matching](workflow.md#source-matching).

## PDF Field Extraction

For sources that expose no reliable metadata before the invoice PDF is opened
— currently `GraphEmail` — the invoice date and total are read from the
PDF's own content via `IInvoicePdfExtractor`, implemented using Azure AI
Document Intelligence's prebuilt `invoice` model. This is a provider-boundary
interface like the invoice source and OneDrive integrations: the core workflow
and the email source integration both stay unaware of Document Intelligence
specifics.

VAT mode is deliberately never derived from the PDF; `actualVatMode` always
comes from configuration, the same rule as every other integration type (see
[Invoice Total](#invoice-total)). A failed or low-confidence extraction is
treated as a `RetrievalError` — no new workflow state is needed. See
[workflow.md#source-matching](workflow.md#source-matching) for how this fits
the email source's matching flow.

## Invoice Date

The date shown on the invoice. This date is used in the saved OneDrive filename
and should be represented in ISO format when included in filenames.

## Invoice Total

The monetary total from the invoice.

Invoice totals must preserve:

- Amount.
- Currency.
- Whether the amount is VAT inclusive (`inc`) or VAT exclusive (`exc`).

Currency must be part of all amount comparisons. For example, OpenAI invoices
may be in USD while most other invoices are expected to be in GBP, so raw decimal
amounts must not be compared without currency.

## Money Amount

A strongly typed value representing an amount and currency.

The implementation should prefer a widely used .NET money library rather than
passing loose decimal and string pairs through the application. `NodaMoney` is a
candidate package because it provides money and currency types backed by ISO
4217 currency data. The domain should still wrap library types where useful so
persistence and workflow rules remain stable if the implementation package
changes.

## OneDrive Destination

The configured OneDrive folder where a retrieved invoice should be saved.

The saved location should be stored after upload so later runs can avoid
duplicating files and can show where the invoice was saved.

OneDrive reconciliation behavior is defined in
[workflow.md#onedrive-reconciliation](workflow.md#onedrive-reconciliation).

## FreeAgent Bill

The FreeAgent bill that corresponds to a processed invoice.

The service should store the FreeAgent bill URL after matching or upload. If the
bill total does not match the invoice total, the service may update the bill
where the integration and configuration allow it.

## Processing Run

A single execution of the background service.

A processing run should record start time, finish time, status, and summary
counts so failures can be inspected through storage and monitoring.

## Invoice Workflow State

The current state of an invoice record as it moves through retrieval,
reconciliation, and save steps. States are modelled as a union type
(`InvoiceWorkflowState`) rather than a bare enum so that each state carries
exactly the data valid in that state — a record cannot exist in a state
without the values that state requires.

Current states:

- `Expected` — carries an optional `LastDiagnostic`: absent for a record never
  yet attempted, or the source integration's explanation of why its most
  recent attempt found nothing, while still within the tolerance window (today
  is before `expectedDate + dateToleranceDays`). Retried on later runs, with
  `LastDiagnostic` refreshed on every attempt.
- `NotFound` — requires `Diagnostic`: the source integration's explanation of
  why the deadline-time attempt found nothing (search window, expected
  amount/tolerance, and the nearest rejected candidate's actual values, if
  any). Terminal state. Reaching `NotFound` also stops the
  recurrence for that configuration: no further expected records are created
  while its most recent record is `NotFound` (see
  [Next-expected creation and cancellation](#next-expected-creation-and-cancellation)).
  This is deliberate — a missing invoice most often means the underlying
  subscription or service was cancelled, so no more invoices of that type are
  expected. If instead an invoice was genuinely skipped for one period, a user
  must intervene manually to resume the schedule (automatic recovery is deferred).
- `RetrievalError` — requires an error message; a retrieval attempt failed with a
  technical error, so the system could not determine whether the invoice exists.
  Always retried, with no retry limit.
- `Retrieved` — requires `ActualInvoiceDetails`.
- `ReconciledFromOneDrive` — requires `ActualInvoiceDetails` and
  `OneDriveDetails`.
- `SavedToOneDrive` — requires `ActualInvoiceDetails` and `OneDriveDetails`.

`ActualInvoiceDetails` encapsulates values read from the actual invoice once
found (currently the actual invoice date; extensible with amount, currency,
VAT mode, and source ID as retrieval features land). `OneDriveDetails`
encapsulates where the invoice file lives in OneDrive (currently the location;
extensible with a file ID).

Future workflow steps (FreeAgent upload, next-expected creation, skip states)
should be added as new union cases carrying whatever data those states
require. The persisted `status` string derives from the case name and must be
kept stable once persisted.

### Next-Expected Creation and Cancellation

The next expected record for a configuration is derived from that
configuration's most recent record. The next date is only produced once the most
recent record has reached a success state (`SavedToOneDrive` or
`ReconciledFromOneDrive`); it is calculated from the actual invoice date plus the
configured frequency. While the most recent record is in any non-success state
(`Expected`, `RetrievalError`, `Retrieved`, or the terminal `NotFound`), no next
record is created.

For `NotFound` this stop is intentional, not a defect. When an expected invoice
is never found within its tolerance window, the most likely cause is that the
underlying subscription or service was cancelled, so no further invoices of that
type will ever arrive and the recurrence should stop rather than accumulate
perpetually-missing records. In the less likely case that a single period was
genuinely skipped, resuming the schedule currently requires manual intervention
(for example creating the next expected record or clearing the `NotFound`
record); automatic recovery of a one-off gap is deferred to later work.

## Identifier Types

Domain identifiers should use small typed wrappers rather than raw strings wherever
practical. For example, `InvoiceConfigurationId` is a `readonly record struct` that
wraps a stable slug string rather than exposing `string` directly.

Typed identifiers:

- Prevent parameter-order bugs at compile time (e.g. swapping two `string` IDs).
- Centralise parsing and validation at the type boundary.
- Round-trip as plain strings in Cosmos DB, seed files, and binding frameworks
  without any call-site conversion, via the shared `StringIdTypeConverter<TId>`
  and `StringIdJsonConverter<TId>`.

New identifier types should implement `IStringId<TSelf>` and apply the shared
converters via attributes, following the pattern established by
`InvoiceConfigurationId`; validation lives in the type's constructor.

## Next Expected Invoice

The future expected invoice record created after the current invoice reaches the
configured success state.

The next expected invoice is derived from the invoice configuration, recurrence
rule, and completed invoice metadata. Creation should be idempotent so retries
or repeated runs do not create duplicate records for the same configured period.
