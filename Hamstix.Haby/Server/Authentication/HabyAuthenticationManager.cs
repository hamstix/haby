using Hamstix.Haby.Server.Configuration;

using System.Security.Cryptography;
using System.Text;

namespace Hamstix.Haby.Server.Authentication
{
    /// <summary>
    /// The authorization request manager for the microservices registry API.
    /// </summary>
    public class HabyAuthenticationManager
    {
        public string SecureToken { get; }

        public HabyAuthenticationManager(IConfiguration configuration)
        {
            SecureToken = GetSecureToken(configuration);
        }

        public bool ValidateToken(string? token)
        {
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(SecureToken))
                return false;

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(token),
                Encoding.UTF8.GetBytes(SecureToken));
        }

        static string GetSecureToken(IConfiguration configuration)
        {
            var secureToken = Environment.GetEnvironmentVariable(AppConstants.EnvVariables.SecureToken);
            if (!string.IsNullOrEmpty(secureToken))
                return secureToken;

            secureToken = configuration[AppConstants.Configuration.SecureToken];
            if (!string.IsNullOrEmpty(secureToken))
                return secureToken;

            return string.Empty;
        }
    }
}
