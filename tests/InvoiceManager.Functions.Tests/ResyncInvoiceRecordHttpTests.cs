using InvoiceManager.Core;
using InvoiceManager.Functions.Functions;

namespace InvoiceManager.Functions.Tests;

public sealed class ResyncInvoiceRecordHttpTests
{
    [Fact]
    public void ParseRequest_Succeeds_ForValidConfigurationIdAndNamedIntegrationType()
    {
        var result = ResyncInvoiceRecordHttp.ParseRequest("config-1", "MicrosoftBilling");

        Assert.Equal(new ResyncInvoiceRecordHttp.ParsedRequest("config-1", IntegrationType.MicrosoftBilling), result);
    }

    [Fact]
    public void ParseRequest_ReturnsNull_WhenConfigurationIdIsMissing()
    {
        Assert.Null(ResyncInvoiceRecordHttp.ParseRequest(null, "MicrosoftBilling"));
        Assert.Null(ResyncInvoiceRecordHttp.ParseRequest("", "MicrosoftBilling"));
        Assert.Null(ResyncInvoiceRecordHttp.ParseRequest("   ", "MicrosoftBilling"));
    }

    [Fact]
    public void ParseRequest_ReturnsNull_WhenIntegrationTypeIsMissingOrUnrecognisedName()
    {
        Assert.Null(ResyncInvoiceRecordHttp.ParseRequest("config-1", null));
        Assert.Null(ResyncInvoiceRecordHttp.ParseRequest("config-1", "NotARealIntegrationType"));
    }

    [Fact]
    public void ParseRequest_ReturnsNull_WhenIntegrationTypeIsAnUndefinedNumericValue()
    {
        // Enum.TryParse alone accepts any integer-parseable string for a non-flags enum,
        // defined or not - this is exactly the gap that would let an out-of-range numeric
        // value reach the repository as an undefined IntegrationType.
        Assert.Null(ResyncInvoiceRecordHttp.ParseRequest("config-1", "999"));
    }
}
