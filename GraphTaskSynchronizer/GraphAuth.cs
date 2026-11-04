using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GraphTaskSynchronizer;

/// <summary>
/// App-only (client credentials) tokens for Graph. ClientSecretCredential caches the token
/// and refreshes it before expiry, so asking for one per request is cheap.
/// </summary>
public sealed class ClientCredentialTokenProvider : IGraphTokenProvider
{
    private static readonly TokenRequestContext GraphScope = new(new[] { "https://graph.microsoft.com/.default" });

    private readonly TokenCredential _credential;

    public ClientCredentialTokenProvider(TokenCredential credential) => _credential = credential;

    public async Task<string> GetTokenAsync(CancellationToken ct) =>
        (await _credential.GetTokenAsync(GraphScope, ct)).Token;

    public static ClientCredentialTokenProvider FromConfiguration(IConfiguration config, ILoggerFactory loggerFactory)
    {
        var tenantId = config["AzureAd:TenantId"] ?? throw new InvalidOperationException("AzureAd:TenantId missing");
        var clientId = config["AzureAd:ClientId"] ?? throw new InvalidOperationException("AzureAd:ClientId missing");
        var clientSecret = config["AzureAd:ClientSecret"] ?? throw new InvalidOperationException("AzureAd:ClientSecret missing");

        loggerFactory.CreateLogger<ClientCredentialTokenProvider>()
            .LogInformation("[Auth] Using client credentials for tenant {TenantId}, app {ClientId}.", tenantId, clientId);

        return new ClientCredentialTokenProvider(new ClientSecretCredential(tenantId, clientId, clientSecret));
    }
}
