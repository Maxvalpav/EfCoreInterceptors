# План исправлений — конкретные патчи

Документ отвечает на вопрос «что именно менять в коде». Для каждого пункта:
симптом → патч → как проверить.

---

## 1. Мультитенантность: фильтр «залипает» в кэше модели

**Симптом.** `ApplyTenantFilters(provider)` строит query filter с захватом
`ITenantProvider`. EF кэширует модель **один раз на тип контекста** — первый
запрос фиксирует значение, все остальные тенанты получают чужой фильтр.

**Патч.** Фильтр должен читать значение из контекста, а не из захваченного
провайдера, плюс нужен `IModelCacheKeyFactory`, если фильтр всё же зависит
от внешнего состояния.

```csharp
// ❌ было: значение вычисляется в момент построения модели
entityType.SetQueryFilter(e => e.TenantId == provider.CurrentTenantId);

// ✅ стало: контекст сам хранит текущего тенанта, EF видит доступ к полю
public class AppDbContext : DbContext
{
    public string? CurrentTenantId { get; private set; }
    public void SetTenant(string id) => CurrentTenantId = id;
}

modelBuilder.Entity<Order>()
    .HasQueryFilter("EfCoreInterceptors.Tenant",
        e => e.TenantId == ((AppDbContext)EF.Property<object>(e, "__ctx")).CurrentTenantId);
```

Практичный вариант без хаков — свойство на контексте и фильтр через
замыкание на `this`:

```csharp
modelBuilder.Entity<Order>()
    .HasQueryFilter("EfCoreInterceptors.Tenant", e => e.TenantId == CurrentTenantId);
```

**Проверка.** Тест: два скоупа с разными тенантами в одном процессе,
второй не должен видеть строки первого. Сейчас такой тест упадёт.

---

## 2. Состояние интерсептора, ключуемое по `DbContext`

**Симптом.** `Dictionary<DbContext, List<IDomainEvent>>` в singleton-интерсепторе:
контекст никогда не собирается GC, при исключении запись не удаляется,
параллельный доступ → `InvalidOperationException`.

```csharp
// ❌
private readonly Dictionary<DbContext, List<IDomainEvent>> _buffer = new();

// ✅
private static readonly ConditionalWeakTable<DbContext, List<IDomainEvent>> _buffer = new();

public override int SavedChanges(SaveChangesCompletedEventData e, int result)
{
    if (e.Context is null || !_buffer.TryGetValue(e.Context, out var events)) return result;
    _buffer.Remove(e.Context);            // ← обязательно снять
    _dispatcher.Dispatch(events);
    return result;
}

public override void SaveChangesFailed(DbContextErrorEventData e)   => Restore(e.Context);
public override void SaveChangesCanceled(DbContextEventData e)      => Restore(e.Context);
```

**Проверка.** Тест на 1000 контекстов в цикле + `GC.Collect()` →
`ConditionalWeakTable` пустеет; тест на исключение в `SaveChanges` →
события вернулись на агрегаты.

---

## 3. Порядок интерсепторов

**Симптом.** `WithAuditing().WithSoftDeletes()` и обратный порядок дают разный
результат (`UpdatedBy` проставлен или нет).

**Патч.** Ввести явный приоритет и сортировать при сборке.

```csharp
public interface IOrderedInterceptor { int Order { get; } }

// Validation(-300) → Guards(-200) → MultiTenancy(-150)
// → SoftDelete(-100) → Audit(0) → Version(50)
// → ChangeLog(100) → Outbox(200) → DomainEvents(300) → Metrics(1000)

internal IInterceptor[] Build() => _items
    .OrderBy(i => (i as IOrderedInterceptor)?.Order ?? 0)
    .ToArray();
```

**Проверка.** Тест: регистрация в «неправильном» порядке даёт тот же
результат, что и в «правильном».

---

## 4. ChangeLog: PK у Added-сущностей

**Симптом.** На `SavingChanges` ключ ещё не назначен БД → в аудит-трейл
попадает `0`.

```csharp
// ✅ двухфазная запись
public override InterceptionResult<int> SavingChanges(DbContextEventData e, InterceptionResult<int> r)
{
    _pending = Capture(e.Context!.ChangeTracker.Entries());  // сохраняем EntityEntry, не значения PK
    return r;
}

public override int SavedChanges(SaveChangesCompletedEventData e, int result)
{
    foreach (var (entry, log) in _pending)
        log.EntityId = SerializeKey(entry);                  // ключ уже есть
    e.Context!.Set<ChangeLogEntry>().AddRange(_pending.Select(p => p.log));
    return e.Context.SaveChanges() + result;                 // в той же транзакции
}
```

**Важно:** второй `SaveChanges` обязан идти внутри той же транзакции —
иначе теряется атомарность. Оберните в `IDbContextTransaction`, если её нет.

---

## 5. SoftDelete: каскады и идемпотентность

```csharp
foreach (var entry in ctx.ChangeTracker.Entries<ISoftDeletableEntity>()
                        .Where(e => e.State == EntityState.Deleted))
{
    if (entry.Entity.IsDeleted) { entry.State = EntityState.Unchanged; continue; } // идемпотентность

    entry.State = EntityState.Modified;
    entry.Entity.IsDeleted = true;
    entry.Entity.DeletedAtUtc = _time.GetUtcNow();
    entry.Entity.DeletedBy = _users?.UserName;

    // Created* не трогаем
    entry.Property(nameof(IAuditableEntity.CreatedAtUtc)).IsModified = false;
}

// Каскад: дети, не поддерживающие soft delete, помеченные Deleted вместе с родителем
var orphans = ctx.ChangeTracker.Entries()
    .Where(e => e.State == EntityState.Deleted && e.Entity is not ISoftDeletableEntity)
    .ToList();
if (orphans.Count > 0 && _policy == CascadePolicy.Throw)
    throw new SoftDeleteCascadeException(orphans.Select(o => o.Metadata.Name));
```

---

## 6. `ValueTask` в async-методах интерсептора

```csharp
// ❌ теряет результат, ломает цепочку интерсепторов
public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
    DbContextEventData e, InterceptionResult<int> result, CancellationToken ct) => default;

// ✅
public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
    DbContextEventData e, InterceptionResult<int> result, CancellationToken ct)
{
    ct.ThrowIfCancellationRequested();
    Apply(e.Context);
    return ValueTask.FromResult(result);
}
```

---

## 7. `WritesRequireTransaction`: ложные срабатывания на SaveChanges

```csharp
private static bool ShouldCheck(CommandEventData e) =>
    e.CommandSource is not (CommandSource.SaveChanges or CommandSource.Migrations)
    && IsWrite(e.Command.CommandText);
```

---

## 8. ReadOnlyGuard: перестать угадывать по тексту

```csharp
public override InterceptionResult<DbDataReader> ReaderExecuting(
    DbCommand command, CommandEventData e, InterceptionResult<DbDataReader> result)
{
    var isWrite = e.CommandSource switch
    {
        CommandSource.SaveChanges       => true,
        CommandSource.Migrations        => true,
        CommandSource.BulkUpdate        => true,
        CommandSource.ExecuteSqlRaw     => _sqlHeuristic(command.CommandText), // только здесь эвристика
        _                               => false,
    };
    return isWrite ? throw new ReadOnlyContextException(command.CommandText) : result;
}
```

Плюс `[GeneratedRegex]` вместо рантайм-регекса:

```csharp
[GeneratedRegex(@"^\s*(?:with\b[\s\S]*?\)\s*)?(insert|update|delete|merge|truncate|drop|alter|create)\b",
    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
private static partial Regex WriteStatement();
```

---

## 9. Outbox: защита от двойной доставки

Добавить в `OutboxMessage`: `LockedUntilUtc`, `AttemptCount`, `Error`,
`ProcessedAtUtc`. Забор пачки — атомарный:

```sql
-- PostgreSQL
UPDATE outbox SET locked_until_utc = now() + interval '1 minute'
WHERE id IN (
    SELECT id FROM outbox
    WHERE processed_at_utc IS NULL AND (locked_until_utc IS NULL OR locked_until_utc < now())
    ORDER BY id LIMIT @batch
    FOR UPDATE SKIP LOCKED)
RETURNING *;
```

Плюс экспоненциальный backoff по `AttemptCount` и dead-letter после N попыток.

---

## 10. ConcurrencyRetry: не повторять во внешней транзакции

```csharp
if (context.Database.CurrentTransaction is not null)
    return;   // повтор внутри чужой транзакции невозможен — она уже aborted

var db = entry.GetDatabaseValues();
if (db is null)                       // строку удалили — retry бессмысленен
    throw new ConcurrencyConflictException("Row was deleted by another user.");
entry.OriginalValues.SetValues(db);
```

---

## 11. Кэш второго уровня

- Ключ: `SQL + отсортированные (имя, тип, значение) параметров + имя контекста`.
- Инвалидация — на `TransactionCommitted`, а не на команде записи.
- Вынести хранилище за интерфейс:

```csharp
public interface IQueryCacheStore
{
    bool TryGet(string key, out CachedResult value);
    void Set(string key, CachedResult value, TimeSpan ttl);
    void Invalidate(string tag);
}
```

- Полностью буферизовать `DbDataReader` в `DataTable`/массив строк перед
  возвратом второму потребителю.

---

## 12. Публичные контракты: согласовать типы времени

```csharp
public interface IAuditableEntity
{
    DateTimeOffset  CreatedAtUtc { get; set; }
    string?         CreatedBy    { get; set; }
    DateTimeOffset? UpdatedAtUtc { get; set; }   // было не-nullable
    string?         UpdatedBy    { get; set; }
}

public interface ILoadTimestamped { DateTimeOffset? LoadedAtUtc { get; set; } } // было DateTime?
```

Это ломающее изменение → мажорная версия пакета.

---

## 13. Проектная гигиена

```xml
<PropertyGroup>
  <Nullable>enable</Nullable>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  <AnalysisMode>All</AnalysisMode>
  <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
  <GenerateDocumentationFile>true</GenerateDocumentationFile>
  <IsAotCompatible>true</IsAotCompatible>
</PropertyGroup>
```

- `Directory.Packages.props` — central package management.
- `Microsoft.CodeAnalysis.PublicApiAnalyzers` — контроль публичного API.
- `dotnet format --verify-no-changes` в CI.
- Разбить пакет: `.Core` / `.Auditing` / `.Outbox` / `.Caching` / `.Security`
  / `.Observability` / `.MediatR`.

---

## 14. Тесты, которых не хватает

| Сценарий | Почему важен |
|---|---|
| Порядок интерсепторов (все перестановки ключевой пятёрки) | тихие баги аудита |
| Изоляция тенантов в двух параллельных скоупах | утечка данных |
| `SaveChanges` с исключением → буферы очищены | утечка памяти |
| Outbox с двумя воркерами → нет дублей | at-least-once → exactly-once-ish |
| Soft delete родителя с не-soft-delete детьми | осиротевшие записи |
| Кэш L2 + откат транзакции | грязное чтение |
| Retry внутри внешней транзакции | «повтор в мёртвой транзакции» |
| Concurrency: 50 параллельных обновлений одной строки | корректность Version |

Цель — не «70 тестов», а покрытие каждого интерсептора тройкой
happy path / краевой случай / взаимодействие с соседом.
