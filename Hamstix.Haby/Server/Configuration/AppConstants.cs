namespace Hamstix.Haby.Server.Configuration
{
#pragma warning disable CS1591 // Missing XML comment for public visible type or member
    public static class AppConstants
    {
        public static class EnvVariables
        {
            public const string PgConnectionString = "REGISTRY_PG_CONN";
            public const string SecureToken = "SECURE_TOKEN";
        }

        public static class Configuration
        {
            public const string ConnectionString = "PostgreSQL:DefaultConnection:ConnectionString";
            public const string SecureToken = "SecureToken";
            public const string AllowAllPolicy = "AllowAll";
        }

        /// <summary>
        /// The current version of the API. Change super carefully!
        /// </summary>
        public static readonly Version CurrentApiVersion = new Version(1, 0, 0);
    }
}
