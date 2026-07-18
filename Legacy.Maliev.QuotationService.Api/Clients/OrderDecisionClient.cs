using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using System.Net;

namespace Legacy.Maliev.QuotationService.Api.Clients;

/// <summary>Transitions linked orders through OrderService's idempotent named-status boundary.</summary>
public sealed class OrderDecisionClient(HttpClient httpClient) : IOrderDecisionClient
{
    /// <inheritdoc />
    public async Task<OrderDecisionResult> TransitionAsync(
        int orderId,
        bool accepted,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/orderstatuses/histories/{orderId}/{(accepted ? "accepted" : "declined")}");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.StatusCode switch
            {
                HttpStatusCode.Created => OrderDecisionResult.Completed,
                HttpStatusCode.Conflict => OrderDecisionResult.Conflict,
                HttpStatusCode.NotFound => OrderDecisionResult.NotFound,
                _ => OrderDecisionResult.Unavailable,
            };
        }
        catch (HttpRequestException)
        {
            return OrderDecisionResult.Unavailable;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return OrderDecisionResult.Unavailable;
        }
    }
}
