using Hamstix.Haby.Client.Auth;
using Hamstix.Haby.Shared;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using System.Text;
using System.Text.Json;

namespace Hamstix.Haby.Client.Services
{
    public class AuthenticationService : IAuthenticationService
    {
        readonly HttpClient _client;
        readonly JsonSerializerOptions _options;
        readonly AuthenticationStateProvider _authStateProvider;
        readonly ILocalStorage _localStorage;
        readonly IWebAssemblyHostEnvironment _hostEnvironment;

        public AuthenticationService(HttpClient client, AuthenticationStateProvider authStateProvider, ILocalStorage localStorage, IWebAssemblyHostEnvironment hostEnvironment)
        {
            _hostEnvironment = hostEnvironment;
            _client = client;
            _options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            _authStateProvider = authStateProvider;
            _localStorage = localStorage;
        }

        public async Task<AuthResultModel> Login(AclModel user)
        {
            var content = JsonSerializer.Serialize(user);
            var bodyContent = new StringContent(content, Encoding.UTF8, "application/json");

            var message = new HttpRequestMessage
            {
                Method = HttpMethod.Post,
                Headers =
                {
                    { "Authorization", $"Bearer {user.Token}" },
                },
                RequestUri = new Uri(new Uri(_hostEnvironment.BaseAddress), "api/webui/haby/v1/acls/check-token"),
                Content = bodyContent
            };

            var authResult = await _client.SendAsync(message);

            if (!authResult.IsSuccessStatusCode)
                return new AuthResultModel { IsAuthSuccessful = false, Message = $"The access token check returns status code error: {authResult.StatusCode}" };

            var authContent = await authResult.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<AuthResultModel>(authContent, _options);
            if (!authResult.IsSuccessStatusCode)
                return result ?? new AuthResultModel { IsAuthSuccessful = false, Message = $"Check token returned error HTTP {authResult.StatusCode}" };
            if (!result.IsAuthSuccessful)
                return result;
            await _localStorage.SaveStringAsync("token", user.Token);

            ((AppAuthenticationStateProvider)_authStateProvider).NotifyUserAuthentication();
            _client.DefaultRequestHeaders.Add("Authorization", $"Bearer {user.Token}");
            return new AuthResultModel { IsAuthSuccessful = true };
        }

        public async Task Logout()
        {
            await _localStorage.RemoveAsync("token");
            ((AppAuthenticationStateProvider)_authStateProvider).NotifyUserLogout();
            _client.DefaultRequestHeaders.Authorization = null;
        }
    }
}
