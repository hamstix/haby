using System.Net;
using System.Text.Json;

internal sealed class ResponseLossProbe : DelegatingHandler
{
    internal int Attempts { get; private set; }
    internal string? AcceptedUid { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Post || !request.RequestUri!.AbsolutePath.EndsWith("/configmaps", StringComparison.Ordinal))
            return await base.SendAsync(request, cancellationToken);
        Attempts++;
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Created) return response;
        using (response)
        using (var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)))
            AcceptedUid = body.RootElement.GetProperty("metadata").GetProperty("uid").GetString();
        throw new HttpRequestException("Qualification response loss after confirmed API acceptance.");
    }
}
