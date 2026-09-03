namespace InvoiceManager.Core;

/// <summary>
/// Where the invoice file lives in OneDrive. <see cref="OneDriveLocation"/> is a
/// human-readable display value (a webUrl where available); <see cref="DriveId"/>
/// and <see cref="ItemId"/> are the stable Graph-addressable reference used to
/// re-download the file's bytes (see <c>IOneDriveIntegration.DownloadAsync</c>) -
/// needed because the PDF bytes themselves are never persisted between workflow
/// steps, only where to fetch them again. <see cref="FolderLocation"/> is the
/// containing folder's webUrl, sourced from the same Graph response as
/// <see cref="OneDriveLocation"/> (no extra API call) - <see cref="Core.None"/> for
/// a record saved before this field existed, or if Graph didn't report a parent.
/// </summary>
public sealed record OneDriveDetails(string OneDriveLocation, string DriveId, string ItemId, Option<string> FolderLocation);
