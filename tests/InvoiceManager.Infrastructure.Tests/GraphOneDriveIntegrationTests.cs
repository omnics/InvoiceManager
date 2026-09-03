using System.Globalization;
using System.Net;
using InvoiceManager.Core;
using InvoiceManager.Core.Integrations;
using InvoiceManager.Infrastructure.OneDrive;
using InvoiceManager.TestSupport;
using NodaMoney;

namespace InvoiceManager.Infrastructure.Tests;

public sealed class GraphOneDriveIntegrationTests
{
    private static readonly OneDriveFolder Folder =
        new("drive-1", "Drive One", "folder-1", "/Bills/Microsoft 365");

    private static readonly InvoiceFilename Filename = new(
        new InvoiceFilenameSettings { Culture = CultureInfo.GetCultureInfo("en-GB") });

    [Fact]
    public async Task UploadAsync_PutsPdfToGraphContentEndpoint_AndReturnsWebUrl()
    {
        var handler = new StubHttpMessageHandler((_, _) => Json(
            HttpStatusCode.Created,
            """{ "id": "01ABCDEF", "webUrl": "https://contoso-my.sharepoint.com/invoice.pdf" }"""));
        using var httpClient = new HttpClient(handler);
        var integration = Build(httpClient);

        var pdf = new byte[] { 1, 2, 3 };
        var result = await integration.UploadAsync(new OneDriveUploadRequest(
            Folder,
            "2025-07-12 Microsoft 365 Business Basic G152207778 £11.59 exc.pdf",
            pdf));

        Assert.Equal("https://contoso-my.sharepoint.com/invoice.pdf", result.OneDriveLocation);
        Assert.Equal("drive-1", result.DriveId);
        Assert.Equal("01ABCDEF", result.ItemId);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.StartsWith(
            "https://graph.microsoft.com/v1.0/drives/drive-1/items/folder-1:/",
            Uri.UnescapeDataString(request.RequestUri!.ToString()));
        Assert.EndsWith(":/content", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task UploadAsync_SendsBearerToken_ForGraphFilesScope()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Json(HttpStatusCode.OK, """{ "id": "1", "webUrl": "https://example/x.pdf" }"""));
        using var httpClient = new HttpClient(handler);
        var tokenProvider = new FakeMicrosoftTokenProvider("graph-token");
        var integration = Build(httpClient, tokenProvider);

        await integration.UploadAsync(new OneDriveUploadRequest(Folder, "x.pdf", [9]));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer graph-token", request.Authorization);
        var scopes = Assert.Single(tokenProvider.RequestedScopes);
        Assert.Contains("https://graph.microsoft.com/Files.ReadWrite.All", scopes);
    }

    [Fact]
    public async Task UploadAsyncAndSearchAsync_UseStableDriveItemEndpoints()
    {
        var handler = new StubHttpMessageHandler((_, index) => index == 0
            ? Json(HttpStatusCode.Created, """{"id":"file","webUrl":"https://example/file"}""")
            : Json(HttpStatusCode.OK, Children(nextLink: null)));
        var integration = Build(new HttpClient(handler));
        var folder = new OneDriveFolder("drive-id", "Drive", "folder-id", "/Bills/Renamed");

        await integration.UploadAsync(new OneDriveUploadRequest(folder, "invoice.pdf", [1]));
        await integration.SearchAsync(new OneDriveSearchRequest(folder, Criteria()));

        Assert.Contains("/drives/drive-id/items/folder-id:/invoice.pdf:/content", handler.Requests[0].RequestUri!.ToString());
        Assert.EndsWith("/drives/drive-id/items/folder-id/children", handler.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task UploadAsync_Throws_WhenGraphReturnsError()
    {
        var handler = new StubHttpMessageHandler((_, _) => Json(HttpStatusCode.Forbidden, "denied"));
        using var httpClient = new HttpClient(handler);
        var integration = Build(httpClient);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            integration.UploadAsync(new OneDriveUploadRequest(Folder, "x.pdf", [1])));
    }

    [Fact]
    public async Task SearchAsync_ReturnsMatch_WithParsedDetailsAndReason_WhenAFileSatisfiesCriteria()
    {
        var handler = new StubHttpMessageHandler((_, _) => Json(HttpStatusCode.OK, Children(
            nextLink: null,
            ("2026-07-10 Microsoft 365 Business Basic G152207778 £11.59 exc.pdf", "id-1", "https://example/id-1"))));
        using var httpClient = new HttpClient(handler);
        var integration = Build(httpClient);

        var result = await integration.SearchAsync(new OneDriveSearchRequest(Folder, Criteria()));

        var match = AssertMatch(result);
        Assert.Equal("https://example/id-1", match.OneDriveDetails.OneDriveLocation);
        Assert.Equal("drive-1", match.OneDriveDetails.DriveId);
        Assert.Equal("id-1", match.OneDriveDetails.ItemId);
        Assert.Equal(new DateOnly(2026, 7, 10), match.Details.ActualInvoiceDate);
        Assert.Equal(new Money(11.59m, "GBP"), match.Details.ActualAmount);
        Assert.Equal("G152207778", match.Details.SourceInvoiceId.Value);
        Assert.Contains("2026-07-10", match.MatchReason);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.EndsWith("/children", request.RequestUri!.ToString());
    }

    [Theory]
    [InlineData("2026-07-13", true)]   // three days late: on the tolerance boundary.
    [InlineData("2026-07-14", false)]  // four days late: just outside the window.
    public async Task SearchAsync_HonoursDateTolerance(string fileDate, bool expectMatch)
    {
        var handler = new StubHttpMessageHandler((_, _) => Json(HttpStatusCode.OK, Children(
            nextLink: null,
            ($"{fileDate} Microsoft 365 Business Basic G152207778 £11.59 exc.pdf", "id-1", "https://example/id-1"))));
        using var httpClient = new HttpClient(handler);
        var integration = Build(httpClient);

        var result = await integration.SearchAsync(new OneDriveSearchRequest(Folder, Criteria()));

        Assert.Equal(expectMatch, result is OneDriveMatch);
    }

    [Fact]
    public async Task SearchAsync_ReturnsNoMatch_WhenCurrencyDiffers()
    {
        var handler = new StubHttpMessageHandler((_, _) => Json(HttpStatusCode.OK, Children(
            nextLink: null,
            ("2026-07-10 Microsoft 365 Business Basic G152207778 €11.59 exc.pdf", "id-1", "https://example/id-1"))));
        using var httpClient = new HttpClient(handler);
        var integration = Build(httpClient);

        var result = await integration.SearchAsync(new OneDriveSearchRequest(Folder, Criteria()));

        Assert.True(result is NoOneDriveMatch, $"Expected NoOneDriveMatch but got {result}.");
    }

    [Fact]
    public async Task SearchAsync_IgnoresNearMissAndUnrelatedFiles_AndReturnsTheValidMatch()
    {
        var handler = new StubHttpMessageHandler((_, _) => Json(HttpStatusCode.OK, Children(
            nextLink: null,
            ("report.pdf", "id-0", "https://example/id-0"),
            ("2026-7-10 Microsoft 365 Business Basic G1 £11.59 exc.pdf", "id-1", "https://example/id-1"),
            ("2026-07-10 Microsoft 365 Business Basic G152207778 £11.59 exc.pdf", "id-2", "https://example/id-2"))));
        using var httpClient = new HttpClient(handler);
        var integration = Build(httpClient);

        var result = await integration.SearchAsync(new OneDriveSearchRequest(Folder, Criteria()));

        var match = AssertMatch(result);
        Assert.Equal("https://example/id-2", match.OneDriveDetails.OneDriveLocation);
    }

    [Fact]
    public async Task SearchAsync_PicksClosestByDate_WhenSeveralCandidatesMatch()
    {
        var handler = new StubHttpMessageHandler((_, _) => Json(HttpStatusCode.OK, Children(
            nextLink: null,
            ("2026-07-08 Microsoft 365 Business Basic G1 £11.59 exc.pdf", "id-far", "https://example/far"),
            ("2026-07-11 Microsoft 365 Business Basic G2 £11.59 exc.pdf", "id-near", "https://example/near"))));
        using var httpClient = new HttpClient(handler);
        var integration = Build(httpClient);

        var result = await integration.SearchAsync(new OneDriveSearchRequest(Folder, Criteria()));

        var match = AssertMatch(result);
        Assert.Equal("https://example/near", match.OneDriveDetails.OneDriveLocation);
    }

    [Fact]
    public async Task SearchAsync_FollowsPaging_AcrossNextLink()
    {
        var page2Url = "https://graph.microsoft.com/v1.0/drives/drive-1/root/children?$skiptoken=abc";
        var handler = new StubHttpMessageHandler((_, index) => index switch
        {
            0 => Json(HttpStatusCode.OK, Children(
                nextLink: page2Url,
                ("report.pdf", "id-0", "https://example/id-0"))),
            _ => Json(HttpStatusCode.OK, Children(
                nextLink: null,
                ("2026-07-10 Microsoft 365 Business Basic G152207778 £11.59 exc.pdf", "id-1", "https://example/id-1"))),
        });
        using var httpClient = new HttpClient(handler);
        var integration = Build(httpClient);

        var result = await integration.SearchAsync(new OneDriveSearchRequest(Folder, Criteria()));

        var match = AssertMatch(result);
        Assert.Equal("https://example/id-1", match.OneDriveDetails.OneDriveLocation);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(page2Url, handler.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task SearchAsync_ReturnsMatch_WhenFileOmitsTheVatIndicator()
    {
        // A manually-saved file may lack the trailing "inc"/"exc" indicator. Matching is on
        // date, amount, and description, so the file still reconciles.
        var handler = new StubHttpMessageHandler((_, _) => Json(HttpStatusCode.OK, Children(
            nextLink: null,
            ("2026-07-10 Microsoft 365 Business Basic G152207778 £11.59.pdf", "id-1", "https://example/id-1"))));
        using var httpClient = new HttpClient(handler);
        var integration = Build(httpClient);

        var result = await integration.SearchAsync(new OneDriveSearchRequest(Folder, Criteria()));

        var match = AssertMatch(result);
        Assert.Equal("https://example/id-1", match.OneDriveDetails.OneDriveLocation);
        Assert.Equal(new Money(11.59m, "GBP"), match.Details.ActualAmount);
    }

    [Fact]
    public async Task SearchAsync_MatchesDescriptionFreeFileByDateOnly_WhenAmountCriteriaAreAbsent()
    {
        var handler = new StubHttpMessageHandler((_, _) => Json(HttpStatusCode.OK, Children(
            nextLink: null,
            ("2026-07-10 G152207778 £999.99.pdf", "id-1", "https://example/id-1"))));
        using var httpClient = new HttpClient(handler);
        var integration = Build(httpClient);

        var criteria = new OneDriveSearchCriteria(
            new DateOnly(2026, 7, 10), 3, Option.None, "");
        var result = await integration.SearchAsync(new OneDriveSearchRequest(Folder, criteria));

        var match = AssertMatch(result);
        Assert.Equal("G152207778", match.Details.SourceInvoiceId.Value);
    }

    [Fact]
    public async Task SearchAsync_ReturnsNoMatch_WhenDescriptionDiffers()
    {
        // Same date, amount, and currency, but a different subscription's file sharing
        // the folder: the description must match, so this is not reconciled.
        var handler = new StubHttpMessageHandler((_, _) => Json(HttpStatusCode.OK, Children(
            nextLink: null,
            ("2026-07-10 Microsoft 365 Copilot G152207778 £11.59 exc.pdf", "id-1", "https://example/id-1"))));
        using var httpClient = new HttpClient(handler);
        var integration = Build(httpClient);

        var result = await integration.SearchAsync(new OneDriveSearchRequest(Folder, Criteria()));

        Assert.True(result is NoOneDriveMatch, $"Expected NoOneDriveMatch but got {result}.");
    }

    [Fact]
    public async Task SearchAsync_Throws_WhenDestinationFolderDoesNotExist()
    {
        // Destinations are addressed by stable item ID, so a 404 means the configured folder
        // was deleted or moved after the ID was captured — ID-based addressing cannot recreate
        // it, unlike the old path-based behavior. This must surface as a retrieval failure, not
        // be swallowed as "no match".
        var handler = new StubHttpMessageHandler((_, _) => Json(
            HttpStatusCode.NotFound,
            """{ "error": { "code": "itemNotFound", "message": "The resource could not be found." } }"""));
        using var httpClient = new HttpClient(handler);
        var integration = Build(httpClient);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            integration.SearchAsync(new OneDriveSearchRequest(Folder, Criteria())));
    }

    [Fact]
    public async Task SearchAsync_Throws_WhenGraphReturnsAGenuineError()
    {
        // A non-404 failure (e.g. 403 Forbidden) is a real fault the caller must surface
        // as a retrieval error, not swallow as "no match".
        var handler = new StubHttpMessageHandler((_, _) => Json(HttpStatusCode.Forbidden, "denied"));
        using var httpClient = new HttpClient(handler);
        var integration = Build(httpClient);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            integration.SearchAsync(new OneDriveSearchRequest(Folder, Criteria())));
    }

    [Fact]
    public async Task DownloadAsync_GetsContentEndpoint_ByDriveAndItemId_AndReturnsBytes()
    {
        var pdf = new byte[] { 1, 2, 3, 4 };
        var handler = new StubHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pdf) });
        using var httpClient = new HttpClient(handler);
        var integration = Build(httpClient);

        var result = await integration.DownloadAsync(new OneDriveDetails("https://example/id-1", "drive-1", "item-1"));

        Assert.Equal(pdf, result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(
            "https://graph.microsoft.com/v1.0/drives/drive-1/items/item-1/content",
            request.RequestUri!.ToString());
    }

    [Fact]
    public async Task DownloadAsync_Throws_WhenGraphReturnsError()
    {
        var handler = new StubHttpMessageHandler((_, _) => Json(HttpStatusCode.NotFound, "not found"));
        using var httpClient = new HttpClient(handler);
        var integration = Build(httpClient);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            integration.DownloadAsync(new OneDriveDetails("https://example/id-1", "drive-1", "item-1")));
    }

    private static OneDriveMatch AssertMatch(OneDriveSearchResult result) =>
        result is OneDriveMatch match
            ? match
            : throw new Xunit.Sdk.XunitException($"Expected OneDriveMatch but got {result}.");

    private static GraphOneDriveIntegration Build(
        HttpClient httpClient,
        FakeMicrosoftTokenProvider? tokenProvider = null) =>
        new(httpClient, tokenProvider ?? new FakeMicrosoftTokenProvider(), Filename);

    private static OneDriveSearchCriteria Criteria() => new(
        ExpectedDate: new DateOnly(2026, 7, 10),
        DateToleranceDays: 3,
        AmountMatchingCriteria: new AmountMatchingCriteria(new Money(11.59m, "GBP"), 0m),
        InvoiceDescription: "Microsoft 365 Business Basic");

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static string Children(string? nextLink, params (string Name, string Id, string WebUrl)[] items)
    {
        var values = string.Join(",", items.Select(i =>
            $$"""{ "id": "{{i.Id}}", "name": {{System.Text.Json.JsonSerializer.Serialize(i.Name)}}, "webUrl": "{{i.WebUrl}}" }"""));
        var next = nextLink is null ? "" : $""", "@odata.nextLink": "{nextLink}" """;
        return $$"""{ "value": [{{values}}]{{next}} }""";
    }
}
