namespace InvoiceManager.Core;

/// <summary>
/// Where the invoice file lives in OneDrive. <see cref="OneDriveLocation"/> is a
/// human-readable display value (a webUrl where available); <see cref="DriveId"/>
/// and <see cref="ItemId"/> are the stable Graph-addressable reference used to
/// re-download the file's bytes (see <c>IOneDriveIntegration.DownloadAsync</c>) -
/// needed because the PDF bytes themselves are never persisted between workflow
/// steps, only where to fetch them again.
/// </summary>
public sealed record OneDriveDetails(string OneDriveLocation, string DriveId, string ItemId);
