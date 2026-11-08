using System.Security.Claims;
using System.Text.Encodings.Web;
using Haby.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Haby.Operator.Host;

/// <summary>Central operator-admin authentication, also used by the authorization fallback policy.</summary>
internal sealed class OperatorAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, IOperatorAccessStore store)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "operator-admin";
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Delivery has its own authorization contract and must never consume operator credentials.
        if (Context.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null)
            return AuthenticateResult.NoResult();
        var values = Request.Headers.Authorization;
        if (values.Count != 1 || values[0] is not { } header || !header.StartsWith("Bearer ", StringComparison.Ordinal) ||
            !OperatorCredential.TryIdentify(header[7..], out _)) return AuthenticateResult.Fail("Operator access denied.");
        try
        {
            var grant = await store.AuthorizeAsync(header[7..], Context.RequestAborted);
            if (grant is null) return AuthenticateResult.Fail("Operator access denied.");
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, OperatorCredential.Actor(grant.Id))], SchemeName);
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
        }
        catch (Exception) { return AuthenticateResult.Fail("Operator access denied."); }
    }
}
