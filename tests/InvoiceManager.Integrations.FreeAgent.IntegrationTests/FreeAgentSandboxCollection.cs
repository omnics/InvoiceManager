namespace InvoiceManager.Integrations.FreeAgent.IntegrationTests;

/// <summary>
/// Runs every sandbox test class in this project one at a time. Each class builds its own
/// FreeAgentTokenProvider, but they all share the single rotating refresh token in Key Vault and
/// the provider only serializes refreshes within its own instance - two classes refreshing in
/// parallel can consume the same refresh token and fail intermittently.
/// </summary>
[CollectionDefinition(Name)]
public sealed class FreeAgentSandboxCollection
{
    public const string Name = "FreeAgent sandbox";
}
