namespace Hamstix.Haby.Shared.PluginsCore
{
    public class ConfigurationResult
    {
        public Service Service { get; private set; }
        public ConfigurationResultStatuses Status { get; set; }
        public string? ErrorMessage { get; set; }

        public ConfigurationResult(Service service)
        {
            Service = service;
        }
    }
}
