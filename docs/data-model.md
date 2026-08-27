# Data Model

InvoiceManager should use Azure Cosmos DB for NoSQL in serverless mode as the
initial persistent storage option.

The data model should be optimized for the service's operational queries rather
than normalized like a relational database.

## Containers

Initial containers:

- `invoice-configurations`
- `invoice-records`
- `processing-runs`

This can be adjusted during implementation if query patterns require fewer or
more containers.

## invoice-configurations

Stores recurring invoice expectations and provider configuration references.

Live configurations and immutable audit revisions share this container and the
same constant `/partitionKey` value, `config`. `documentType` is
`invoiceConfiguration` or `invoiceConfigurationRevision`; every live query
excludes revisions. Keeping every configuration in one partition makes the
Cosmos-native `id` constraint enforce globally unique configuration IDs while
preserving atomic live-document and revision writes.

Purpose:

- List active invoice configurations.
- Select the correct invoice integration.
- Determine expected invoice frequency.
- Determine default expected amount and currency where useful for matching.
- Determine OneDrive and FreeAgent behavior.

Suggested partition key:

- `/partitionKey` (constant value `config`)

Candidate fields:

- `id`
- `partitionKey`
- `integrationType` — kept flat for query/index filtering; derived and written
  from `integrationConfiguration` on save, not read back into the domain type
- `integrationConfiguration` — a discriminated object (`type` of
  `microsoftBilling` or `graphEmail`) carrying only the fields relevant to that
  integration type: `billingAccountId` for `microsoftBilling`;
  `senderEmailAddress` and `bodyPattern` (a regular expression a candidate
  email's plain-text body must match) for `graphEmail`
- `invoiceName`
- `expectedFrequency`
- `amountMatchingCriteria` — optional object containing `amount`, `currency`,
  and `amountTolerance`; absent when the provider's amount is unpredictable
- `defaultVatMode`
- `isActive`
- `oneDriveFolder` — a required object containing `driveId`, `driveName`,
  `folderItemId`, and `folderPath` (no legacy path-only mode)
- `dateToleranceDays`
- `freeAgentMatching` — optional object; absent for configurations that don't
  match to FreeAgent. Contains `contactUrl` (the FreeAgent contact's resource
  URL, the only part matching keys off) and `contactDisplayName` (a cached,
  non-authoritative display name shown in AdminWeb without a live FreeAgent
  call on every page load; refreshed from FreeAgent whenever the owning
  configuration is saved via Edit or Import), plus optional
  `dateReconciliation`/`amountReconciliation` sub-objects
- `createdAt`
- `updatedAt`

Live documents carry the Cosmos `_etag` through edit/restore forms. Mutations
use `If-Match` and a same-partition transactional batch to replace the live
document and append the revision atomically. Revisions have no TTL and contain
a unique ID, action, timestamp, actor object ID/display name, and the complete
resulting snapshot. The first mutation of legacy unaudited data also appends a
pre-audit baseline.

Notes:

- Do not store secrets in configuration records.
- Store references to secret names or configuration keys when needed.
- Separate Microsoft 365 invoices, such as Copilot and Office 365 extensions,
  should usually be separate configuration records even when they use the same
  `integrationType`.
- If multi-company support becomes necessary, reconsider the partition key and
  include a company or tenant identifier.

### Duplicate-validation sentinel

`InvoiceConfigurationService`'s cross-configuration duplicate-search-criteria
check (list every live configuration, then validate a candidate against them)
is a read-then-write operation: without protection, two concurrent
Create/Update/Restore calls for *different* configuration IDs can both read
the same list, both pass validation, and both commit, since their individual
document ETags don't conflict with each other - reintroducing the exact
duplicate the check exists to prevent.

A single sentinel document closes that race:

- `id`: the fixed constant `__duplicate_validation_sentinel__` (one sentinel
  for the whole container, alongside every configuration and revision, in
  the same `config` partition). Deliberately contains underscores, which a
  real configuration ID's lowercase-kebab-case validation never allows, so
  this ID can never collide with one a user creates - structurally, not by a
  reserved-word check that a new call site could forget to apply.
- `documentType`: `invoiceConfigurationValidationSentinel`.
- `partitionKey`: `config` (the same constant value as everything else in
  this container).
- No other fields - the sentinel carries no meaningful content of its own.
  Every write to it is a conditional replace with the same body; the only
  purpose is to force Cosmos to hand out a new `_etag`, the way any write
  does regardless of whether the body actually changed.

Protocol: before validating, read the sentinel's current ETag. Validate the
candidate against the live list as before. Then include a conditional
replace of the sentinel (`If-Match` on the ETag just read) in the *same*
transactional batch as the configuration + revision writes that Create/
Update/Restore already perform. One concurrent writer's batch commits and
changes the sentinel's ETag; a second writer whose batch also included a
(now-stale) `If-Match` on that ETag has its whole batch rejected with a
precondition failure - so its earlier validation is treated as unreliable,
and it re-reads the sentinel and the live list and revalidates once before
giving up (see `InvoiceConfigurationService`'s XML doc remarks for the exact
retry/give-up rule). This is optimistic serialization of the validation
check itself, not a lock: writes that don't touch search criteria (activate/
deactivate) don't participate and never contend with it.

`ConfigurationSeeder`/`tools/InvoiceManager.Seeder` participates too, even
though it never calls the duplicate-search-criteria check itself and only
ever runs single-threaded, once, at deploy time: `scripts/Deploy-Infra.ps1`
runs `terraform apply` (which can start routing traffic to a live AdminWeb
instance) *before* invoking the seeder, so a seeded configuration can
genuinely race a concurrent Create/Update/Restore request through AdminWeb
during that window, in either order. `CreateIfNotExistsAsync` (the plain
insert-if-absent method the seeder calls) carries the whole protocol instead:

- If the seeder's insert wins the sentinel race first, its successful insert
  conditionally replaces the sentinel in the same transactional batch, using
  its own read of the sentinel's ETag. An AdminWeb request that read the
  sentinel and the configuration list *before* the seeder's insert then
  correctly loses its own write with `ValidationSentinelConflict` and
  revalidates through `InvoiceConfigurationService`.
- If an AdminWeb write commits conflicting search criteria first - either
  before the seeder's very first attempt, or between the seeder losing a
  sentinel race and its retry - `CreateIfNotExistsAsync` revalidates the seed
  configuration's search criteria against the live list on *every* attempt
  it makes to insert a **new** ID, so a blind retry can never insert on top
  of a conflict it would otherwise never notice. A genuine seed-time
  conflict throws `SeedConfigurationConflictException` rather than being
  silently committed alongside the configuration it conflicts with - see
  that type's XML doc for the reasoning (this is a deploy-time data problem
  for a human to fix, not a normal outcome for the pipeline to recover from
  automatically).

Crucially, this duplicate-search-criteria check only ever runs for a
genuinely new configuration ID - `CreateIfNotExistsAsync` checks whether a
configuration with this exact ID already exists *first*, and returns
immediately (its long-standing, always-safe no-op) without running the
check at all if so. This matters because a seeded configuration's live
search criteria can legitimately drift away from what the seed file
originally specified (an admin edits it after it's first seeded, freeing up
its original criteria for some other configuration to claim) - re-seeding
that same ID later (e.g. on a redeploy) must remain a harmless no-op even
though the seed file's original criteria might now genuinely match a
different live configuration; it must never be misreported as a conflict.

See the XML docs on `CreateIfNotExistsAsync`, `ConfigurationSeeder`, and
`SeedConfigurationConflictException` for the full reasoning.

## invoice-records

Stores expected invoice processing history, including retrieval and
reconciliation outcomes.

Purpose:

- Track the next expected invoice.
- Track whether an expected invoice has been retrieved.
- Track OneDrive saved location.
- Track FreeAgent bill URL.
- Support retry and audit history.

Suggested partition key:

- `/configurationId`

Candidate fields:

- `id`
- `configurationId`
- `expectedDate`
- `processingSnapshot` — required object containing integration type, billing
  account ID, OneDrive destination, invoice description, date/amount criteria,
  VAT mode, and provider-neutral source-selection fields
- `status`
- `actualInvoiceDetails` — nested sub-object, present when the state carries
  actual values: `actualInvoiceDate`, `actualAmount`, `actualCurrency`,
  `sourceInvoiceId` (candidates: `actualVatMode`, `dateRetrieved`). The VAT mode
  is intentionally not stored on actuals — it is taken from configuration.
- `oneDriveDetails` — nested sub-object: `oneDriveLocation` (candidate:
  `oneDriveFileId`)
- `sourceMetadata`
- `matchStatus`
- `matchReason`
- `reconciledFromOneDrive`
- `reconciledAt`
- `reconciliationSource`
- `freeAgentBillUrl`
- `nextInvoiceRecordId`
- `nextInvoiceCreatedAt`
- `lastError` — present when `status` is `RetrievalError`; the technical failure
  detail from the last retrieval attempt, used for diagnosis
- `lastMatchDiagnostic` — the source/FreeAgent integration's explanation of why
  its most recent match attempt found nothing (search window, expected
  amount/tolerance, and the nearest rejected candidate's actual values, if
  any). Required when `status` is `NotFound`. Present but optional when
  `status` is `Expected` or `FreeAgentMatchExpected` - absent for a
  never-yet-attempted record.
- `retryCount`
- `createdAt`
- `updatedAt`

Notes:

- Expected fields are the criteria used to find the invoice. Actual fields are
  populated after retrieval or OneDrive reconciliation and should not overwrite
  the expected values.
- `status` is the workflow-state discriminator (see the Invoice Workflow State
  section of the domain model). The `actualInvoiceDetails` and `oneDriveDetails`
  sub-objects are present exactly when the state requires them; reads reject
  documents whose sub-objects are missing when the status requires them, or
  incomplete.
- The snapshot VAT mode distinguishes VAT inclusive (`inc`) and VAT exclusive
  (`exc`) totals.
- Amount comparisons must include currency. OpenAI invoices may be in USD while
  most other invoices are expected to be in GBP.
- `sourceMetadata` may contain provider-specific non-secret metadata.
- `matchReason` should preserve why a candidate was accepted, such as matching
  expected date and amount within the configured tolerance.
- `reconciliationSource` can distinguish automatic OneDrive scans from future
  manual override or migration tooling without requiring an admin UI now.
- The service should avoid creating duplicate records for the same expected
  invoice period.

## processing-runs

Stores summary information for each service execution.

Purpose:

- Review recent runs.
- Link logs and failures to a specific run.
- Record summary counts for monitoring and diagnosis.

Suggested partition key:

- `/runMonth`

Candidate fields:

- `id`
- `runMonth`
- `startedAt`
- `finishedAt`
- `status`
- `triggerType`
- `expectedInvoiceCount`
- `retrievedInvoiceCount`
- `reconciledFromOneDriveCount`
- `savedToOneDriveCount`
- `uploadedToFreeAgentCount`
- `nextInvoiceCreatedCount`
- `missingInvoiceCount`
- `failedInvoiceCount`
- `errorSummary`

## Query Patterns

The initial model should support these queries:

- Find all active invoice configurations.
- Find invoice records for a configuration.
- Find invoices expected on or before a date.
- Find invoice records that need OneDrive reconciliation.
- Find invoices with failed or retryable status.
- Find recent processing runs.
- Find the latest record for a configured invoice.
- Find whether the next expected record already exists for a configuration and
  period.
- Find possible duplicate records by configuration, expected date, amount, and
  currency.

## Consistency Expectations

The workflow should persist enough state after meaningful steps to allow retry
after partial failure without losing progress. The data model supports those
steps through status, retry, reconciliation, OneDrive, FreeAgent, and
next-invoice fields on `invoice-records`.

See
[workflow.md#status-transitions](workflow.md#status-transitions)
for the processing sequence and retry behavior.

## Open Data Decisions

These decisions should be revisited during implementation:

- Exact partition keys after concrete query patterns are known.
- Whether expected invoice records and processing outcomes remain in one
  container.
- Whether provider-specific metadata needs separate typed records.
- Whether manual override events need their own records or can be represented by
  reconciliation fields on invoice records.
