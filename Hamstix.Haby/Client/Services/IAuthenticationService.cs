using Hamstix.Haby.Shared;

namespace Hamstix.Haby.Client.Services
{
    public interface IAuthenticationService
    {
        Task<AuthResultModel> Login(AclModel user);
        Task Logout();
    }
}
