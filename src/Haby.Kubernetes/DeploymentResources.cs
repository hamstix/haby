using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Haby.Kubernetes.Contracts;
using static Haby.Kubernetes.DeploymentIntents;

namespace Haby.Kubernetes;

/// <summary>Ownership is per CPU/memory leaf of the named main container. API-defaulted
/// requests follow limits only when the accepted request was absent; evidence retains absence.</summary>
internal static class DeploymentResources
{
    private static readonly string[] Groups = ["requests", "limits"];
    private static readonly string[] Dimensions = ["cpu", "memory"];

    internal static JsonObject Json(ResourceRequirements resources)
    {
        var result = new JsonObject();
        foreach (var group in Groups)
        {
            var quantities = group == "requests" ? resources.Requests : resources.Limits;
            if (quantities is null) continue;
            var map = new JsonObject();
            if (quantities.Cpu is { } cpu) map["cpu"] = cpu;
            if (quantities.Memory is { } memory) map["memory"] = memory;
            result[group] = map;
        }
        return result;
    }

    internal static void Validate(ResourceRequirements? resources)
    {
        if (resources is null) return;
        // Reuse the closed portable schema AND semantic validation, including quantities/order.
        KubernetesContract.DecodeWorkload(Encoding.UTF8.GetBytes(new JsonObject
        {
            ["image"] = new JsonObject { ["reference"] = "registry.example/validation:v1" },
            ["overrides"] = new JsonObject { ["resources"] = Json(resources) }
        }.ToJsonString()));
    }

    private static string? Input(ResourceRequirements? resources, string group, string dimension)
    {
        var quantities = group == "requests" ? resources?.Requests : resources?.Limits;
        return dimension == "cpu" ? quantities?.Cpu : quantities?.Memory;
    }
    private static string? Effective(ResourceRequirements? resources, string group, string dimension) =>
        Input(resources, group, dimension) ?? (group == "requests" ? Input(resources, "limits", dimension) : null);

    private static JsonObject? Observed(JsonNode main)
    {
        Need(main["resources"] is null or JsonObject, "resource-shape");
        var resources = main["resources"] as JsonObject;
        foreach (var group in Groups) Need(resources?[group] is null or JsonObject, "resource-shape");
        return resources;
    }

    internal static void Matches(JsonNode main, ResourceRequirements? requirements)
    {
        var observed = Observed(main);
        foreach (var group in Groups)
            foreach (var dimension in Dimensions)
                if (Effective(requirements, group, dimension) is { } expected)
                {
                    var value = observed?[group]?[dimension];
                    Need(value is JsonValue text && text.TryGetValue<string>(out var quantity) &&
                        Equivalent(expected, quantity), "resource-drift");
                }
    }

    internal static void Removed(JsonNode main, ResourceRequirements? previous, ResourceRequirements? target)
    {
        var observed = Observed(main);
        foreach (var group in Groups)
            foreach (var dimension in Dimensions)
                if (Effective(previous, group, dimension) is not null && Effective(target, group, dimension) is null)
                    Need(observed?[group]?[dimension] is null, "removed-resource-present");
    }

    internal static void Patch(JsonNode main, ResourceRequirements? previous, ResourceRequirements? target,
        string path, Action<string, string, JsonNode?> add)
    {
        var observed = Observed(main);
        if (observed is null && target is not null) add("add", path, new JsonObject());
        foreach (var group in Groups)
        {
            var writes = Dimensions.Where(d => Input(target, group, d) is not null).ToArray();
            if (observed?[group] is null && writes.Length > 0) add("add", path + "/" + group, new JsonObject());
            foreach (var dimension in Dimensions)
            {
                var old = Effective(previous, group, dimension);
                var next = Effective(target, group, dimension);
                var live = observed?[group]?[dimension];
                // A new owned leaf cannot take an existing injected value, even if equivalent.
                Need(old is not null || next is null || live is null, "resource-collision");
                var leaf = path + "/" + group + "/" + dimension;
                if (Input(target, group, dimension) is { } value) add("add", leaf, JsonValue.Create(value));
                else if (old is not null && live is not null) add("remove", leaf, null);
            }
        }
    }

    // Kubernetes Quantity uses a decimal coefficient plus decimal/SI or binary suffix.
    // Parse readback exactly as a rational; never use a floating-point conversion or SDK rounding.
    internal static bool Equivalent(string expected, string? observed) =>
        Quantity(expected) is { } left && Quantity(observed) is { } right && left.Numerator * right.Denominator == right.Numerator * left.Denominator;

    private static (BigInteger Numerator, BigInteger Denominator)? Quantity(string? text)
    {
        if (text is null || text.Length is 0 or > 128) return null;
        var match = Regex.Match(text, @"\A([+]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+))([eE][+-]?[0-9]+|[numkMGTPE]|[KMGTPE]i)?\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        if (!match.Success) return null;
        var number = match.Groups[1].Value; var suffix = match.Groups[2].Value;
        var dot = number.IndexOf('.'); var scale = dot < 0 ? 0 : number.Length - dot - 1;
        if (!BigInteger.TryParse(number.Replace(".", "", StringComparison.Ordinal), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var coefficient) || coefficient <= 0) return null;
        var denominator = BigInteger.Pow(10, scale);
        if (suffix.EndsWith('i')) coefficient *= BigInteger.Pow(1024, "KMGTPE".IndexOf(suffix[0]) + 1);
        else
        {
            int exponent;
            if (suffix.Length > 1)
            {
                if (!int.TryParse(suffix.AsSpan(1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent) || exponent is < -100 or > 100) return null;
            }
            else exponent = suffix switch { "n" => -9, "u" => -6, "m" => -3, "k" => 3, "M" => 6, "G" => 9, "T" => 12, "P" => 15, "E" => 18, _ => 0 };
            if (exponent < 0) denominator *= BigInteger.Pow(10, -exponent);
            else coefficient *= BigInteger.Pow(10, exponent);
        }
        return (coefficient, denominator);
    }
}
