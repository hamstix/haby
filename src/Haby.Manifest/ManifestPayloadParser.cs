using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Corvus.Text.Json.Validator;

namespace Haby.Manifest;

/// <summary>Parses the original import-envelope bytes without changing or persisting them.</summary>
public static class ManifestPayloadParser
{
    private const string SchemaUri = "https://haby.dev/schemas/application-manifest.v1alpha1.json";
    private const int DiagnosticLimit = 100;
    private static readonly Regex VersionPattern = new("\\A[A-Za-z0-9][A-Za-z0-9._+-]{0,127}\\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Lazy<JsonSchema> Schema = new(LoadSchema);
    private static readonly Lazy<JsonObject> SchemaDocument = new(() => JsonNode.Parse(LoadSchemaText())!.AsObject());
    private static readonly Dictionary<string, JsonSchema> BranchSchemas = new(StringComparer.Ordinal);
    private static readonly ManifestJsonContext Context = ManifestJsonContext.CreateStrict();
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 64, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false };

    public static ManifestParseResult Parse(ReadOnlyMemory<byte> payload)
    {
        var bytes = payload.Span;
        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ||
            bytes.StartsWith(new byte[] { 0xFF, 0xFE }) || bytes.StartsWith(new byte[] { 0xFE, 0xFF }) ||
            (bytes.Length >= 4 && ((bytes[0] == (byte)'{' && bytes[1] == 0 && bytes[2] == (byte)'"' && bytes[3] == 0) ||
                                   (bytes[0] == 0 && bytes[1] == (byte)'{' && bytes[2] == 0 && bytes[3] == (byte)'"'))) ||
            !IsValidUtf8(bytes))
            return Request("invalid-encoding", "", "The payload must be UTF-8 without a byte-order mark.");

        var syntax = ScanSyntax(bytes);
        if (syntax.Count != 0)
        {
            var requestErrors = syntax.Where(item => !IsManifestPath(item.Path))
                .Select(item => new ManifestRequestError("manifest-import." + item.Code, item.Path, item.Message, item.Parameters, item.Location)).ToArray();
            if (requestErrors.Length != 0)
                return new ManifestParseResult.InvalidRequest(requestErrors);
            return Invalid(syntax.Select(item => Diagnostic("manifest.syntax." + item.Code, StripManifest(item.Path), item.Message, item.Parameters, item.Location)));
        }

        using var document = JsonDocument.Parse(payload, DocumentOptions);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return Request("type-mismatch", "", "The import envelope must be an object.", ("expectedType", "object"));

        var envelopeErrors = ValidateEnvelope(root);
        if (envelopeErrors.Count != 0)
            return new ManifestParseResult.InvalidRequest(envelopeErrors);

        var version = root.GetProperty("applicationVersion"u8).GetString()!;
        var manifest = root.GetProperty("manifest"u8);
        var headers = ValidateHeader(manifest);
        if (headers.Count != 0)
            return Invalid(headers);

        var manifestBytes = Encoding.UTF8.GetBytes(manifest.GetRawText());
        using var collector = Corvus.Text.Json.JsonSchemaResultsCollector.Create(Corvus.Text.Json.JsonSchemaResultsLevel.Detailed, 20000);
        var schemaValid = Schema.Value.Validate(manifestBytes, collector);
        var diagnostics = new List<ManifestDiagnostic>();
        if (!schemaValid)
            diagnostics.AddRange(TranslateSchemaResults(manifest, collector));
        foreach (var path in V1Alpha1NumericSetGuard.FindDuplicatePaths(manifest, DiagnosticLimit + 1))
        {
            diagnostics.Add(Diagnostic(V1Alpha1NumericSetGuard.DiagnosticCode, path, "Parameter allowed values must be unique."));
        }
        if (diagnostics.Count != 0)
            return Invalid(diagnostics);
        if (!schemaValid)
            return Invalid([Diagnostic("manifest.schema.constraint-violation", "", "The manifest violates its schema.", ("constraint", "schema"))]);

        try
        {
            // Both types are obtained from the metadata-only generated context.
            _ = JsonSerializer.Deserialize(payload.Span, Context.ImportApplicationEnvelope);
            var typedManifest = JsonSerializer.Deserialize(manifestBytes, Context.ApplicationManifest);
            if (typedManifest is null)
                return Invalid([Diagnostic("manifest.schema.type-mismatch", "", "The manifest must be an object.", ("expectedType", "object"))]);
            var report = CreateReport(ManifestSemanticValidator.Validate(typedManifest));
            return report.IsValid
                ? new ManifestParseResult.Valid(version, typedManifest) { Report = report }
                : new ManifestParseResult.InvalidManifest(report);
        }
        catch (JsonException)
        {
            // A schema/DTO disagreement cannot leak deserializer text or user values.
            return Invalid([Diagnostic("manifest.schema.constraint-violation", "", "The manifest does not match its declared contract.", ("constraint", "dto"))]);
        }
    }

    private static string LoadSchemaText()
    {
        using var stream = typeof(ManifestPayloadParser).Assembly.GetManifestResourceStream("Haby.Manifest.application-manifest.v1alpha1.json")
            ?? throw new InvalidOperationException("The v1alpha1 schema resource is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static JsonSchema LoadSchema() => JsonSchema.FromText(LoadSchemaText(), SchemaUri, new JsonSchema.Options(allowFileSystemAndHttpResolution: false));

    private static bool IsValidUtf8(ReadOnlySpan<byte> bytes)
    {
        try { _ = new UTF8Encoding(false, true).GetCharCount(bytes); return true; }
        catch (DecoderFallbackException) { return false; }
    }

    private static List<ManifestRequestError> ValidateEnvelope(JsonElement root)
    {
        var errors = new List<ManifestRequestError>();
        foreach (var property in root.EnumerateObject())
        {
            var path = "/" + Escape(property.Name);
            if (property.Name is not ("applicationVersion" or "manifest"))
                errors.Add(new("manifest-import.unknown-property", path, "The import envelope contains an unknown member.", Params(("property", SafeProperty(property.Name)))));
        }
        foreach (var name in new[] { "applicationVersion", "manifest" })
        {
            if (!root.TryGetProperty(name, out var value))
            {
                errors.Add(new("manifest-import.required", "/" + name, "The import envelope requires this member.", Params(("property", name))));
                continue;
            }
            var expected = name == "manifest" ? JsonValueKind.Object : JsonValueKind.String;
            if (value.ValueKind != expected)
                errors.Add(new("manifest-import.type-mismatch", "/" + name, "An import envelope member has the wrong type.", Params(("expectedType", expected == JsonValueKind.Object ? "object" : "string"))));
            else if (name == "applicationVersion" && !VersionPattern.IsMatch(value.GetString()!))
                errors.Add(new("manifest-import.invalid-application-version", "/applicationVersion", "The application version has an invalid format.", Params()));
        }
        return errors.OrderBy(error => error.Path, StringComparer.Ordinal).ThenBy(error => error.Code, StringComparer.Ordinal).ToList();
    }

    private static List<ManifestDiagnostic> ValidateHeader(JsonElement manifest)
    {
        var diagnostics = new List<ManifestDiagnostic>();
        foreach (var (name, expected) in new[] { ("apiVersion", "haby.dev/v1alpha1"), ("kind", "Application") })
        {
            if (!manifest.TryGetProperty(name, out var value))
                diagnostics.Add(Diagnostic("manifest.schema.required", "/" + name, "A required manifest member is absent.", ("property", name)));
            else if (value.ValueKind != JsonValueKind.String)
                diagnostics.Add(Diagnostic("manifest.schema.type-mismatch", "/" + name, "A manifest member has the wrong type.", ("expectedType", "string")));
            else if (value.GetString() != expected)
                diagnostics.Add(Diagnostic(name == "apiVersion" ? "manifest.schema.unsupported-api-version" : "manifest.schema.unsupported-kind", "/" + name, "The manifest header is unsupported.", (name, expected)));
        }
        if (manifest.TryGetProperty("$schema"u8, out var schemaValue))
        {
            if (schemaValue.ValueKind != JsonValueKind.String)
                diagnostics.Add(Diagnostic("manifest.schema.type-mismatch", "/$schema", "The schema URI must be a string.", ("expectedType", "string")));
            else if (schemaValue.GetString() != SchemaUri)
                diagnostics.Add(Diagnostic("manifest.schema.schema-uri-mismatch", "/$schema", "The schema URI does not match the selected API version.", ("expectedSchema", SchemaUri)));
        }
        foreach (var name in new[] { "metadata", "spec" })
        {
            if (!manifest.TryGetProperty(name, out _))
                diagnostics.Add(Diagnostic("manifest.schema.required", "/" + name, "A required manifest member is absent.", ("property", name)));
        }
        return diagnostics;
    }

    private static IEnumerable<ManifestDiagnostic> TranslateSchemaResults(JsonElement manifest, Corvus.Text.Json.JsonSchemaResultsCollector collector)
    {
        foreach (var result in collector.EnumerateResults())
        {
            if (result.IsMatch) continue;
            var schemaPath = result.GetSchemaEvaluationLocationText();
            var path = result.GetDocumentEvaluationLocationText();
            if (string.IsNullOrEmpty(schemaPath) || !schemaPath.Contains('/')) continue;
            var keyword = schemaPath[(schemaPath.LastIndexOf('/') + 1)..];
            if (keyword is not ("required" or "additionalProperties" or "type" or "oneOf" or "const" or "enum" or "pattern" or "minLength" or "maxLength" or "minimum" or "maximum" or "minItems" or "maxItems" or "maxProperties" or "uniqueItems" or "propertyNames" or "not")) continue;
            if (keyword is "propertyNames" or "pattern" or "maxLength" &&
                (schemaPath.Contains("/propertyNames", StringComparison.Ordinal) ||
                 ((schemaPath.Contains("/$defs/localId/", StringComparison.Ordinal) || schemaPath.Contains("/$defs/applicationId/", StringComparison.Ordinal)) &&
                  TryAtPointer(manifest, path, out var map) && map.ValueKind == JsonValueKind.Object)))
            {
                if (keyword is "propertyNames" or "pattern")
                {
                    foreach (var invalid in InvalidMapKeys(manifest, path)) yield return invalid;
                }
                continue;
            }
            if (keyword == "oneOf")
            {
                if (TryAtPointer(manifest, path, out var union) && union.ValueKind == JsonValueKind.Object)
                {
                    var discriminator = schemaPath.Contains("parameter", StringComparison.Ordinal) ? "type" :
                        schemaPath.Contains("document", StringComparison.Ordinal) ? "format" : "kind";
                    if (union.TryGetProperty(discriminator, out var discriminatorValue) && discriminatorValue.ValueKind == JsonValueKind.String &&
                        BranchFor(discriminator, discriminatorValue.GetString()!) is { } branch)
                    {
                        foreach (var branchDiagnostic in ValidateBranch(union, path, branch)) yield return branchDiagnostic;
                    }
                    else
                    {
                        if (discriminator == "format" && !union.TryGetProperty("format"u8, out _))
                        {
                            foreach (var name in new[] { "fileName", "format", "template" })
                                if (!union.TryGetProperty(name, out _))
                                    yield return Diagnostic("manifest.schema.required", path + "/" + name, "A required manifest member is absent.", ("property", name));
                            continue;
                        }
                        var code = discriminator == "type" ? "manifest.parameter.type-invalid" : "manifest.schema.invalid-discriminator";
                        yield return Diagnostic(code, path + "/" + discriminator, "The union discriminator is invalid.", discriminator == "type" ? ("parameterType", "unknown") : ("property", discriminator));
                    }
                    continue;
                }
            }
            if (keyword == "required")
            {
                // Derive absent members from the selected schema's required list.
                foreach (var name in MissingRequiredMembers(manifest, path, schemaPath))
                    yield return Diagnostic("manifest.schema.required", path.EndsWith("/" + Escape(name), StringComparison.Ordinal) ? path : path + "/" + Escape(name), "A required manifest member is absent.", ("property", name));
                continue;
            }
            if (keyword == "additionalProperties")
            {
                if (SchemaNode(schemaPath) is JsonValue schemaValue && schemaValue.TryGetValue<bool>(out var allowed) && !allowed)
                {
                    var property = LastSegment(path);
                    yield return Diagnostic("manifest.schema.unknown-property", path, "A core object contains an unknown member.", ("property", SafeProperty(property)));
                }
                continue;
            }
            if (keyword == "type")
                yield return Diagnostic(path.StartsWith("/spec/parameters/", StringComparison.Ordinal) && path.EndsWith("/default", StringComparison.Ordinal)
                    ? "manifest.parameter.default-type-mismatch" : "manifest.schema.type-mismatch", path,
                    "A manifest member has the wrong type.", ("expectedType", SchemaNode(schemaPath)?.GetValue<string>() ?? "contract type"));
            else if (keyword is "pattern" or "maxLength" &&
                schemaPath.Contains("/$defs/", StringComparison.Ordinal) && IdKind(path) is { } idKind)
                yield return Diagnostic("manifest.id.invalid", path, "An identifier is invalid.", ("idKind", idKind));
            else if (keyword == "not" && schemaPath.Contains("Parameter", StringComparison.Ordinal))
                yield return Diagnostic("manifest.parameter.default-conflict", path, "A parameter cannot declare both defaults.");
            else if (keyword == "const" && path.EndsWith("/kind", StringComparison.Ordinal))
                yield return Diagnostic("manifest.schema.invalid-discriminator", path, "The union discriminator is invalid.", ("property", "kind"));
            else if (keyword == "uniqueItems" && path.StartsWith("/spec/parameters/", StringComparison.Ordinal) && path.EndsWith("/allowedValues", StringComparison.Ordinal))
                yield return Diagnostic("manifest.parameter.allowed-value-duplicate", path, "Parameter allowed values must be unique.");
            else if (keyword != "propertyNames")
                yield return Diagnostic("manifest.schema.constraint-violation", path, "A manifest constraint is violated.", ("constraint", keyword));
        }
    }

    private static string? BranchFor(string discriminator, string value) => (discriminator, value) switch
    {
        ("type", "string") => "stringParameter",
        ("type", "boolean") => "booleanParameter",
        ("type", "integer") => "integerParameter",
        ("type", "number") => "numberParameter",
        ("format", "json" or "yaml") => "structuredDocument",
        ("format", "text") => "textDocument",
        ("kind", "local-resource") => "localTarget",
        ("kind", "application-export") => "externalTarget",
        _ => null
    };

    private static IEnumerable<ManifestDiagnostic> ValidateBranch(JsonElement value, string prefix, string branch)
    {
        JsonSchema schema;
        lock (BranchSchemas)
        {
            if (!BranchSchemas.TryGetValue(branch, out schema!))
            {
                var original = JsonNode.Parse(LoadSchemaText())!.AsObject();
                var branchRoot = new JsonObject
                {
                    ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
                    ["$defs"] = original["$defs"]!.DeepClone(),
                    ["$ref"] = "#/$defs/" + branch
                };
                schema = JsonSchema.FromText(branchRoot.ToJsonString(), "urn:haby:manifest-branch:" + branch,
                    new JsonSchema.Options(allowFileSystemAndHttpResolution: false));
                BranchSchemas.Add(branch, schema);
            }
        }
        using var collector = Corvus.Text.Json.JsonSchemaResultsCollector.Create(Corvus.Text.Json.JsonSchemaResultsLevel.Detailed, 20000);
        if (schema.Validate(Encoding.UTF8.GetBytes(value.GetRawText()), collector)) yield break;
        var translated = TranslateSchemaResults(value, collector).ToArray();
        foreach (var diagnostic in translated)
        {
            var prefixed = diagnostic with { Path = prefix + diagnostic.Path };
            if (prefix.StartsWith("/spec/parameters/", StringComparison.Ordinal) && diagnostic.Path == "/default" &&
                prefixed.Code == "manifest.schema.type-mismatch")
                prefixed = prefixed with { Code = "manifest.parameter.default-type-mismatch" };
            else if (prefix.StartsWith("/spec/parameters/", StringComparison.Ordinal) && diagnostic.Path == "/allowedValues" &&
                     prefixed.Code == "manifest.schema.constraint-violation" &&
                     prefixed.Parameters.TryGetValue("constraint", out var constraint) && constraint == "uniqueItems")
                prefixed = Diagnostic("manifest.parameter.allowed-value-duplicate", prefixed.Path, "Parameter allowed values must be unique.");
            yield return prefixed;
        }
        if (translated.Length == 0)
            yield return Diagnostic("manifest.schema.constraint-violation", prefix, "A union member violates its schema.", ("constraint", "branch"));
    }

    private static IEnumerable<string> MissingRequiredMembers(JsonElement manifest, string path, string schemaPath)
    {
        if (SchemaNode(schemaPath) is not JsonArray required) yield break;
        if (TryAtPointer(manifest, path, out var value) && value.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in required.Select(node => node!.GetValue<string>()))
                if (!value.TryGetProperty(name, out _)) yield return name;
        }
        else
        {
            var name = LastSegment(path);
            if (required.Any(node => node!.GetValue<string>() == name)) yield return name;
        }
    }

    private static JsonNode? SchemaNode(string pointer)
    {
        JsonNode? node = SchemaDocument.Value;
        foreach (var segment in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
            node = node is JsonObject obj && obj.TryGetPropertyValue(segment, out var child) ? child : null;
        return node;
    }

    private static IEnumerable<ManifestDiagnostic> InvalidMapKeys(JsonElement manifest, string path)
    {
        if (!TryAtPointer(manifest, path, out var map) || map.ValueKind != JsonValueKind.Object) yield break;
        var idKind = path switch
        {
            "/spec/resources" => "resource",
            "/spec/exports" => "resource-export",
            "/spec/components" => "component",
            "/spec/parameters" => "parameter",
            _ when path.EndsWith("/bindings", StringComparison.Ordinal) => "binding",
            _ when path.EndsWith("/documents", StringComparison.Ordinal) => "document",
            _ when path.EndsWith("/workloads", StringComparison.Ordinal) => "workload",
            _ => null
        };
        foreach (var member in map.EnumerateObject())
        {
            var isLabel = path.EndsWith("/labels", StringComparison.Ordinal);
            var pattern = isLabel ? "\\A[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*\\z" : "\\A[a-z][a-z0-9]*(?:-[a-z0-9]+)*\\z";
            var maxLength = isLabel ? 128 : 63;
            if (!Regex.IsMatch(member.Name, pattern, RegexOptions.CultureInvariant) || member.Name.Length > maxLength)
            {
                if (idKind is not null)
                    yield return Diagnostic("manifest.id.invalid", path + "/" + Escape(member.Name), "A declaration ID is invalid.", ("idKind", idKind));
                else
                    yield return Diagnostic("manifest.schema.constraint-violation", path + "/" + Escape(member.Name), "A map key violates its constraint.", ("constraint", "propertyNames"));
            }
        }
    }

    private static string? IdKind(string path) => path switch
    {
        "/metadata/id" => "application",
        _ when path.EndsWith("/providerRef", StringComparison.Ordinal) => "provider-alias",
        _ when path.EndsWith("/applicationId", StringComparison.Ordinal) => "application",
        _ when path.EndsWith("/accessProfile", StringComparison.Ordinal) => "access-profile",
        _ when path.EndsWith("/contract", StringComparison.Ordinal) || path.EndsWith("/type", StringComparison.Ordinal) || path.EndsWith("/rendererRef", StringComparison.Ordinal) => "contract",
        _ when path.EndsWith("/resourceRef", StringComparison.Ordinal) => "resource",
        _ when path.EndsWith("/exportRef", StringComparison.Ordinal) => "resource-export",
        _ when path.EndsWith("/documentRef", StringComparison.Ordinal) => "document",
        _ when path.EndsWith("/profile", StringComparison.Ordinal) => "workload-profile",
        _ => null
    };

    private static bool TryAtPointer(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        foreach (var encoded in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = encoded.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(segment, out var property)) value = property;
            else if (value.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index) && index >= 0 && index < value.GetArrayLength()) value = value[index];
            else return false;
        }
        return true;
    }

    private static ManifestParseResult.InvalidManifest Invalid(IEnumerable<ManifestDiagnostic> source) => new(CreateReport(source));

    private static ManifestValidationReport CreateReport(IEnumerable<ManifestDiagnostic> source)
    {
        var unique = source.DistinctBy(d => (d.Code, d.Severity, d.Path,
            string.Join("\u001f", d.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)),
            string.Join("\u001f", d.RelatedPaths))).OrderBy(d => d.Path, StringComparer.Ordinal)
            .ThenBy(d => d.Code, StringComparer.Ordinal)
            .ThenBy(d => string.Join("\u001f", d.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)), StringComparer.Ordinal)
            .ThenBy(d => string.Join("\u001f", d.RelatedPaths), StringComparer.Ordinal).ToArray();
        return new(unique.Take(DiagnosticLimit).ToArray(), unique.Length > DiagnosticLimit);
    }

    private static ManifestDiagnostic Diagnostic(string code, string path, string message, params (string Key, string Value)[] parameters) =>
        new(code, ManifestDiagnosticSeverity.Error, path, message, Params(parameters), [], null);

    private static ManifestDiagnostic Diagnostic(string code, string path, string message, IReadOnlyDictionary<string, string> parameters, ManifestSourceLocation? location) =>
        new(code, ManifestDiagnosticSeverity.Error, path, message, parameters, [], location);

    private static IReadOnlyDictionary<string, string> Params(params (string Key, string Value)[] values) =>
        values.OrderBy(value => value.Key, StringComparer.Ordinal).ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);

    private static ManifestParseResult.InvalidRequest Request(string code, string path, string message, params (string Key, string Value)[] parameters) =>
        new([new ManifestRequestError("manifest-import." + code, path, message, Params(parameters))]);

    private static string Escape(string segment) => segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    private static string LastSegment(string path) => path[(path.LastIndexOf('/') + 1)..].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
    private static string SafeProperty(string property) => property.Length <= 256 && property.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '$') ? property : "<redacted>";
    private static bool IsManifestPath(string path) => path == "/manifest" || path.StartsWith("/manifest/", StringComparison.Ordinal);
    private static string StripManifest(string path) => path == "/manifest" ? "" : path["/manifest".Length..];

    private sealed record SyntaxFinding(string Code, string Path, string Message, IReadOnlyDictionary<string, string> Parameters, ManifestSourceLocation? Location);

    private static List<SyntaxFinding> ScanSyntax(ReadOnlySpan<byte> bytes)
    {
        var findings = new List<SyntaxFinding>();
        var currentPath = "";
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 1024, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        try
        {
            if (!reader.Read()) throw new JsonException();
            ScanValue(ref reader, bytes, "", 0, findings, ref currentPath);
            if (reader.Read()) throw new JsonException();
        }
        catch (DepthExceededException) { }
        catch (JsonException error)
        {
            findings.Add(new("invalid-json", currentPath, "The JSON payload is invalid.", Params(), error.LineNumber is long line && error.BytePositionInLine is long column ? new((int)line + 1, (int)column + 1) : At(bytes, reader.BytesConsumed)));
        }
        return findings;
    }

    private sealed class DepthExceededException : Exception;

    private static void ScanValue(ref Utf8JsonReader reader, ReadOnlySpan<byte> bytes, string path, int depth, List<SyntaxFinding> findings, ref string currentPath)
    {
        currentPath = path;
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            if (depth == 64)
            {
                findings.Add(new("max-depth-exceeded", path, "JSON nesting exceeds the accepted limit.", Params(("maxDepth", "64")), At(bytes, reader.TokenStartIndex)));
                throw new DepthExceededException();
            }
            var isObject = reader.TokenType == JsonTokenType.StartObject;
            var names = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            while (reader.Read())
            {
                if (reader.TokenType == (isObject ? JsonTokenType.EndObject : JsonTokenType.EndArray)) return;
                string childPath;
                if (isObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
                    var name = reader.GetString()!;
                    childPath = path + "/" + Escape(name);
                    if (HasInvalidUnicodeEscape(reader.ValueSpan))
                        findings.Add(new("invalid-json", childPath, "The JSON payload contains invalid Unicode.", Params(), At(bytes, reader.TokenStartIndex)));
                    if (!names.Add(name))
                        findings.Add(new("duplicate-property", childPath, "An object member is duplicated.", Params(("property", SafeProperty(name))), At(bytes, reader.TokenStartIndex)));
                    currentPath = childPath;
                    if (!reader.Read()) throw new JsonException();
                }
                else childPath = path + "/" + index++;
                ScanValue(ref reader, bytes, childPath, depth + 1, findings, ref currentPath);
                currentPath = path;
            }
            throw new JsonException();
        }
        if (reader.TokenType is not (JsonTokenType.String or JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False or JsonTokenType.Null))
            throw new JsonException();
        if (reader.TokenType == JsonTokenType.String && HasInvalidUnicodeEscape(reader.ValueSpan))
            findings.Add(new("invalid-json", path, "The JSON payload contains invalid Unicode.", Params(), At(bytes, reader.TokenStartIndex)));
    }

    private static bool HasInvalidUnicodeEscape(ReadOnlySpan<byte> value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != (byte)'\\' || ++i >= value.Length) continue;
            if (value[i] != (byte)'u') continue;
            if (i + 4 >= value.Length) return true;
            var digits = Encoding.ASCII.GetString(value.Slice(i + 1, 4));
            if (!ushort.TryParse(digits, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var unit)) return true;
            i += 4;
            if (unit is >= 0xDC00 and <= 0xDFFF) return true;
            if (unit is >= 0xD800 and <= 0xDBFF)
            {
                if (i + 6 >= value.Length || value[i + 1] != (byte)'\\' || value[i + 2] != (byte)'u') return true;
                var lowDigits = Encoding.ASCII.GetString(value.Slice(i + 3, 4));
                if (!ushort.TryParse(lowDigits, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var low) || low is < 0xDC00 or > 0xDFFF) return true;
                i += 6;
            }
        }
        return false;
    }

    private static ManifestSourceLocation At(ReadOnlySpan<byte> bytes, long offset)
    {
        var line = 1;
        var column = 1;
        for (var i = 0; i < offset && i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n') { line++; column = 1; }
            else column++;
        }
        return new(line, column);
    }
}
