namespace Hamstix.Haby.Server.Models
{
    public class RegConfiguration
    {
        public string Key { get; set; }
        public string Value { get; set; }

        public RegConfiguration(string key, string value)
        {
            Key = key;
            Value = value;
        }

        RegConfiguration() { }
    }
}
