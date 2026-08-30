using EfCore.Interceptors.Abstractions;

namespace WebApiSample;

public class Product : IAuditableEntity, ISoftDeletableEntity, IHasDomainEvents
{
    private readonly List<IDomainEvent> _events = [];

    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }

    // Audit
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }

    // Soft delete
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAtUtc { get; set; }
    public string? DeletedBy { get; set; }

    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public IReadOnlyList<IDomainEvent> DomainEvents => _events;

    public void AddDomainEvent(IDomainEvent domainEvent) => _events.Add(domainEvent);
    public void ClearDomainEvents() => _events.Clear();

    /// <summary>Raised by the create endpoint; lands in the outbox in the same transaction.</summary>
    public static Product Create(string name, decimal price)
    {
        var product = new Product { Name = name, Price = price };
        product._events.Add(new ProductCreated(product.Name, price));
        return product;
    }
}

public sealed record ProductCreated(string Name, decimal Price) : IDomainEvent
{
    public DateTimeOffset OccurredAtUtc { get; } = DateTimeOffset.UtcNow;
}
