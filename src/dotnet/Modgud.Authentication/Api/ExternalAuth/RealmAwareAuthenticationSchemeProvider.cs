using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Authentication.Api.ExternalAuth;

/// <summary>
/// The framework's <see cref="AuthenticationSchemeProvider"/>, with one addition:
/// before answering a question that involves the dynamically registered external
/// schemes, it makes sure the current realm's login providers have been
/// materialised on this node from the database (ADR 0022, D6).
/// <para>
/// Three call sites matter. The authentication middleware asks
/// <see cref="GetRequestHandlerSchemesAsync"/> on every request to find a remote
/// handler for callbacks such as <c>/signin-oidc/{slug}</c>; a challenge asks
/// <see cref="GetSchemeAsync"/> by name; discovery asks <see cref="GetAllSchemesAsync"/>.
/// Each is preceded by <see cref="LoginProviderSchemeMaterializer.EnsureFreshAsync"/>,
/// which is a timestamp comparison unless the realm is stale. The realm comes
/// from the request (<c>RealmMiddleware</c> runs before authentication) or from
/// an ambient <see cref="TenantContext"/>.
/// </para>
/// <para>
/// The materializer is resolved lazily: it depends on the scheme managers, which
/// register schemes through this very provider — a constructor dependency in
/// both directions would be a cycle.
/// </para>
/// </summary>
public sealed class RealmAwareAuthenticationSchemeProvider : AuthenticationSchemeProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly Lazy<LoginProviderSchemeMaterializer> _materializer;

    public RealmAwareAuthenticationSchemeProvider(
        IOptions<AuthenticationOptions> options,
        IHttpContextAccessor httpContextAccessor,
        IServiceProvider services) : base(options)
    {
        _httpContextAccessor = httpContextAccessor;
        _materializer = new Lazy<LoginProviderSchemeMaterializer>(
            services.GetRequiredService<LoginProviderSchemeMaterializer>);
    }

    public override async Task<AuthenticationScheme?> GetSchemeAsync(string name)
    {
        if (name.StartsWith(DynamicOidcSchemeManager.SchemeNamePrefix, StringComparison.Ordinal))
            await _materializer.Value.EnsureFreshAsync(CurrentRealm());
        return await base.GetSchemeAsync(name);
    }

    public override async Task<IEnumerable<AuthenticationScheme>> GetRequestHandlerSchemesAsync()
    {
        await _materializer.Value.EnsureFreshAsync(CurrentRealm());
        return await base.GetRequestHandlerSchemesAsync();
    }

    public override async Task<IEnumerable<AuthenticationScheme>> GetAllSchemesAsync()
    {
        await _materializer.Value.EnsureFreshAsync(CurrentRealm());
        return await base.GetAllSchemesAsync();
    }

    private string? CurrentRealm()
        => _httpContextAccessor.HttpContext?.Items[TenantConstants.HttpContextTenantIdKey] as string
           ?? TenantContext.CurrentOrNull;
}
