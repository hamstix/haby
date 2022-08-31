using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Mapster;
using Monq.Core.BasicDotNetMicroservice.Extensions;
using System.Text.Json.Nodes;

namespace Hamstix.Haby.Server.Configuration.Mapster;

public class InfrastructureProfile : IRegister
{
    public void Register(TypeAdapterConfig config)
    {
        config.Default
            .UseDestinationValue(member => member.SetterModifier == AccessModifier.None &&
                                   member.Type.IsGenericType &&
                                   member.Type.GetGenericTypeDefinition() == typeof(RepeatedField<>));

        config.NewConfig<JsonObject, Struct>()
            .MapWith(src => src.ToProtoStruct());
        config.NewConfig<JsonArray, ListValue>()
            .MapWith(src => src.ToProtoArray());

        config.NewConfig<JsonObject, Value>()
            .MapWith(src => Value.Parser.ParseJson(src.ToJsonString(null)));
        config.NewConfig<JsonNode, Value>()
            .MapWith(src => Value.Parser.ParseJson(src.ToJsonString(null)));

        config.NewConfig<JsonObject, JsonObject>()
            .MapWith(src => src);
        config.NewConfig<JsonObject, JsonNode>()
            .MapWith(src => src);
        config.NewConfig<JsonNode, JsonNode>()
            .MapWith(src => src);

        config.NewConfig<Value, Value>()
            .MapWith(src => src);

        // Map DateTimeOffset to Timestamp.
        config.NewConfig<DateTimeOffset, Timestamp>()
            .MapWith(dateTimeOffset => dateTimeOffset.ToTimestamp());
        // Map Timestamp to DateTimeOffset.
        config.NewConfig<Timestamp, DateTimeOffset>()
            .MapWith(timestamp => timestamp.ToDateTimeOffset());
        // Nullable  Datetime offset.
        config.NewConfig<DateTimeOffset?, Timestamp>()
            .MapWith(dateTimeOffset => dateTimeOffset == null ? new Timestamp() : dateTimeOffset.Value.ToTimestamp());
        config.NewConfig<Timestamp, DateTimeOffset?>()
            .MapWith(timestamp =>
                timestamp.ToDateTimeOffset() == DateTimeOffset.MinValue ? null : (Nullable<DateTimeOffset>)timestamp.ToDateTimeOffset());

        config.NewConfig<Timestamp, DateTimeOffset?>()
            .MapWith(timestamp =>
                timestamp == null ? null : (Nullable<DateTimeOffset>)timestamp.ToDateTimeOffset());

        //config.NewConfig<PagingRequestModel, PagingModel>()
        //    .Map(src => src.Page, dst => 1, cond => cond.Page <= 0)
        //    .Map(src => src.Page, dst => dst.Page, cond => cond.Page > 0)
        //    .Map(src => src.PerPage, dst => 1000, cond => cond.PerPage == 0)
        //    .Map(src => src.PerPage, dst => dst.PerPage, cond => cond.PerPage > 0 || cond.PerPage == -1);
    }
}