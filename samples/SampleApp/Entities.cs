using System.ComponentModel.DataAnnotations.Schema;
using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace SampleApp;

public class Order : IAuditableEntity, ISoftDeletableEntity, IHasDomainEvents, ILoadTimestamped
{
    private readonly List<IDomainEvent> _events = [];

    public int Id { get; set; }
    public string Customer { get; set; } = string.Empty;
    public decimal Total { get; set; }
    public bool IsPaid { get; set; }
    public List<OrderItem> Items { get; set; } = [];

    // Audit
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }

    // Soft delete
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAtUtc { get; set; }
    public string? DeletedBy { get; set; }

    // Materialization stamp
    public DateTimeOffset? LoadedAtUtc { get; set; }

    [NotMapped]
    public IReadOnlyList<IDomainEvent> DomainEvents => _events;

    public void AddDomainEvent(IDomainEvent domainEvent) => _events.Add(domainEvent);
    public void ClearDomainEvents() => _events.Clear();

    public void MarkPaid()
    {
        IsPaid = true;
        AddDomainEvent(new OrderPaid(Id, Total));
    }
}

public class OrderItem : IAuditableEntity, ISoftDeletableEntity
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public Order? Order { get; set; }
    public string Product { get; set; } = string.Empty;
    public decimal Price { get; set; }

    /// <summary>Stored encrypted at rest thanks to the [Encrypted] attribute.</summary>
    [Encrypted]
    public string? CardNumber { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }

    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAtUtc { get; set; }
    public string? DeletedBy { get; set; }
}

public sealed record OrderPaid(int OrderId, decimal Total) : IDomainEvent
{
    public DateTimeOffset OccurredAtUtc { get; } = DateTimeOffset.UtcNow;
}
