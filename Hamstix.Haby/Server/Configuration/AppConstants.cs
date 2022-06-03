using System;
namespace Hamstix.Haby.Server.Configuration
{
#pragma warning disable CS1591 // Missing XML comment for public visible type or member
    public static class AppConstants
    {
        public static class RegConfiguration
        {
            public const string Initialized = "initialized";
        }

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
            public const string DisableSslVerification = "DisableSslVertification";
        }

        public const string AuthenticationSchemeName = "MsReg";

        /// <summary>
        /// The name of the setting that controls the Kubernetes configuration unit application service.
        /// </summary>
        public const string SkipK8sQueryParameter = "skipK8s";

        /// <summary>
        /// The name of the setting that controls the Kubernetes configuration unit application service.
        /// </summary>
        public const string K8sMergeTypeQueryParameter = "k8sMergeType";

        /// <summary>
        /// The current version of the API. Change super carefully!
        /// </summary>
        public static readonly Version CurrentApiVersion = new Version(1, 0, 0);
    }
}
