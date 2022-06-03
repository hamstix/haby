namespace Hamstix.Haby.Shared.PluginsCore
{
    public class Plugin
    {
        public Plugin(string name)
        {
            Name = name;
        }

        /// <summary>
        /// The name of the plugin to be used in the system.
        /// </summary>
        public string Name { get; init; }

        /// <summary>
        /// The type to be used as handler for the plugin.
        /// </summary>
        public Type StrategyType { get; init; }
    }
}
