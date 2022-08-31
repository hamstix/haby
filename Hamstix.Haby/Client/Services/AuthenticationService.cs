using Grpc.Core;
using Hamstix.Haby.Client.Auth;
using Hamstix.Haby.Shared.Grpc.System;
using Microsoft.AspNetCore.Components.Authorization;

namespace Hamstix.Haby.Client.Services;

public class AuthenticationService : IAuthenticationService
{
    readonly HttpClient _client;
    readonly AuthenticationStateProvider _authStateProvider;
    readonly ILocalStorage _localStorage;
    readonly SystemService.SystemServiceClient _systemService;

    public AuthenticationService(HttpClient client,
        AuthenticationStateProvider authStateProvider,
        ILocalStorage localStorage,
        SystemService.SystemServiceClient systemService)
    {
        _systemService = systemService;
        _client = client;
        _authStateProvider = authStateProvider;
        _localStorage = localStorage;
    }

    public async Task<AuthResultModel> Login(AclModel user)
    {
        var metadata = new Grpc.Core.Metadata();
        metadata.Add("Authorization", $"Bearer {user.Token}");

        var model = new AclModel
        {
            Token = user.Token,
        };
        AuthResultModel result;
        try
        {
            result = await _systemService.CheckAuthTokenAsync(model, metadata);
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.Unauthenticated)
        {
            return new AuthResultModel { IsAuthSuccessful = false, Message = $"The access token check returns status code error: {e.StatusCode}" };
        }

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
