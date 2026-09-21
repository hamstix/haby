using Hamstix.Haby.Shared.Grpc.System;
using System.Net;
using System.Net.Http.Json;

namespace Hamstix.Haby.Client.Services;

public sealed class AuthenticationService : IAuthenticationService
{
    readonly HttpClient _client;

    public AuthenticationService(HttpClient client)
    {
        _client = client;
    }

    public async Task<AuthResultModel> Login(AclModel user)
    {
        var response = await _client.PostAsJsonAsync("auth/login", user);
        return response.IsSuccessStatusCode
            ? new AuthResultModel { IsAuthSuccessful = true }
            : new AuthResultModel
            {
                IsAuthSuccessful = false,
                Message = response.StatusCode == HttpStatusCode.Unauthorized
                    ? "The access token is invalid."
                    : $"Login failed with status code {response.StatusCode}."
            };
    }

    public async Task Logout()
    {
        await _client.PostAsync("auth/logout", null);
    }
}
