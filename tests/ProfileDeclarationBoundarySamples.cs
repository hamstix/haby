namespace Haby.Tests;

internal static class ProfileDeclarationBoundarySamples
{
    internal static IEnumerable<string> Sources()
    {
        // Close to the CLI file limit; default HTML escaping would exceed the
        // generated client's unchanged 4 MiB response limit.
        yield return Wrap("\"" + new string('<', 1024 * 1024 - 2048) + "\"");
        // Exact canonical numbers may require more bytes than their source spelling.
        yield return Wrap("[" + string.Join(",", Enumerable.Repeat("0", 500000)) + "]");
        // Unicode characters in the framework encoder's global block list must
        // not expand from three UTF-8 bytes to six-byte Unicode escapes.
        yield return Wrap("\"" + new string('\u3000', 348000) + "\"");
        yield return Wrap("\"" + new string('\uE000', 348000) + "\"");
        // Every omitted empty secretProjections list would otherwise add bytes.
        var defaults = Wrap("\"VALUE\"").Replace("\"staticContributions\":{}",
            "\"staticContributions\":{" + string.Join(",", Enumerable.Range(0, 256).Select(i =>
                "\"c" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\":{\"documentRef\":\"settings\",\"path\":\"/x\",\"value\":0}")) + "}", StringComparison.Ordinal);
        yield return defaults.Replace("VALUE", new string('x', 1047200 - System.Text.Encoding.UTF8.GetByteCount(defaults) + 5), StringComparison.Ordinal);
        // Four declaration containers plus sixty ordinary arrays = existing depth 64.
        yield return Wrap(new string('[', 60) + "1e999" + new string(']', 60));
    }

    // Minimal shapes leave no compensating default omission; exponent spelling
    // applies to both contract integer fields, not just ordinary values.
    internal static string IntegerBoundary(int bytes, bool secretVersion, int revision = 1000000000, string profileId = "layout")
    {
        var prefix = "{\"apiVersion\":\"haby.dev/v1alpha1\",\"kind\":\"ConfigurationProfile\",\"metadata\":{\"id\":\"" + profileId + "\",\"revision\":" +
            (revision == 1000000000 ? "1e9" : revision.ToString(System.Globalization.CultureInfo.InvariantCulture)) + "},\"spec\":{";
        var body = secretVersion
            ? "\"staticContributions\":{\"settings\":{\"documentRef\":\"settings\",\"path\":\"/x\",\"value\":{\"padding\":\"PADDING\"},\"secretProjections\":[{\"path\":\"/secret\",\"format\":\"string\",\"secretRef\":{\"name\":\"missing\",\"version\":1e9}}]}}"
            : "\"documents\":{\"settings\":{\"fileName\":\"app.json\",\"format\":\"json\",\"template\":\"PADDING\"}}";
        var source = prefix + body + "}}";
        return source.Replace("PADDING", new string('x', bytes - System.Text.Encoding.UTF8.GetByteCount(source) + 7), StringComparison.Ordinal);
    }

    private static string Wrap(string template) =>
        "{\"apiVersion\":\"haby.dev/v1alpha1\",\"kind\":\"ConfigurationProfile\",\"metadata\":{\"id\":\"layout\",\"revision\":1},\"spec\":{\"documents\":{\"settings\":{\"fileName\":\"app.json\",\"format\":\"json\",\"template\":" +
        template + "}},\"staticContributions\":{},\"bindingRules\":{}}}";
}
