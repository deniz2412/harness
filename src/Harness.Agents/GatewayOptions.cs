namespace Harness.Agents;

public sealed class GatewayOptions
{
    /// <summary>OpenAI-compatible endpoint of the model gateway (LiteLLM).</summary>
    public string BaseUrl { get; set; } = "http://gateway:4000";
    public string ApiKey { get; set; } = "local-dev-master";
    /// <summary>Model tiering: cheap for gather-type nodes, strong for review/implement.</summary>
    public string CheapModel { get; set; } = "cheap";
    public string StrongModel { get; set; } = "strong";
}
