using Google.Protobuf.WellKnownTypes;
using Google.Protobuf;
using Mapster;

namespace Hamstix.Haby.Server.Extensions;

public static class FieldMaskResultExtensions
{
    /// <summary>
    /// Map <paramref name="list"/> to the <typeparamref name="TResponse"/> applying FieldMask.
    /// </summary>
    /// <typeparam name="TSource"></typeparam>
    /// <typeparam name="TResponse"></typeparam>
    /// <typeparam name="TResponseItem"></typeparam>
    /// <param name="list"></param>
    /// <param name="fieldMask"></param>
    /// <param name="addFieldAction"></param>
    /// <returns></returns>
    public static TResponse ApplyFieldMask<TSource, TResponse, TResponseItem>(this ICollection<TSource> list, FieldMask? fieldMask,
        Action<TResponse, TResponseItem> addFieldAction)
        where TResponse : class, new()
        where TResponseItem : class, IMessage<TResponseItem>, new()
    {
        var response = new TResponse();
        foreach (var item in list)
        {
            var mappedItem = item.Adapt<TResponseItem>();
            if (fieldMask is not null)
            {
                addFieldAction(response, fieldMask.ApplyTo(mappedItem));
            }
            else
                addFieldAction(response, mappedItem);
        }
        return response;
    }

    public static TResponse ApplyFieldMask<TSource, TResponse>(this TSource item, FieldMask? fieldMask)
        where TResponse : class, IMessage<TResponse>, new()
    {
        var result = item.Adapt<TResponse>();

        if (fieldMask is not null)
        {
            return fieldMask.ApplyTo(result);
        }
        else
            return result;
    }
}
