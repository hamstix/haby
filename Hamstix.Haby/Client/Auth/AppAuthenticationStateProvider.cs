using Hamstix.Haby.Client.Services;
using Microsoft.AspNetCore.Components.Authorization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;

namespace Hamstix.Haby.Client.Auth
{
    class AppAuthenticationStateProvider : AuthenticationStateProvider
    {
        readonly ILocalStorage _localStorage;
        readonly AuthenticationState _anonymous;
        readonly HttpClient _httpClient;
        readonly IHttpClientFactory _httpClientFactory;

        public AppAuthenticationStateProvider(ILocalStorage localStorage, HttpClient httpClient, IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
            _httpClient = httpClient;
            _localStorage = localStorage;
            _anonymous = new AuthenticationState(new ClaimsPrincipal());
        }

        public override async Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var token = await _localStorage.GetStringAsync("token");
            if (string.IsNullOrWhiteSpace(token))
                return _anonymous;
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "User"),
            }, "User");

            var user = new ClaimsPrincipal(identity);
            return new AuthenticationState(user);
        }

        public void NotifyUserAuthentication()
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "User"),
            }, "User");
            var authenticatedUser = new ClaimsPrincipal(identity);

            var authState = Task.FromResult(new AuthenticationState(authenticatedUser));
            NotifyAuthenticationStateChanged(authState);
        }

        public void NotifyUserLogout()
        {
            var authState = Task.FromResult(_anonymous);
            NotifyAuthenticationStateChanged(authState);
        }
    }
}
