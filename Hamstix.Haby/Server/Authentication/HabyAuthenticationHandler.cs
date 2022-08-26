using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Principal;
using System.Text.Encodings.Web;

namespace Hamstix.Haby.Server.Authentication
{
    /// <summary>
    /// The authorization request handler for the microservices registry API.
    /// </summary>
    public class HabyAuthenticationHandler : AuthenticationHandler<HabyAuthenticationOptions>
    {
        const string BearerPrefix = "Bearer ";
        static readonly Task<AuthenticateResult> _unauthorized = Task.FromResult(AuthenticateResult.Fail("Unauthorized"));

        readonly HabyAuthenticationManager _authManager;

        public HabyAuthenticationHandler(
            IOptionsMonitor<HabyAuthenticationOptions> options,
            ILoggerFactory loggerFactory,
            UrlEncoder encoder,
            ISystemClock clock,
            HabyAuthenticationManager authManager)
            : base(options, loggerFactory, encoder, clock)
        {
            _authManager = authManager;
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey(HeaderNames.Authorization))
                return _unauthorized;

            var headerValue = Request.Headers[HeaderNames.Authorization];
            if (headerValue.Count == 0)
                return _unauthorized;
            if (!headerValue[0].StartsWith(BearerPrefix))
                return _unauthorized;

            var token = headerValue[0][BearerPrefix.Length..];
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(_authManager.SecureToken))
                return _unauthorized;

            if (token != _authManager.SecureToken)
                return _unauthorized;

            var identity = new ClaimsIdentity(Array.Empty<Claim>(), Scheme.Name);
            var principal = new GenericPrincipal(identity, null);

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
