using EfCore.Interceptors.Observability;

namespace WebApiSample;

/// <summary>
/// "Delivers" product-created events. In production this would call a message broker,
/// send an email, sync a search index, etc.
/// </summary>
public class ProductCreatedHandler(ILogger<ProductCreatedHandler> logger) : IOutboxMessageHandler
{
    public ValueTask HandleAsync(EfCore.Interceptors.Entities.OutboxMessage message, CancellationToken cancellationToken)
    {
        logger.LogInformation("[OUTBOX DELIVERED] id={Id} type={Type} payload={Payload}",
            message.Id, message.Type, message.PayloadJson);
        return ValueTask.CompletedTask;
    }
}
