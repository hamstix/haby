using Microsoft.AspNetCore.Components.Authorization;
using System.Security.Claims;

namespace Hamstix.Haby.Client.Auth;

public sealed class CookieAuthenticationStateProvider : AuthenticationStateProvider
{
    static readonly AuthenticationState Anonymous = new(new ClaimsPrincipal());

    readonly HttpClient _httpClient;

    public CookieAuthenticationStateProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var response = await _httpClient.GetAsync("auth/user");
        if (!response.IsSuccessStatusCode)
            return Anonymous;

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "Haby user")],
            "HabyCookie");
        return new AuthenticationState(new ClaimsPrincipal(identity));
    }
}
