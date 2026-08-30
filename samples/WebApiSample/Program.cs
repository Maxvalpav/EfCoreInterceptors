using EfCore.Interceptors;
using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Observability;
using EfCore.Interceptors.Saving;
using Microsoft.EntityFrameworkCore;
using WebApiSample;

var builder = WebApplication.CreateBuilder(args);

// ---------- Interceptor wiring (the interesting part) ----------
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserProvider, HttpContextCurrentUserProvider>();
builder.Services.AddSingleton<ITenantProvider, StaticTenantProviderForApi>();

// Scoped interceptor: resolves the per-request current user correctly.
builder.Services.AddScoped<AuditSaveChangesInterceptor>();
builder.Services.AddScoped<SoftDeleteSaveChangesInterceptor>();

builder.Services.AddDbContext<ProductDbContext>((sp, options) =>
    options
        .UseSqlite($"Data Source={Path.Combine(Path.GetTempPath(), "webapi-sample.db")}")
        .AddInterceptors(
            sp.GetRequiredService<AuditSaveChangesInterceptor>(),
            sp.GetRequiredService<SoftDeleteSaveChangesInterceptor>())
        .UseEfInterceptors(s => s
            .WithOutbox()                                          // events -> OutboxMessages (atomic)
            .WithSlowQueryWarning(TimeSpan.FromMilliseconds(300))  // slow query alerts
            .WithSqlLogging(sampleRate: 0.25)                      // sample 25% of SQL logs
            .WithCommandMetrics()                                  // ef.command.* metrics
            .WithNPlusOneDetection(5)));                           // N+1 detector

// Outbox background worker: delivers ProductCreated events and stamps ProcessedAtUtc.
builder.Services.AddScoped<IOutboxMessageHandler, ProductCreatedHandler>();
builder.Services.AddOutboxProcessor<ProductDbContext>(pollInterval: TimeSpan.FromSeconds(1));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<ProductDbContext>().Database.EnsureCreated();
}

// ---------- Minimal API ----------
app.MapGet("/products", async (ProductDbContext db) =>
    Results.Ok(await db.Products.Select(p => new { p.Id, p.Name, p.Price }).ToListAsync()));

app.MapGet("/products/{id:int}", async (int id, ProductDbContext db) =>
    await db.Products.FirstOrDefaultAsync(p => p.Id == id) is { } product
        ? Results.Ok(product)
        : Results.NotFound());

app.MapPost("/products", async (ProductDto dto, ProductDbContext db) =>
{
    var product = Product.Create(dto.Name, dto.Price);   // raises ProductCreated -> outbox
    db.Products.Add(product);
    await db.SaveChangesAsync();                          // audit + outbox in one transaction

    return Results.Created($"/products/{product.Id}", product);
});

app.MapDelete("/products/{id:int}", async (int id, ProductDbContext db) =>
{
    var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id);
    if (product is null) return Results.NotFound();

    db.Products.Remove(product);   // soft delete: UPDATE IsDeleted=1, row survives
    db.SaveChanges();
    return Results.NoContent();
});

app.MapPost("/products/{id:int}/restore", async (int id, ProductDbContext db) =>
{
    // IgnoreQueryFilters = "bin": restore soft-deleted rows by flipping the flag back.
    var deleted = await db.Products.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == id && p.IsDeleted);
    if (deleted is null) return Results.NotFound();

    deleted.IsDeleted = false;
    deleted.DeletedAtUtc = null;
    deleted.DeletedBy = null;
    db.SaveChanges();
    return Results.Ok(deleted);
});

// Watch the outbox queue being drained by the background processor.
app.MapGet("/outbox", async (ProductDbContext db) =>
    Results.Ok(await db.OutboxMessages
        .OrderByDescending(m => m.Id)
        .Take(20)
        .Select(m => new { m.Id, m.Type, m.PayloadJson, m.ProcessedAtUtc })
        .ToListAsync()));

app.Run();

public record ProductDto(string Name, decimal Price);
