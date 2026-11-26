# Int32-compatible configuration JSON

External `ConfigurationJson.Serialize`, Preview ordinary nodes and authorized
configuration delivery emit mathematically integral Int32 values as plain JSON
integer tokens. For example, `-1e0` becomes `-1`, and `1e9` becomes `1000000000`.
This permits the stock .NET JSON configuration provider with `GetValue<int>` and
`JsonElement.GetInt32`. This compatibility guarantee is limited to Int32; it does
not promise compatibility with every .NET numeric binder. Fractions and values
outside Int32 retain their exact compact canonical numeric representation.

Projected JSON secret numbers use the same external spelling after authorization.
Preview still redacts the entire confidential node. Measurement uses the same
numeric representation as writing, including the existing UTF-8 response budget.

`ConfigurationJson.SerializeCanonical` preserves the prior canonical numeric bytes
for internal inputs and identity. Planning snapshots, render pins, exact Provider
input sets, Profile snapshots and portable Profile declarations explicitly use
that path. Host frozen Provider input summaries retain the same representation.
Domain canonical text, equality/hash and the tagged configuration document codec
are unchanged. Fixed pre-change byte fixtures and SHA-256 baselines cover the
five affected encodings and their persisted round trips. This change requires no
catalog migration, reset or codec-version change; accepted Plan/Profile/Provider
input identity remains unchanged.

The external conversion checks normalized coefficient length, exponent sign and
Int32 range before expanding. It allocates at most ten digits plus a sign for a
candidate integer; huge exponents remain compact without exponent-sized powers,
rounding or floating-point conversion.
