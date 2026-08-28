using System.Text;
using System.Text.Json;
using InvoiceManager.AdminWeb.Pages.Configurations;
using InvoiceManager.AdminWeb.Services;
using InvoiceManager.Core;
using InvoiceManager.Infrastructure.MicrosoftAuthorization;
using InvoiceManager.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InvoiceManager.AdminWeb.Tests;

public sealed class IndexModelExportTests
{
    [Fact]
    public async Task OnGetExportAsync_ReturnsJsonFile_WithoutCosmosOrEnvironmentLocalFields()
    {
        var configuration = Configurations.Build(id: new InvoiceConfigurationId("export-me"));
        var model = CreateModel(new FakeConfigurationRepository(configuration));

        var result = await model.OnGetExportAsync("export-me", configuration.IntegrationType);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/json", file.ContentType);
        Assert.Equal("export-me.invoiceconfiguration.json", file.FileDownloadName);

        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(file.FileContents));
        var propertyNames = document.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.DoesNotContain("documentType", propertyNames);
        Assert.DoesNotContain("partitionKey", propertyNames);
        Assert.DoesNotContain("_etag", propertyNames);
        Assert.DoesNotContain("isActive", propertyNames);
        Assert.Equal("export-me", document.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task OnGetExportAsync_ReturnsNotFound_ForUnknownConfiguration()
    {
        var model = CreateModel(new FakeConfigurationRepository());

        var result = await model.OnGetExportAsync("missing", IntegrationType.MicrosoftBilling);

        Assert.IsType<NotFoundResult>(result);
    }

    private static IndexModel CreateModel(FakeConfigurationRepository repository)
    {
        var httpContext = new DefaultHttpContext();
        return new IndexModel(
            new InvoiceConfigurationService(repository),
            new FakeMicrosoftAuthorizationStore(hasTokenCache: true),
            new FakeMicrosoftResourceDiscovery(),
            new FakeInvoiceRecordResyncTrigger())
        {
            PageContext = new PageContext { HttpContext = httpContext },
        };
    }

    private sealed class FakeInvoiceRecordResyncTrigger : IInvoiceRecordResyncTrigger
    {
        public Task<InvoiceRecordResyncTriggerResult> TriggerAsync(
            InvoiceConfigurationId configurationId,
            IntegrationType integrationType,
            InvoiceConfigurationActor actor,
            bool confirmed,
            CancellationToken cancellationToken) =>
            Task.FromResult<InvoiceRecordResyncTriggerResult>(
                new InvoiceRecordResyncTriggerSucceeded(InvoiceRecordId.NewId(new DateOnly(2025, 7, 1), configurationId)));
    }

    private sealed class FakeMicrosoftAuthorizationStore(bool hasTokenCache) : IMicrosoftAuthorizationStore
    {
        public Task<bool> HasTokenCacheAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(hasTokenCache);

        public Task<byte[]?> ReadTokenCacheAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>(null);

        public Task SaveTokenCacheAsync(byte[] tokenCache, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ClearTokenCacheAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
