using Google.Protobuf.WellKnownTypes;
using Google.Protobuf;
using Mapster;
using IdentityModel.Client;
using Hamstix.Haby.Shared.Grpc.Services;

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
        where TResponseItem : class, IMessage, new()
    {
        var response = new TResponse();
        foreach (var item in list)
        {
            var mappedItem = item.Adapt<TResponseItem>();
            if (fieldMask is not null)
            {
                var mergedReply = new TResponseItem();
                fieldMask.Merge(mappedItem, mergedReply);
                addFieldAction(response, mergedReply);
            }
            else
                addFieldAction(response, mappedItem);
        }
        return response;
    }

    public static TResponse ApplyFieldMask<TSource, TResponse>(this TSource item, FieldMask? fieldMask)
        where TResponse : class, IMessage, new()
    {
        var result = item.Adapt<TResponse>();

        if (fieldMask is not null)
        {
            var mergedReply = new TResponse();
            fieldMask.Merge(result, mergedReply);
            return mergedReply;
        }
        else
            return result;
    }
}
