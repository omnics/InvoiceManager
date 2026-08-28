using InvoiceManager.Core;
using InvoiceManager.Functions.Functions;

namespace InvoiceManager.Functions.Tests;

public sealed class ResyncInvoiceRecordHttpTests
{
    [Fact]
    public void ParseRequest_Succeeds_ForValidConfigurationIdAndNamedIntegrationType()
    {
        var result = ResyncInvoiceRecordHttp.ParseRequest(
            "config-1", "MicrosoftBilling", "actor-oid", "Actor Name", "true");

        Assert.Equal(
            new ResyncInvoiceRecordHttp.ParsedRequest(
                new InvoiceConfigurationId("config-1"),
                IntegrationType.MicrosoftBilling,
                new InvoiceConfigurationActor("actor-oid", "Actor Name"),
                true),
            result);
    }

    [Fact]
    public void ParseRequest_ReturnsNone_WhenConfigurationIdIsMissing()
    {
        Assert.True(ResyncInvoiceRecordHttp.ParseRequest(null, "MicrosoftBilling", "actor-oid", "Actor Name", "true") is None);
        Assert.True(ResyncInvoiceRecordHttp.ParseRequest("", "MicrosoftBilling", "actor-oid", "Actor Name", "true") is None);
        Assert.True(ResyncInvoiceRecordHttp.ParseRequest("   ", "MicrosoftBilling", "actor-oid", "Actor Name", "true") is None);
    }

    [Fact]
    public void ParseRequest_ReturnsNone_WhenIntegrationTypeIsMissingOrUnrecognisedName()
    {
        Assert.True(ResyncInvoiceRecordHttp.ParseRequest("config-1", null, "actor-oid", "Actor Name", "true") is None);
        Assert.True(ResyncInvoiceRecordHttp.ParseRequest("config-1", "NotARealIntegrationType", "actor-oid", "Actor Name", "true") is None);
    }

    [Fact]
    public void ParseRequest_ReturnsNone_WhenIntegrationTypeIsAnUndefinedNumericValue()
    {
        // Enum.TryParse alone accepts any integer-parseable string for a non-flags enum,
        // defined or not - this is exactly the gap that would let an out-of-range numeric
        // value reach the repository as an undefined IntegrationType.
        Assert.True(ResyncInvoiceRecordHttp.ParseRequest("config-1", "999", "actor-oid", "Actor Name", "true") is None);
    }

    [Fact]
    public void ParseRequest_ReturnsNone_WhenActorFieldsAreMissing()
    {
        Assert.True(ResyncInvoiceRecordHttp.ParseRequest("config-1", "MicrosoftBilling", null, "Actor Name", "true") is None);
        Assert.True(ResyncInvoiceRecordHttp.ParseRequest("config-1", "MicrosoftBilling", "actor-oid", null, "true") is None);
        Assert.True(ResyncInvoiceRecordHttp.ParseRequest("config-1", "MicrosoftBilling", "  ", "Actor Name", "true") is None);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-bool")]
    [InlineData("false")]
    public void ParseRequest_TreatsMissingOrUnparseableConfirmed_AsNotConfirmed(string? confirmedText)
    {
        var result = ResyncInvoiceRecordHttp.ParseRequest("config-1", "MicrosoftBilling", "actor-oid", "Actor Name", confirmedText);

        Assert.True(result is ResyncInvoiceRecordHttp.ParsedRequest { Confirmed: false });
    }

    [Fact]
    public void ParseRequest_ParsesConfirmedTrue()
    {
        var result = ResyncInvoiceRecordHttp.ParseRequest("config-1", "MicrosoftBilling", "actor-oid", "Actor Name", "true");

        Assert.True(result is ResyncInvoiceRecordHttp.ParsedRequest { Confirmed: true });
    }
}
