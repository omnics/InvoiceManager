using InvoiceManager.Core;

namespace InvoiceManager.TestSupport;

public static class Records
{
    public static InvoiceRecord Build(
        InvoiceConfiguration? config = null,
        DateOnly? expectedDate = null,
        InvoiceWorkflowState? state = null)
    {
        var resolvedConfig = config ?? Configurations.Build();
        return new InvoiceRecord(
            resolvedConfig.Id,
            expectedDate ?? resolvedConfig.StartDate,
            state ?? new Expected(Option.None),
            InvoiceProcessingSnapshot.FromConfiguration(resolvedConfig));
    }
}
