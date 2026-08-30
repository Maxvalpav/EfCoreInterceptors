using System.Text;
using EfCore.Interceptors;
using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Commands;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using SampleApp;

// Clean slate for repeat runs.
var dbPath = Path.Combine(Path.GetTempPath(), "ef-interceptors-demo.db");
if (File.Exists(dbPath))
{
    File.Delete(dbPath);
}

// Simple "current user" resolution — in a web app this would read HttpContext.
ICurrentUserProvider users = new StaticCurrentUserProvider(Environment.UserName);
string encryptionKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

var loggerFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss.fff ";
    })
    .SetMinimumLevel(LogLevel.Information));

DbContextOptions<AppDbContext> NewAppOptions() => new DbContextOptionsBuilder<AppDbContext>()
    .UseSqlite($"Data Source={dbPath}")
    .UseLoggerFactory(loggerFactory)
    .EnableSensitiveDataLogging()
    // ---- The whole interceptor suite, wired in one place ----
    .UseEfInterceptors(s => s
        .WithAuditing(users)
        .WithSoftDeletes(users)
        .WithDomainEvents(new ConsoleDomainEventDispatcher(e =>
            Console.WriteLine($"      >>> DOMAIN EVENT: {e.GetType().Name} {Describe(e)}")))
        .WithSqlLogging(loggerFactory)
        .WithSlowQueryWarning(TimeSpan.FromMilliseconds(1), loggerFactory)
        .WithQueryHints(new Dictionary<string, string>
        {
            ["recompile"] = "-- /* hint: recompile requested via TagWith */"
        })
        .WithConnectionLogging(loggerFactory)
        .WithTransactionLogging(loggerFactory)
        .WithSessionInit(["PRAGMA foreign_keys=ON;"], loggerFactory)
        .WithLoadStamping()
        .WithPropertyEncryption(new AesGcmPropertyValueEncryptor(encryptionKey)))
    .Options;

void Banner(string title)
{
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"=== {title} ===");
    Console.ResetColor();
}

// 1) Schema
Banner("0. Creating schema");
using (var db = new AppDbContext(NewAppOptions()))
{
    db.Database.EnsureCreated();
}

// 2) Audit stamping on insert + update
Banner("1. AuditSaveChangesInterceptor — Created/Updated stamps");
int orderId;
{
    using var db = new AppDbContext(NewAppOptions());
    var order = new Order { Customer = "Acme Corp", Total = 250m };
    order.Items.Add(new OrderItem { Product = "Widget", Price = 100m, CardNumber = "4111-1111-1111-1111" });
    order.Items.Add(new OrderItem { Product = "Gadget", Price = 150m });
    db.Orders.Add(order);
    db.SaveChanges();                       // <- audit columns filled here
    orderId = order.Id;
    Console.WriteLine($"      Order #{order.Id}: created={order.CreatedAtUtc:HH:mm:ss} by={order.CreatedBy}, updated={order.UpdatedAtUtc:HH:mm:ss} by={order.UpdatedBy}");

    // Property encryption proof: the stored column holds ciphertext, not the card number.
    var storedCard = db.Database
        .SqlQuery<string>($"SELECT CardNumber AS Value FROM Items LIMIT 1")
        .Single();
    Console.WriteLine($"      Stored CardNumber ciphertext: {storedCard[..Math.Min(28, storedCard.Length)]}...");

    order.Total += 50m;
    db.SaveChanges();                       // <- UpdatedAtUtc/By refreshed, Created* protected
    Console.WriteLine($"      After update : updated={order.UpdatedAtUtc:HH:mm:ss} by={order.UpdatedBy}");
}

// 3) Domain events
Banner("2. DomainEventsSaveChangesInterceptor — outbox dispatch after save");
{
    using var db = new AppDbContext(NewAppOptions());
    var order = db.Orders.Include(o => o.Items).First(o => o.Id == orderId);
    order.MarkPaid();
    db.SaveChanges();                       // <- event published right after commit
    Console.WriteLine($"      Order marked paid; events cleared: {order.DomainEvents.Count == 0}");
}

// 4) Slow query warning + SQL logging + TagWith hints + load stamping
Banner("3. SlowQueryCommandInterceptor + SqlLogging + QueryHints + LoadStamping");
{
    using var db = new AppDbContext(NewAppOptions());
    var orders = db.Orders
        .TagWith("recompile")               // <- picked up by QueryHintsCommandInterceptor
        .Where(o => o.Total > 100)
        .ToList();
    foreach (var o in orders)
    {
        Console.WriteLine($"      Loaded '{o.Customer}' (LoadedAtUtc={o.LoadedAtUtc:HH:mm:ss})"); // <- materialization stamp
    }
}

// 5) Soft delete: Remove becomes UPDATE + query filter hides the row
Banner("4. SoftDeleteSaveChangesInterceptor — logical delete");
{
    using var db = new AppDbContext(NewAppOptions());
    var item = db.Items.First(i => i.OrderId == orderId);
    db.Items.Remove(item);
    db.SaveChanges();

    var rawOptions = new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlite($"Data Source={dbPath}")
        .Options;
    using (var unfiltered = new NoFilterContext(rawOptions))
    {
        var row = unfiltered.Items.IgnoreQueryFilters().Single(i => i.Id == item.Id);
        Console.WriteLine($"      Row still in DB: IsDeleted={row.IsDeleted}, deletedBy={row.DeletedBy}");
    }

    Console.WriteLine($"      Filtered context sees {db.Items.Count(i => i.Id == item.Id)} row(s)");
}

// 6) Transaction lifecycle logs begin/commit
Banner("5. TransactionLifecycleLoggingInterceptor — explicit transaction");
{
    using var db = new AppDbContext(NewAppOptions());
    await using var tx = await db.Database.BeginTransactionAsync();
    var order = db.Orders.First(o => o.Id == orderId);
    order.Customer = "Acme Corp LLC";
    db.SaveChanges();
    await tx.CommitAsync();
    Console.WriteLine($"      Customer renamed inside transaction.");
}

// 7) Read-only guard blocks writes on the reporting context
Banner("6. ReadOnlyGuardCommandInterceptor — reporting context is write-protected");
{
    var options = new DbContextOptionsBuilder<ReportingDbContext>()
        .UseSqlite($"Data Source={dbPath}")
        .UseLoggerFactory(loggerFactory)
        .UseEfInterceptors(s => s.WithReadOnlyGuard())
        .Options;

    using var reportDb = new ReportingDbContext(options);
    try
    {
        reportDb.Orders.ExecuteUpdate(s => s.SetProperty(o => o.Total, 0m));
        Console.WriteLine("      Write was NOT blocked?!");
    }
    catch (ReadOnlyContextException ex)
    {
        Console.WriteLine($"      Blocked as expected: {ex.Message.Split('.')[0]}");
    }

    var count = reportDb.Orders.Count();
    Console.WriteLine($"      Reads still work: {count} active order(s)");
}

// 8) Session-init interceptor enforced FK constraints (SQLite PRAGMA)
Banner("7. SessionInitConnectionInterceptor — PRAGMA foreign_keys=ON");
{
    using var db = new AppDbContext(NewAppOptions());
    db.Items.Add(new OrderItem { OrderId = 999999, Product = "Orphan", Price = 1m });
    try
    {
        db.SaveChanges();
        Console.WriteLine("      FK violation NOT caught?!");
    }
    catch (DbUpdateException ex)
    {
        Console.WriteLine($"      Orphan insert rejected by database: {ex.InnerException?.Message}");
    }
}

// 9) Identity resolution: attach a detached copy over a tracked instance
Banner("8. IdentityResolutionInterceptors — merge instead of InvalidOperationException");
{
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlite($"Data Source={dbPath}")
        .UseLoggerFactory(loggerFactory)
        // Built-in EF Core 10 resolvers (or our custom ones from WithIdentityResolution):
        .AddInterceptors([new UpdatingIdentityResolutionInterceptor()])
        .Options;

    using var db = new AppDbContext(options);

    var tracked = db.Orders.First(o => o.Id == orderId);   // tracked state: Acme Corp LLC
    var incoming = new Order { Id = orderId, Customer = "Acme (detached copy)", Total = 42m };
    db.Attach(incoming);                                   // would throw without the interceptor

    Console.WriteLine($"      Tracked instance now: Customer='{tracked.Customer}', Total={tracked.Total}");
}

// 10) Audit trail + atomic outbox
Banner("9. ChangeLog + Outbox — per-property audit trail and durable event queue");
{
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlite($"Data Source={dbPath}")
        .UseLoggerFactory(loggerFactory)
        .UseEfInterceptors(s => s
            .WithAuditing(users)
            .WithChangeLog(users)
            .WithOutbox())
        .Options;

    using var db = new AppDbContext(options);
    var order = new Order { Customer = "Audit Demo", Total = 99m };
    db.Orders.Add(order);
    db.SaveChanges();

    order.MarkPaid();                       // -> becomes an OutboxMessage in the same transaction
    db.SaveChanges();

    var changes = db.ChangeLogEntries.Where(e => e.EntityName == "Order").ToList();
    Console.WriteLine($"      ChangeLog rows for Order: {changes.Count}");
    foreach (var entry in changes.TakeLast(1))
    {
        Console.WriteLine($"      Last entry: {entry.Action} by {entry.Actor} at {entry.TimestampUtc:HH:mm:ss}");
        Console.WriteLine($"      Diff: {entry.ChangesJson}");
    }

    var outboxRow = db.OutboxMessages.Single();
    Console.WriteLine($"      Outbox row: {outboxRow.Type} -> {outboxRow.PayloadJson}");
    Console.WriteLine($"      (A background processor delivers it and stamps ProcessedAtUtc)");
}

Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine();
Console.WriteLine("All scenarios finished successfully.");
Console.ResetColor();

static string Describe(object e)
    => e is OrderPaid op ? $"{{ OrderId={op.OrderId}, Total={op.Total} }}" : "{}";

/// <summary>Bypasses global query filters to inspect raw rows.</summary>
public class NoFilterContext(DbContextOptions<AppDbContext> options) : AppDbContext(options);
