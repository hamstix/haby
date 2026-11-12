using Haby.Domain;
using Xunit;

namespace Haby.Application.Tests;

public sealed class GeneratedCredentialOutputTests
{
    private static readonly DurablePlanningInputs Inputs = Haby.Tests.DurableInputSamples.Create(EnvironmentRecordId.New(), EnvironmentId.Create("pilot"),
        ManifestRevisionRef.Create(ApplicationRecordId.New(), ManifestRevisionNumber.Create(1)), ProviderAlias.Create("sample.primary"),
        ProviderInstanceId.New(), ProviderConfigurationRevisionNumber.Create(1), "sample.impl/v1", "queue-user");
    private static DurableOperationNode Producer(string id = "material") => new(OperationId.Create(id),
        ManifestLocalId.Create("queue-user"), new(new(ResourceTypeId.Create("sample.resource/v1"),
            new("sample/v1", "sample.configuration/v1", "sample.impl/v1")), OperationContractId.Create(GeneratedCredentialContracts.Action)),
        new(Inputs.Environment, Inputs.Providers.RequireUniformResourceProvider().Instance, Inputs.Providers.RequireUniformResourceProvider().Configuration, Inputs.Providers.RequireUniformResourceProvider().Implementation.Value),
        GeneratedCredentialInputCodec.Encode(new(new(Inputs.Environment, Inputs.Revision.ApplicationRecordId, ManifestLocalId.Create("queue-user")),
            GeneratedCredentialSlot.ApplicationPassword, GeneratedCredentialSlot.RandomPasswordV1)), []);
    private static DurableOperationNode Consumer() => Producer("consumer") with
    {
        Action = Producer().Action with { ActionContract = OperationContractId.Create("sample.user.create/v1") },
        Input = new("sample.user/v1", "{}"),
        Dependencies = [Producer().Id],
        OutputReferences = [GeneratedCredentialContracts.PasswordFrom(Producer().Id)]
    };

    [Fact]
    public void Frozen_selectors_round_trip_are_copied_and_change_graph_and_plan_identity()
    {
        var selectors = new[] { GeneratedCredentialContracts.PasswordFrom(Producer().Id) };
        var graph = new DurableOperationGraph([Producer(), Consumer() with { OutputReferences = selectors }]);
        var encoded = DurableOperationGraphCodec.Encode(graph);
        var fingerprint = DurableOperationGraphCodec.Fingerprint(Inputs, graph);
        selectors[0] = selectors[0] with { OutputKey = "changed" };
        Assert.Equal(encoded, DurableOperationGraphCodec.Encode(graph));
        Assert.Equal(graph, DurableOperationGraphCodec.Decode(encoded));
        Assert.Equal(fingerprint, DurableOperationGraphCodec.Fingerprint(Inputs, DurableOperationGraphCodec.Decode(encoded)));
        var without = new DurableOperationGraph([Producer(), Consumer() with { OutputReferences = [] }]);
        Assert.NotEqual(graph, without);
        Assert.NotEqual(fingerprint, DurableOperationGraphCodec.Fingerprint(Inputs, without));
        var another = Producer("another");
        var changed = new DurableOperationGraph([Producer(), another, Consumer() with
        {
            Dependencies = [Producer().Id, another.Id], OutputReferences = [GeneratedCredentialContracts.PasswordFrom(another.Id)]
        }]);
        Assert.NotEqual(fingerprint, DurableOperationGraphCodec.Fingerprint(Inputs, changed));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("contract")]
    [InlineData("key")]
    [InlineData("dependency")]
    [InlineData("resource")]
    [InlineData("environment")]
    [InlineData("type")]
    [InlineData("payload")]
    public void Invalid_selectors_and_producers_are_rejected_at_graph_acceptance(string mode)
    {
        var producer = Producer();
        var consumer = Consumer();
        var selector = consumer.OutputReferences.Single();
        if (mode == "missing") selector = selector with { ProducerOperationId = OperationId.Create("missing") };
        if (mode == "contract") selector = selector with { OutputContract = "other/v1" };
        if (mode == "key") selector = selector with { OutputKey = "role" };
        if (mode == "dependency") consumer = consumer with { Dependencies = [] };
        if (mode == "resource") consumer = consumer with { ResourceId = ManifestLocalId.Create("other") };
        if (mode == "environment") consumer = consumer with { References = consumer.References with { Environment = EnvironmentRecordId.New() } };
        if (mode == "type") producer = producer with { Action = consumer.Action };
        if (mode == "payload") producer = producer with { Input = producer.Input with { Json = "{}" } };
        Assert.Throws<InvalidDataException>(() => new DurableOperationGraph([producer, consumer with { OutputReferences = [selector] }]));
    }

    [Fact]
    public void Non_secret_evidence_can_cross_Resources_without_weakening_password_ownership()
    {
        var database = Producer("database") with
        {
            ResourceId = ManifestLocalId.Create("database"),
            Action = Consumer().Action with { ActionContract = OperationContractId.Create("sample.database.create/v1") },
            Input = new("sample.database/v1", "{}")
        };
        var evidence = new OperationOutputRef(database.Id, "sample.database-output/v1", "database");
        var consumer = Consumer() with { Dependencies = [database.Id], OutputReferences = [evidence] };
        Assert.Equal(2, new DurableOperationGraph([database, consumer]).Nodes.Count);
        Assert.Throws<InvalidDataException>(() => new DurableOperationGraph([Producer(), Consumer() with
        { ResourceId = ManifestLocalId.Create("other-login") }]));
    }

    [Fact]
    public void Forged_application_owner_is_refused_against_containing_plan()
    {
        var input = GeneratedCredentialInputCodec.Decode(Producer().Input)!;
        var graph = new DurableOperationGraph([Producer() with { Input = GeneratedCredentialInputCodec.Encode(input with
        { Owner = input.Owner with { ApplicationRecordId = ApplicationRecordId.New() } }) }]);
        Assert.Throws<InvalidDataException>(() => graph.ValidateReferences(Inputs));
    }

    [Theory]
    [InlineData("other/v1", "{}")]
    [InlineData("haby.generated-credential/v1", "{}")]
    [InlineData("haby.generated-credential/v1", "null")]
    [InlineData("haby.generated-credential/v1", "{")]
    [InlineData("haby.generated-credential/v1", "{\"Secret\":\"bad\",\"Version\":\"bad\"}")]
    public void Unsupported_or_corrupt_outputs_fail_closed(string contract, string payload) =>
        Assert.Null(GeneratedCredentialOutputCodec.Decode(contract, payload));

    [Fact]
    public void Output_codec_contains_only_the_exact_reference_and_rejects_extra_material()
    {
        var output = new GeneratedCredentialOutput(new(SecretId.New(), SecretVersionId.New()));
        var json = GeneratedCredentialOutputCodec.Encode(output);
        Assert.Equal(output, GeneratedCredentialOutputCodec.Decode(GeneratedCredentialContracts.OutputContract, json));
        Assert.Null(GeneratedCredentialOutputCodec.Decode(GeneratedCredentialContracts.OutputContract,
            json[..^1] + ",\"Password\":\"must-not-be-accepted\"}"));
    }
}
