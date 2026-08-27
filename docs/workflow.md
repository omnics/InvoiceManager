# Invoice Matching and OneDrive Reconciliation

This document describes how InvoiceManager should use invoice search criteria to
find source invoices and files that already exist in OneDrive.

## Purpose

InvoiceManager must be able to run safely every day without duplicating files or
losing track of invoices that were handled outside the service.

The workflow must account for:

- Invoices that exist in the source system but have not been saved to OneDrive.
- Invoices that appear several days away from their nominal expected date.
- Providers that expose limited metadata before the invoice PDF is opened.
- Multiple invoices from one provider for the same period.
- Files that already exist in OneDrive because of historical manual downloads,
  manual repairs, or previous partial runs.
- Database records that do not yet know about files already present in OneDrive.

## Daily Workflow

The daily controller should process each due or retryable expected invoice as
follows:

1. Load the expected invoice record and the live configuration used only for the
   active/inactive gate. Matching, source selection, reconciliation, and upload
   use the record's immutable processing snapshot.
2. Build provider-independent invoice search criteria from the expected record.
3. Ask the OneDrive integration to search the configured destination for an
   existing matching file.
4. If a OneDrive match exists, update the invoice record as reconciled from
   OneDrive and continue the downstream workflow from that saved file.
5. If no OneDrive match exists, call the source integration with the invoice
   search criteria.
6. If the source integration returns no match, record the invoice as not found
   or retryable according to the configured retry policy.
7. If the source integration returns an accepted match, use it as the retrieved
   invoice.
8. Save the retrieved invoice to OneDrive.
9. Continue with FreeAgent attachment behavior.
10. Persist state and telemetry after each meaningful step.

A separate pass, run immediately before this one on every trigger, creates the
next expected invoice record for any configuration whose most recent record has
reached a success state (see [Implementation status](#implementation-status)).

FreeAgent behavior is intentionally left at a high level here and should be
expanded when the FreeAgent integration is designed in detail.

### Implementation status

The Microsoft 365 happy path is implemented: `DueInvoiceProcessor` loads due
records (steps 1–2), retrieves a match from the Microsoft 365 source integration
(steps 5, 7), and saves the PDF to OneDrive (step 8), persisting after each step
(`Expected → Retrieved → SavedToOneDrive`). If a run fails after `Retrieved` and
before `SavedToOneDrive`, the next run treats that record as due again and
resumes the source retrieval/save path. `DueInvoiceProcessor` does not create
the next expected record itself (step 10) — `ExpectedRecordGenerator` does,
run immediately before `DueInvoiceProcessor` in both Functions entry points
(`GenerateExpectedRecordsTimer`/`GenerateExpectedRecordsHttp`). A record
reaching a success state this run is picked up on the *next* invocation of that
generator, not this one, since generation is idempotent per period - a delay of
at most one processing cycle, never a missed or duplicated record.

OneDrive reconciliation (steps 3–4) is implemented: for each due record the
processor first asks the OneDrive integration to search the configured
destination folder before calling the source. `GraphOneDriveIntegration` lists
the folder's files (paging through `@odata.nextLink`) and matches candidates by
parsing the canonical filename — the reverse of `InvoiceFilename.Generate` — to
read each file's date, amount, currency, and source id, then applies the same
date/amount/currency tolerances as source matching (the shared
`InvoiceSearchCriteria.Matches`). Names that do not follow the convention are
skipped. On a match the record is set to `ReconciledFromOneDrive` — carrying the
match reason and reconciliation timestamp — the source call and upload are
skipped (reconciliation is a success state, `Expected → ReconciledFromOneDrive`,
so `ExpectedRecordGenerator` creates the next expected record on its next run).
A search that fails technically is
treated like a retrieval failure (`RetrievalError`, always retryable). Every
Graph call honours the `Retry-After` header on throttling (HTTP 429/503)
responses and retries a bounded number of times.

The not-found / retry states are also implemented (step 6). When the source
returns no match, the record stays `Expected` while today is before its tolerance
deadline (`expectedDate + dateToleranceDays`) and moves to the terminal `NotFound`
on or after that deadline — so a record first processed after its window has
elapsed goes straight to `NotFound`. Reaching `NotFound` deliberately stops the
recurrence for that configuration: the next expected record is created only from
a success state, so a missing invoice halts the schedule on the assumption the
subscription was cancelled. Resuming a genuinely-skipped period needs manual
intervention for now (see
[Next-Expected Creation and Cancellation](domain-model.md#next-expected-creation-and-cancellation)).
A technical failure during retrieval moves
the record to `RetrievalError` (carrying the failure detail in `lastError`); a
later clean poll that still finds no match clears it back to `Expected`.
`ListDueAsync` picks up `Expected`, `RetrievalError`, and `Retrieved` records;
`RetrievalError` is always retryable with no retry limit. Each run emits
structured telemetry — a per-record logging scope (record, configuration,
integration type) plus a run summary with saved / reconciled / no-match /
not-found / failed counts — captured by Application Insights.

FreeAgent attachment (step 9) is deferred to later work.

## Search Criteria

Expected invoice records should provide the criteria used to locate an invoice:

- Expected invoice date.
- Date tolerance or date window from stored data.
- Expected amount.
- Expected currency.
- Expected VAT mode where relevant.
- Invoice name or category for filenames and reporting.

Expected values are matching criteria. They should not be overwritten by actual
values found during retrieval or reconciliation.

## Source Matching

Source integrations should receive provider-independent search criteria and
translate them into provider-specific search and matching behavior.

Source integrations own the decision about whether a source-system candidate
satisfies the supplied criteria. They should return either no match or an
accepted match with the invoice file or file reference plus actual metadata such
as invoice date, amount, currency, source invoice ID, and provider metadata. The
core workflow records and acts on that result; it does not duplicate the
provider-specific matching logic.

For Microsoft 365, amount and invoice date must both match within the configured
tolerances. Microsoft 365 does not expose a stable product identifier before the
PDF contents are opened, so product labels such as Copilot or Office extensions
must not be required as source-system match keys. Those labels are still useful
for expected invoice identity, generated filenames, logs, and reporting.

Currency must be part of every amount comparison. For example, OpenAI invoices
may be in USD while most other invoices are expected to be in GBP.

Amount matching is optional. When `amountMatchingCriteria` is absent, matching
still enforces the expected date window but does not compare amount or currency.
Azure Billing uses this mode because monthly usage can vary unpredictably.

The Azure Billing invoice-list API filters only by invoice billing period; it
does not support filtering by `invoiceDate`. The integration therefore requests
the preceding 14 months of billing periods, follows all response pages, and then
applies the configured `invoiceDate` tolerance and optional amount criteria
locally. The lookback covers annual as well as monthly invoice periods.

### Microsoft 365 Email Source Matching

`GraphEmailInvoiceSource` finds invoices carried as PDF attachments on
recurring emails, in the same delegated mailbox already used for OneDrive
uploads (an expanded `Mail.Read` scope on the same Entra app registration).
Neither the sender, the date, nor the body reveal the invoice's actual date,
total, or VAT mode, so matching happens in two stages:

1. **Find the candidate email.** Candidates are messages from the configured
   `senderEmailAddress`, with `receivedDateTime` within `dateToleranceDays` of
   the expected date, and (when configured) whose plain-text body matches the
   `bodyPattern` regex — requested via Graph's `Prefer:
   outlook.body-content-type="text"` rather than the truncated `bodyPreview`,
   so patterns match clean text. When more than one candidate matches, the one
   closest to the expected date is tried first.
2. **Extract invoice fields from the PDF.** A matched email with no PDF
   attachment logs a warning and is treated as no match (left for a later run,
   the same as an unmatched invoice). With exactly one PDF attachment, its
   fields are read via `IInvoicePdfExtractor`; with more than one, each is
   tried and the first that both extracts successfully and satisfies the
   configured date/amount tolerances (the same `InvoiceSearchCriteria.Matches`
   check every other source applies) is used. An attachment that extracts but
   doesn't satisfy those tolerances is not a failure — it's simply the wrong
   invoice, so the search moves on to the next candidate email without raising
   an error. Only a PDF that cannot be read at all is a technical failure, and
   even then the search keeps trying every remaining candidate before giving
   up: only once no candidate produces an accepted match is the search
   reported as a failure (mapped by the caller to `RetrievalError`, always
   retryable), so one candidate's unreadable PDF never blocks a further
   candidate's valid invoice from being found.

The extracted invoice date and total become `ActualInvoiceDetails`, and the
matched PDF attachment's filename (without its extension) becomes
`SourceInvoiceId` — no mailbox mutation (no move/flag/mark-as-read) is used
for deduplication. This assumes the sender's attachment naming is well
behaved: the basename must be unique per period for the given invoice
configuration (`InvoiceRecord` identity is derived from configuration ID and
expected date alone, not `SourceInvoiceId`, but a duplicate basename within
one period would still produce two indistinguishable OneDrive filenames for
that configuration) and must contain no whitespace (so the canonical
OneDrive filename stays a parseable, single-space-separated token — see
`InvoiceFilename.TryParse`). An attachment whose basename contains whitespace
is treated the same as an unreadable PDF: the source keeps trying other
candidate emails and, if none produce an accepted match, surfaces a retryable
`RetrievalError` rather than uploading a file it could not later reconcile.
`actualVatMode` is not derived from the PDF: like every other source, VAT
mode always comes from configuration. See
[domain-model.md#pdf-field-extraction](domain-model.md#pdf-field-extraction).

## OneDrive Reconciliation

Before calling a source integration, the workflow should ask the OneDrive
integration to search the configured OneDrive destination for a file that
satisfies the expected invoice criteria.

If a matching file is found, the workflow should:

- Record the OneDrive location and file identifier where available.
- Mark the invoice as reconciled from OneDrive.
- Record when reconciliation occurred.
- Record the reason the match was accepted.
- Continue downstream processing from the existing OneDrive file.

OneDrive matching should use the same date, amount, and currency tolerances as
source matching. A match by amount and date is automatic when both values match
within the configured tolerances.

The OneDrive integration owns matching against OneDrive contents. It should
return either no match or an accepted match with the OneDrive location, file
identifier where available, and match details that explain why the file was
accepted. The core workflow records the reconciliation result and continues the
downstream workflow from the matched file.

Reconciliation is normal workflow behavior, not an exceptional repair path.

## Ambiguity and Duplicates

The workflow should avoid creating duplicate OneDrive files and duplicate invoice
records.

If multiple candidate OneDrive files satisfy the same expected invoice criteria,
the OneDrive integration should still return an automatic match using the
configured tolerances. The implementation should record enough match detail to
explain which file was selected.

The service should avoid creating more than one expected invoice record for the
same configuration and period. Next-record creation must be idempotent so a retry
or manual re-run can safely detect that the next expected record already exists.

## Status Transitions

The implemented state machine for the `InvoiceWorkflowState` union — including the
`NotFound` / `RetrievalError` retry states and the tolerance-deadline rules — is
drawn in [workflow-states.md](workflow-states.md).

The broader conceptual transitions (including the still-deferred FreeAgent and
next-expected steps) are:

```text
Expected
  -> ReconciledFromOneDrive
  -> UploadedToFreeAgent
  -> NextExpectedCreated

Expected
  -> Retrieved
  -> SavedToOneDrive
  -> UploadedToFreeAgent
  -> NextExpectedCreated

Expected
  -> NotFound
  -> Expected or retryable status on a later run

Any processing step
  -> Failed
  -> Retryable status on a later run
```

The workflow should persist after each meaningful step so retries can continue
without repeating completed work.

## Audit Trail

Records updated from existing OneDrive files should preserve that they were
reconciled from OneDrive, when that happened, and why the match was accepted.

The first implementation can keep this lightweight with fields on the invoice
record, such as `reconciliationSource`, `reconciledAt`, and `matchReason`.
Possible reconciliation sources include automatic OneDrive scans, future manual
override tooling, and future migration tooling.
