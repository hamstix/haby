using Hamstix.Haby.Server.Configuration;

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
