using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;

namespace Hamstix.Haby.Server.Extensions;

public static class ProtobufFieldMaskExtensions
{
    public static T ApplyTo<T>(this FieldMask fieldMask, T source)
        where T : class, IMessage<T>
    {
        ArgumentNullException.ThrowIfNull(fieldMask);
        ArgumentNullException.ThrowIfNull(source);

        var selectedPaths = fieldMask.Paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            .ToArray();

        var result = source.Descriptor.Parser.ParseFrom(source.ToByteString()) as T
            ?? throw new InvalidOperationException($"Cannot clone protobuf message {source.Descriptor.FullName}.");
        ClearUnselectedFields(result, selectedPaths, 0);
        return result;
    }

    static void ClearUnselectedFields(IMessage message, string[][] selectedPaths, int depth)
    {
        foreach (var field in message.Descriptor.Fields.InFieldNumberOrder())
        {
            var matchingPaths = selectedPaths
                .Where(path => path.Length > depth &&
                    (string.Equals(path[depth], field.Name, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(path[depth], field.JsonName, StringComparison.Ordinal)))
                .ToArray();

            if (matchingPaths.Length == 0)
            {
                field.Accessor.Clear(message);
                continue;
            }

            if (matchingPaths.Any(path => path.Length == depth + 1) ||
                field.FieldType != FieldType.Message ||
                field.IsRepeated)
            {
                continue;
            }

            if (field.Accessor.GetValue(message) is IMessage nestedMessage)
                ClearUnselectedFields(nestedMessage, matchingPaths, depth + 1);
        }
    }
}
