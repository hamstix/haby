namespace Hamstix.Haby.Shared.Api.WebUi.v1.ConfigurationUnits
{
    public class ConfigurationUnitResultsViewModel
    {
        public ConfigurationUnitViewModel ConfigurationUnit { get; set; } = default!;
        public IEnumerable<ConfigurationUnitKeyResultViewModel> Results { get; set; } =
            Enumerable.Empty<ConfigurationUnitKeyResultViewModel>();
    }
}
