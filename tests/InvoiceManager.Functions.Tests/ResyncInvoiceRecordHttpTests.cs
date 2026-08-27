using InvoiceManager.Core;
using InvoiceManager.Functions.Functions;

namespace InvoiceManager.Functions.Tests;

public sealed class ResyncInvoiceRecordHttpTests
{
    [Fact]
    public void ParseRequest_Succeeds_ForValidConfigurationIdAndNamedIntegrationType()
    {
        var result = ResyncInvoiceRecordHttp.ParseRequest("config-1", "MicrosoftBilling");

        Assert.Equal(
            new ResyncInvoiceRecordHttp.ParsedRequest(new InvoiceConfigurationId("config-1"), IntegrationType.MicrosoftBilling),
            result);
    }

    [Fact]
    public void ParseRequest_ReturnsNone_WhenConfigurationIdIsMissing()
    {
        Assert.True(ResyncInvoiceRecordHttp.ParseRequest(null, "MicrosoftBilling") is None);
        Assert.True(ResyncInvoiceRecordHttp.ParseRequest("", "MicrosoftBilling") is None);
        Assert.True(ResyncInvoiceRecordHttp.ParseRequest("   ", "MicrosoftBilling") is None);
    }

    [Fact]
    public void ParseRequest_ReturnsNone_WhenIntegrationTypeIsMissingOrUnrecognisedName()
    {
        Assert.True(ResyncInvoiceRecordHttp.ParseRequest("config-1", null) is None);
        Assert.True(ResyncInvoiceRecordHttp.ParseRequest("config-1", "NotARealIntegrationType") is None);
    }

    [Fact]
    public void ParseRequest_ReturnsNone_WhenIntegrationTypeIsAnUndefinedNumericValue()
    {
        // Enum.TryParse alone accepts any integer-parseable string for a non-flags enum,
        // defined or not - this is exactly the gap that would let an out-of-range numeric
        // value reach the repository as an undefined IntegrationType.
        Assert.True(ResyncInvoiceRecordHttp.ParseRequest("config-1", "999") is None);
    }
}
