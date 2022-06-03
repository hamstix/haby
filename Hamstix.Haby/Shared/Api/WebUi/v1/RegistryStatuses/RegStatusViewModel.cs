
namespace Hamstix.Haby.Shared.Api.WebUi.v1.RegistryStatuses
{
    public class RegStatusViewModel
    {
        /// <summary>
        /// Registry status.
        /// </summary>
        public RegStatuses Status { get; set; }

        /// <summary>
        /// Error message, if present.
        /// </summary>
        public string Message { get; set; } = default!;

        /// <summary>
        /// Registry version.
        /// </summary>
        public string Version { get; set; } = default!;

        /// <summary>
        /// Registry public Api verion.
        /// </summary>
        public string ApiVersion { get; set; } = default!;

        /// <summary>
        /// The flag indicates whether the database schema has been initialized.
        /// </summary>
        /// <value>
        ///   <c>true</c> if [database schema initilized]; otherwise, <c>false</c>.
        /// </value>
        public bool DbSchemaInitialized { get; set; }

        /// <summary>
        /// Gets or sets the name of the environment. The host automatically sets this property
        /// to the value of the "environment" key as specified in configuration.
        /// </summary>
        public string Environment { get; set; } = default!;
    }
}
