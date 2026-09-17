using Google.Protobuf.WellKnownTypes;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hamstix.Haby.Server.Extensions;

public static class ProtobufJsonExtensions
{
    public static Struct ToProtoStruct(this JsonObject jsonObject)
    {
        var result = new Struct();
        foreach (var item in jsonObject)
            result.Fields.Add(item.Key, ToProtoValue(item.Value));

        return result;
    }

    public static ListValue ToProtoArray(this JsonArray jsonArray)
    {
        var result = new ListValue();
        foreach (var item in jsonArray)
            result.Values.Add(ToProtoValue(item));

        return result;
    }

    static Value ToProtoValue(JsonNode? value) => value switch
    {
        null => new Value { NullValue = NullValue.NullValue },
        JsonObject jsonObject => new Value { StructValue = jsonObject.ToProtoStruct() },
        JsonArray jsonArray => new Value { ListValue = jsonArray.ToProtoArray() },
        JsonValue jsonValue => ToProtoValue(jsonValue),
        _ => throw new ArgumentOutOfRangeException(nameof(value), value.GetType(), "Unsupported JSON node type.")
    };

    static Value ToProtoValue(JsonValue value)
    {
        using var document = JsonDocument.Parse(value.ToJsonString());
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.Null => new Value { NullValue = NullValue.NullValue },
            JsonValueKind.String => new Value { StringValue = document.RootElement.GetString() ?? string.Empty },
            JsonValueKind.Number => new Value { NumberValue = document.RootElement.GetDouble() },
            JsonValueKind.True => new Value { BoolValue = true },
            JsonValueKind.False => new Value { BoolValue = false },
            _ => new Value { StringValue = value.ToString() }
        };
    }
}
