# EfCore.Interceptors

[![NuGet](https://img.shields.io/nuget/v/EfCore.Interceptors?label=NuGet&logo=nuget&color=004880)](https://www.nuget.org/packages/EfCore.Interceptors)
[![NuGet Downloads](https://img.shields.io/nuget/dt/EfCore.Interceptors?label=downloads)](https://www.nuget.org/packages/EfCore.Interceptors)
[![License: MIT](https://img.shields.io/github/license/Maxvalpav/EfCoreInterceptors?color=green)](LICENSE)

## English (short)

Production-ready interceptor suite for **Entity Framework Core 10** (.NET 10).
Covers all 7 EF Core interceptor types with common scenarios: auditing, soft delete, domain events & outbox,
SQL logging, slow query detection, second-level cache, N+1 detection, query hints, read-only guard,
session init, connection/transaction lifecycle, query tree, materialization stamping and identity resolution.

```bash
dotnet add package EfCore.Interceptors
```

```csharp
using EfCore.Interceptors;

optionsBuilder.UseSqlServer(connectionString).UseEfInterceptors(s => s
    .WithAuditing(users)
    .WithSoftDeletes(users)
    .WithDomainEvents(dispatcher)
    .WithSlowQueryWarning(TimeSpan.FromSeconds(2))
    .WithSqlLogging());
```

> Full documentation below is in Russian. See code samples and API list — names are in English.

---

## Русский

Библиотека production-ready перехватчиков (interceptors) для **Entity Framework Core 10** (.NET 10).
Покрывает все 7 типов интерсепторов EF Core типовыми сценариями: аудит, soft delete, доменные события,
логирование SQL, детект медленных запросов, query hints, read-only guard, session-init, транзакции,
дерево запросов, штамп материализации и разрешение коллизий идентичности.

```bash
dotnet add package EfCore.Interceptors   # либо ссылка на проект src/EfCore.Interceptors
```

---

## Быстрый старт

```csharp
using EfCore.Interceptors;

optionsBuilder
    .UseSqlServer(connectionString)
    .UseEfInterceptors(s => s
        .WithAuditing(users)                                  // Created*/Updated* колонки
        .WithSoftDeletes(users)                               // DELETE -> UPDATE IsDeleted=1
        .WithDomainEvents(dispatcher)                         // публикация событий после коммита
        .WithSlowQueryWarning(TimeSpan.FromSeconds(2))        // WARN на медленные запросы
        .WithSqlLogging()                                     // лог всех SQL-команд
    );
```

Регистрация через DI:

```csharp
services.AddEfInterceptors(s => s.WithAuditing().WithSoftDeletes());
services.AddDbContext<AppDbContext>((sp, options) =>
    options.UseSqlServer(cs).UseEfInterceptorsFrom(sp));
```

> Если интерсептору нужны **scoped-зависимости** (например, текущий пользователь из `IHttpContextAccessor`),
> зарегистрируйте сам интерсептор как Scoped и резолвьте вручную:

```csharp
services.AddScoped<AuditSaveChangesInterceptor>();
services.AddDbContext<AppDbContext>((sp, options) =>
    options.UseSqlServer(cs).AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>()));
```

---

## Состав библиотеки

### 1. ISaveChangesInterceptor

| Интерсептор | Что делает |
|---|---|
| `AuditSaveChangesInterceptor` | Заполняет `CreatedAtUtc/CreatedBy/UpdatedAtUtc/UpdatedBy` у сущностей `IAuditableEntity`. При обновлении защищает Created* от подмены (`IsModified = false`). Поддерживает `TimeProvider` для тестов. |
| `SoftDeleteSaveChangesInterceptor` | Превращает `Remove()` в логическое удаление у сущностей `ISoftDeletableEntity`: `State = Modified`, проставляет `IsDeleted/DeletedAtUtc/DeletedBy`. Строка остаётся в БД. Для скрытия из выборок добавьте глобальный фильтр `HasQueryFilter(e => !e.IsDeleted)` — интерсептор отвечает только за запись. |
| `DomainEventsSaveChangesInterceptor` | Outbox-диспетчеризация доменных событий: перед сохранением снимает snapshot событий агрегатов `IHasDomainEvents`, после успешного коммита публикует их через `IDomainEventDispatcher` и очищает. При неудачном сохранении события остаются на агрегатах для повторной попытки. |
| `ChangeLogSaveChangesInterceptor` | **Аудит-трейл в БД**: пишет в таблицу `ChangeLogEntry` имя сущности, сериализованный PK, действие (Added/Modified/Deleted), JSON-дифф изменённых свойств (old/new) и актёра — в той же транзакции, что и бизнес-изменение. Требует замапить `ChangeLogEntry`. |
| `OutboxSaveChangesInterceptor` | **Атомарный outbox**: сериализует доменные события агрегатов в строки `OutboxMessage`, вставляемые в той же транзакции; фоновый воркер доставляет и штампует `ProcessedAtUtc`. При сбое сохранения события возвращаются на агрегаты. Требует замапить `OutboxMessage`. |
| `MultiTenancySaveChangesInterceptor` | **Мультитенантность**: проставляет `TenantId` у `ITenantEntity` на вставке из `ITenantProvider`; модификацию чужого тенанта пресекает `CrossTenantAccessException`. Для изоляции чтений добавьте фильтр по тенанту. |
| `MassOperationGuardSaveChangesInterceptor` | Предохранитель от массовых операций: если один SaveChanges трогает больше N Added/Modified/Deleted — бросает `MassOperationException` со статистикой. |
| `ConcurrencyExceptionTranslatorInterceptor` | Переводит низкоуровневый `DbUpdateConcurrencyException` в доменный `ConcurrencyConflictException`, чтобы верхние слои не зависели от EF. |
| `DeleteGuardSaveChangesInterceptor` | Абсолютный запрет удаления `IProtectedEntity` (финансовые/юридические записи) — `ProtectedEntityException` до похода в БД. |
| `ImmutableEntityGuardSaveChangesInterceptor` | Append-only сущности `IImmutableEntity`: вставки разрешены, изменение и удаление — `ImmutableEntityException`. |
| `PropertyEncryptionSaveChangesInterceptor` + `PropertyDecryptionMaterializationInterceptor` | Прозрачное шифрование свойств `[Encrypted]`: AES-GCM шифротекст в БД, расшифровка при материализации. Реализуйте `IPropertyValueEncryptor` (готовый `AesGcmPropertyValueEncryptor`). |
| `ValidationSaveChangesInterceptor` | Агрегированная DataAnnotations-валидация перед сохранением: все нарушения всех сущностей в одном `EntityValidationException`, а не первое падение на БД. |
| `VersionIncrementSaveChangesInterceptor` | Ведёт счётчик `IVersionedEntity.Version` (+1 при каждом update) для optimistic concurrency на провайдерах без rowversion; объявите свойство concurrency-token — и устаревшие записи будут отклоняться. |
| `ConcurrencyRetrySaveChangesInterceptor` | Классический «retry вокруг SaveChanges» как интерсептор: при конфликте токена записи приводятся по политике **ClientWins** (last-write-wins) или **StoreWins** (перезагрузка), сохранение повторяется до N раз с экспоненциальной задержкой, вызывающий код видит успех. После исчерпания — исходный `DbUpdateConcurrencyException`. |
| `CustomValidationSaveChangesInterceptor` | Адаптер внешних валидаторов без зависимости от них: реализуйте `IEntityValidator` (FluentValidation и т.п.) — агрегированные ошибки в одном `EntityValidationException`. |

### 2. IDbCommandInterceptor

| Интерсептор | Что делает |
|---|---|
| `SqlLoggingCommandInterceptor` | Логирует каждую SQL-команду: старт (Debug), завершение с длительностью (Information), отмена (Information), ошибка (Error). Опция `includeParameterValues` включает/скрывает значения параметров; `textRedactor` маскирует PII/карты во всём, что попадает в лог. |
| `SlowQueryCommandInterceptor` | Предупреждение (Warning), когда длительность команды превысила порог. Использует `CommandExecutedEventData.Duration` — без ручных секундомеров. Есть фильтр «какие команды проверять». |
| `QueryHintsCommandInterceptor` | Дописывает провайдер-специфичные хинты к SQL. Выбор хинта — по тегам `TagWith("key")` (словарь `тег -> хинт`) или произвольным предикатом над текстом SQL. Пример для SQL Server: `{"recompile": "OPTION (RECOMPILE)"}`. |
| `ReadOnlyGuardCommandInterceptor` | Блокирует записи: любой INSERT/UPDATE/DELETE/DDL бросает `ReadOnlyContextException` ещё до обращения к БД. Включается по предикату (например, только для reporting-контекста). Чтения не затрагиваются. |
| `CachingCommandInterceptor` | **Кэш второго уровня**: идентичные SELECT (SQL + значения параметров) отдаются из памяти без похода в БД; TTL, ручная инвалидация (`InvalidateAll` / `Invalidate("TableName")`), опция `invalidateOnWrites: true` чистит кэш после любой записи, внутри явных транзакций кэш по умолчанию отключён. Кэшируются только SELECT/WITH. |
| `NPlusOneDetectorCommandInterceptor` | Детектор N+1: EF параметризует запросы, поэтому повтор одинакового шаблона SQL в одном контексте — классический признак N+1. Warning один раз на шаблон при превышении порога. |
| `MetricsCommandInterceptor` | Метрики System.Diagnostics.Metrics: гистограмма `ef.command.duration` (ms) и счётчики `ef.command.executed` / `ef.command.failed` — совместимо с OTel/MeterListener. |
| `WritesRequireTransactionCommandInterceptor` | Юнит-of-work дисциплина: write-команда вне явной транзакции (например, ad-hoc `ExecuteSqlRaw`) бросает `MissingTransactionException`. |
| `CommandTimeoutCommandInterceptor` | Динамический `CommandTimeout` на команду: селектор по тексту SQL или словарь тегов (`TagWith("report")` → 300 сек), остальное — контекстный дефолт. |
| `CommandSourceBlocker` | Governance: блокирует команды по источнику — по умолчанию запрещает запуск EF-миграций из рантайма приложения (`BlockedCommandSourceException`); можно заблокировать ExecuteSqlRaw/ExecuteDelete и т.п. |
| `RawSqlUsageDetector` | Прозрачность обходных путей: Warning + callback на каждый FromSqlRaw/SqlQuery/ExecuteSqlRaw. |

### 3. IDbConnectionInterceptor

| Интерсептор | Что делает |
|---|---|
| `ConnectionLifecycleLoggingInterceptor` | Логи открытия/закрытия/ошибок соединения с длительностью; маскирует `Password/Pwd/Secret/Token` в строке подключения. |
| `SessionInitConnectionInterceptor` | Выполняет список SQL-операторов при каждом открытии соединения — настройки, которые не выражаются строкой подключения: `SET TRANSACTION ISOLATION LEVEL ...` / `EXEC sp_set_session_context 'TenantId', @p` (SQL Server), `SET search_path TO ...` (PostgreSQL), `PRAGMA foreign_keys=ON` (SQLite). |
| `DynamicConnectionStringConnectionInterceptor` | **Маршрутизация соединений**: резолвит строку подключения в момент открытия через callback — database-per-tenant, read/write split на реплику, динамические failover-цели. |

### 4. IDbTransactionInterceptor

| Интерсептор | Что делает |
|---|---|
| `TransactionLifecycleLoggingInterceptor` | Полный жизненный цикл транзакций: начало, коммит, rollback (Warning), savepoints, ошибки (Error). |
| `ForcedIsolationLevelTransactionInterceptor` | Принудительно начинает каждую транзакцию (включая неявные вокруг SaveChanges) с заданного `IsolationLevel`, перехватывая создание транзакции. Учитывайте возможности провайдера: SQL Server соблюдает уровни, SQLite игнорирует. |

### 5. IQueryExpressionInterceptor

| Интерсептор | Что делает |
|---|---|
| `QueryTreeLoggingInterceptor` | Пишет LINQ expression tree каждого запроса перед компиляцией (Debug). Точка расширения: наследуйтесь и переопределите `Transform(Expression)` — возвращённое дерево компилируется вместо исходного. |
| `StrictQueryPolicyQueryExpressionInterceptor` | Комплаенс-страж: запрещает опасные формы запросов — `IgnoreQueryFilters()` (по умолчанию), опционально `ExecuteDelete()`/`ExecuteUpdate()` — бросая `QueryPolicyViolationException` ещё на этапе компиляции запроса. |

### 6. IMaterializationInterceptor

| Интерсептор | Что делает |
|---|---|
| `LoadStampingMaterializationInterceptor` | Проставляет `LoadedAtUtc` сущностям `ILoadTimestamped` в момент материализации — видно, насколько «протух» объект в памяти. |
| `InitializationMaterializationInterceptor` | Вызывает `IInitializable.OnLoaded()` после материализации — место для пересчёта транзиентного состояния. |
| `FactoryMethodInstantiationBindingInterceptor` | Покрывает `IInstantiationBindingInterceptor`: подменяет constructor binding фабрикой из словаря `Type -> Func<object>` для legacy-типов без пригодного конструктора. |
| `RequireQueryTagsInterceptor` | Требует теги `TagWith(...)`: конкретный набор или «хотя бы один» — иначе `QueryPolicyViolationException`. Делает вопрос «какая фича выдала этот запрос» ответным по SQL-комментарию в трейсах. |
| `PropertyDecryptionMaterializationInterceptor` | Расшифровывает `[Encrypted]` свойства (пара к `PropertyEncryptionSaveChangesInterceptor`). |

### 7. IIdentityResolutionInterceptor

| Интерсептор | Что делает |
|---|---|
| `OverwriteIdentityResolutionInterceptor` / `IgnoreIncomingIdentityResolutionInterceptor` | Разрешают конфликт ключей при `Attach/Add` второй копии той же сущности: входящие значения затирают отслеживаемые (last-write-wins) или игнорируются (cache semantics). В EF Core 10 также есть встроенные `UpdatingIdentityResolutionInterceptor` / `IgnoringIdentityResolutionInterceptor`. |
| `NullMergeIdentityResolutionInterceptor` | Null-preserving merge: входящие значения заполняют только null/пустые свойства отслеживаемого экземпляра — непустые данные всегда побеждают. |
| `NewestWinsIdentityResolutionInterceptor` | Last-write-wins по штампам: выживает экземпляр с более свежим `UpdatedAtUtc`. |

### 8. Хелперы модели (не интерсепторы, но завершают картину)

```csharp
modelBuilder.ApplySoftDeleteFilters();          // !IsDeleted всем ISoftDeletableEntity
modelBuilder.ApplyTenantFilters(tenantProvider); // TenantId == current всем ITenantEntity
```
Фильтры сливаются с существующими через AndAlso (анонимный) или добавляются отдельными
именованными фильтрами EF 10 (`EfCoreInterceptors.SoftDelete` / `.Tenant`) — ничего не затирается.

### 9. Наблюдаемость (System.Diagnostics.Metrics)

Все метрики публикуются в метре `EfCore.Interceptors` — подключите MeterListener/OTLP-экспортёр.

| Интерсептор | Инструменты |
|---|---|
| `SaveChangesMetricsInterceptor` | `ef.save.duration` (гистограмма, ms), `ef.save.executed`, `ef.save.failed`, `ef.save.entities` |
| `TransactionMetricsInterceptor` | `ef.transaction.started/committed/rolledback/failed`, `ef.transaction.duration` |
| `ConnectionMetricsInterceptor` | `ef.connection.opened/closed/failed`, `ef.connection.open_duration` |
| `LongRunningTransactionDetector` | Warning-лог, когда транзакция держится дольше порога (блокировки/vacuum) |
| `SlowSaveChangesDetector` | Warning-лог, когда один SaveChanges дольше порога |
| `CommandsPerSaveDiagnosticInterceptor` | Сколько SQL-команд породил один SaveChanges — ловит скрытый «N+1 на записи» |
| `MaterializationMetricsInterceptor` | `ef.materialization.entities` — всплеск = cartesian explosion / забыли пагинацию |
| `MetricsCommandInterceptor` (см. выше) | `ef.command.duration/executed/failed` |

Пример чтения метрик в тестах/консоли:

```csharp
using var listener = new MeterListener();
listener.InstrumentPublished = (instr, l) =>
{
    if (instr.Meter.Name == "EfCore.Interceptors") l.EnableMeasurementEvents(instr);
};
listener.SetMeasurementEventCallback<long>((inst, value, tags, _) => Console.WriteLine($"{inst.Name}: {value}"));
listener.Start();
```

---

## Контракты сущностей

```csharp
public interface IAuditableEntity      { DateTimeOffset CreatedAtUtc { get; set; } string? CreatedBy { get; set; } DateTimeOffset? UpdatedAtUtc { get; set; } string? UpdatedBy { get; set; } }
public interface ISoftDeletableEntity  { bool IsDeleted { get; set; } DateTimeOffset? DeletedAtUtc { get; set; } string? DeletedBy { get; set; } }
public interface ILoadTimestamped      { DateTimeOffset? LoadedAtUtc { get; set; } }

public interface IDomainEvent          { DateTimeOffset OccurredAtUtc { get; } }
public interface IHasDomainEvents      { IReadOnlyList<IDomainEvent> DomainEvents { get; } void AddDomainEvent(IDomainEvent e); void ClearDomainEvents(); }
public interface IDomainEventDispatcher{ void Dispatch(IEnumerable<IDomainEvent> events); Task DispatchAsync(IEnumerable<IDomainEvent> events, CancellationToken ct = default); }
public interface ICurrentUserProvider  { string? UserName { get; } }   // + StaticCurrentUserProvider
public interface ITenantEntity         { string? TenantId { get; set; } }
public interface ITenantProvider       { string? CurrentTenantId { get; } }  // + StaticTenantProvider
```

Свойства контрактов должны быть замаплены в модели.

Доменные исключения библиотеки: `CrossTenantAccessException`, `MassOperationException`, `ConcurrencyConflictException`, `QueryPolicyViolationException`, `MissingTransactionException`, `ReadOnlyContextException`.

Готовые сущности для маппинга (namespace `EfCore.Interceptors.Entities`): `ChangeLogEntry` (аудит-трейл) и `OutboxMessage` (outbox) — добавьте их в модель:
```csharp
modelBuilder.Entity<ChangeLogEntry>();
modelBuilder.Entity<OutboxMessage>();
```

## Типовая связка: soft delete целиком

```csharp
// 1. Сущность
public class Order : ISoftDeletableEntity, IAuditableEntity { /* ... */ }

// 2. Глобальный фильтр читающей стороны
modelBuilder.Entity<Order>().HasQueryFilter(o => !o.IsDeleted);

// 3. Регистрация пишущей стороны
.UseEfInterceptors(s => s.WithSoftDeletes(users))
```

## Доменные события end-to-end

```csharp
public class Order : IHasDomainEvents
{
    public int Id { get; set; }
    public decimal Total { get; set; }
    public bool IsPaid { get; private set; }

    private readonly List<IDomainEvent> _events = [];
    [NotMapped] public IReadOnlyList<IDomainEvent> DomainEvents => _events;
    public void AddDomainEvent(IDomainEvent e) => _events.Add(e);
    public void ClearDomainEvents() => _events.Clear();

    public void MarkPaid()
    {
        IsPaid = true;
        AddDomainEvent(new OrderPaid(Id, Total));
    }
}

public sealed record OrderPaid(int OrderId, decimal Total) : IDomainEvent
{
    public DateTimeOffset OccurredAtUtc { get; } = DateTimeOffset.UtcNow;
}
```

---

## API регистрации

**На options builder** (работает везде, DI не обязателен):
`UseEfInterceptors(Action<EfInterceptorsSetup>)` — fluent: `WithAuditing`, `WithSoftDeletes`,
`WithDomainEvents`, `WithSqlLogging`, `WithSlowQueryWarning`, `WithQueryHints`, `WithReadOnlyGuard`,
`WithSessionInit`, `WithConnectionLogging`, `WithTransactionLogging`, `WithQueryTreeLogging`,
`WithLoadStamping`, `WithIdentityResolution(overwriteExisting)`,
`WithChangeLog`, `WithOutbox`, `WithMultiTenancy(provider)`, `WithMassOperationGuard(...)`,
`WithConcurrencyTranslation()`, `WithSecondLevelCache(ttl)`, `WithNPlusOneDetection(threshold)`,
`WithCommandMetrics(meterName)`, `WithTransactionalWrites()`, `WithDynamicConnectionString(resolver)`,
`WithForcedIsolationLevel(level)`, `WithStrictQueryPolicy(...)`,
`WithNullMergingIdentityResolution()`,
`WithDeleteGuard()`, `WithImmutableGuard()`, `WithSaveChangesMetrics()`,
`WithTransactionMetrics()`, `WithConnectionMetrics()`,
`WithLongRunningTransactionDetection(threshold)`, `WithCommandTimeout(...)` / `WithCommandTimeoutByTags(...)`,
`WithInitialization()`, `WithPropertyEncryption(encryptor)`, `WithCommandSourceBlocker(...)`, `WithRawSqlUsageDetection(...)`, `WithVersionCounter()`, `WithNewestWinsIdentityResolution()`, `WithMaterializationMetrics()`, `WithRequiredQueryTags(...)` / `WithRequireAnyQueryTag()`,
`WithValidation()`, `WithSlowSaves(threshold)`, `WithConstructorFactories(map)`, `WithConcurrencyRetry(policy, maxRetries)`, `WithCustomValidation(...)`, `WithCommandsPerSaveDiagnostics(n)`
+ `Add(IInterceptor)` для своих.

Со стороны модели: `modelBuilder.ApplySoftDeleteFilters()` и `modelBuilder.ApplyTenantFilters(provider)`.

**Через DI**: `services.AddEfInterceptors(...)` + `.UseEfInterceptorsFrom(serviceProvider)`.


---

## Готовый рабочий пример: ASP.NET Core (samples/WebApiSample)

Запуск и проверка вживую:

```bash
dotnet run --project samples/WebApiSample --urls http://localhost:5000
# в другом терминале:
curl -X POST http://localhost:5000/products -H "X-User: alice" \
     -H "Content-Type: application/json" -d '{"name":"Keyboard","price":49.9}'
curl http://localhost:5000/outbox        # процессор уже доставил события (ProcessedAtUtc != null)
```

Ключевой приём — **scoped-интерсепторы для per-request состояния** (текущий пользователь из заголовка):

```csharp
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserProvider, HttpContextCurrentUserProvider>(); // читает X-User
builder.Services.AddScoped<AuditSaveChangesInterceptor>();                          // сам интерсептор тоже scoped

builder.Services.AddDbContext<ProductDbContext>((sp, options) => options
    .UseSqlite(cs)
    .AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>())   // ← из контейнера
    .UseEfInterceptors(s => s
        .WithSoftDeletes()                              // stateless — можно прямо так
        .WithOutbox()                                   // события -> таблица той же транзакцией
        .WithSlowQueryWarning(TimeSpan.FromMilliseconds(300))
        .WithSqlLogging(sampleRate: 0.25)               // 25% SQL-логов, ошибки — всегда
        .WithCommandMetrics()
        .WithNPlusOneDetection(5)));

// фоновая доставка outbox-сообщений:
builder.Services.AddScoped<IOutboxMessageHandler, ProductCreatedHandler>();
builder.Services.AddOutboxProcessor<ProductDbContext>(pollInterval: TimeSpan.FromSeconds(1));
```

Эндпоинты демки: `GET/POST/DELETE /products`, `POST /products/{id}/restore` (корзина),
`GET /outbox` (очередь и статусы доставки).

---

## Экосистема проекта

### MediatR-интеграция (`src/EfCore.Interceptors.MediatR`)

Мост между доменными событиями и MediatR-пайплайном:

```csharp
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<Program>());
builder.Services.AddMediatRDomainEventDispatcher();   // IDomainEventDispatcher -> IMediator

// обработчики подписываются на обёртку:
public class ProductCreatedHandler
    : INotificationHandler<DomainEventNotification<ProductCreated>>
{
    public Task Handle(DomainEventNotification<ProductCreated> n, CancellationToken ct) { ... }
}
```

### Бенчмарки (`benchmarks/EfCore.Interceptors.Benchmarks`)

Оверхед полного набора интерсепторов на SaveChanges против чистого контекста:

```bash
dotnet run -c Release --project benchmarks/EfCore.Interceptors.Benchmarks --filter *SaveChanges*
```

### CI

`.github/workflows/ci.yml`: restore → Release-build → tests → `dotnet pack` → артефакт `.nupkg`.

### Пакет

```bash
dotnet pack src/EfCore.Interceptors -c Release -o artifacts
# artifacts/EfCore.Interceptors.<version>.nupkg (+ snupkg), README внутри пакета
```
## Ограничения bulk-операций — критично

`ExecuteUpdate` / `ExecuteDelete` (EF Core 7+) транслируются напрямую в `UPDATE … WHERE` / `DELETE … WHERE` и **не проходят через `ISaveChangesInterceptor`**.
Следовательно они обходят: soft delete → физическое удаление, шифрование → запись plaintext, guards/валидацию/аудит/ChangeLog/Outbox/domain events — всё молча.

| Интерсептор | Что происходит при bulk | Тяжесть |
|---|---|---|
| `SoftDeleteSaveChangesInterceptor` | Физическое удаление | 🔴 потеря данных |
| `PropertyEncryptionSaveChangesInterceptor` | Открытый текст в колонке шифротекста | 🔴 утечка/порча |
| `DeleteGuard` / `ImmutableGuard` | Удаление без исключения | 🔴 комплаенс |
| `MultiTenancySaveChangesInterceptor` | `SetProperty(e=>e.TenantId,"other")` передаст строки чужому тенанту | 🔴 безопасность |
| `ChangeLog` / `Audit` / `Outbox` / `DomainEvents` / `Validation` | Молча не отрабатывают | 🟠 |

Защита:
```csharp
// Уровень 1 — guard (в библиотеке уже есть):
.UseEfInterceptors(s => s.WithBulkOperationGuard(BulkOperationPolicy.Throw))

// Уровень 2 — strict policy на этапе компиляции LINQ:
.UseEfInterceptors(s => s.WithStrictQueryPolicy(forbidExecuteDelete: true, forbidExecuteUpdate: true))

// Безопасные альтернативы — библиотечные хелперы (todo vNext):
// await db.Orders.Where(...).ExecuteSoftDeleteAsync(users, timeProvider, ct);
```
Подробно: см. раздел «Ограничения bulk-операций» выше.

## Провайдеры и совместимость

| Провайдер | Примечание |
|---|---|
| SQL Server, PostgreSQL, SQLite, MySQL/Pomelo | Полная поддержка реляционных интерсепторов |
| Cosmos (нереляционный) | `IDbCommandInterceptor`/`IDbConnectionInterceptor`/`IDbTransactionInterceptor` не вызываются — работают только SaveChanges/Materialization/IdentityResolution |
| InMemory | Не реляционный — команда-тесты на нём бессмысленны для `SqlLogging`/`Caching`/`N+1` |

Фичи EF Core:
- **`AddDbContextPool` ломает состояние по `DbContext`** (доменные события/outbox счётчики перетекают между запросами). Реализуйте `IResettableService.ResetState()` на контексте или документируйте несовместимость.
- **Compiled models** фиксируют query filters — tenant-фильтр, зависящий от рантайма, не сработает.
- **Complex types** (`entry.ComplexProperties`) теперь учитываются в аудите/ChangeLog/шифровании (рекурсивный обход).
- **Шифрование**: payload v1 = `0x01|nonce|tag|cipher` + AAD (`table|column|pk`) против cross-column swap; legacy payload без версии дешифруется по fallback.

## Нюансы и рекомендации

- **Времена жизни.** Интерсепторы регистрируются на каждый `DbContext`; сами классы stateless и потокобезопасны (кроме документированных буферов доменных событий/outbox, ключуемых по экземпляру контекста через `ConditionalWeakTable`). Один экземпляр можно шарить между контекстами — так работает инвалидация кэша (`WithSecondLevelCache` + общий `CachingCommandInterceptor`).
- **Мультитенантность**: `ApplyTenantFilters(provider)` захватывает провайдера в кэш модели — первый тенант «залипает». Регистрируйте `TenantModelCacheKeyFactory` (`options.ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>()`) или используйте фильтр через свойство контекста `e => e.TenantId == CurrentTenantId`. Модификация `TenantId` после создания запрещена (immutable) — `CrossTenantAccessException` при `OriginalValue != CurrentValue`.
- **Soft delete не фильтрует чтения.** Обязательно добавляйте глобальный query filter; иначе удалённые строки будут видны. `IgnoreQueryFilters()` также покажет их (удобно для корзины) — а `WithStrictQueryPolicy` может такие вызовы запрещать.
- **Доменные события** диспетчеризуются строго после коммита (at-least-once внутри процесса). Хендлер упал — получите `InvalidOperationException`; данные уже сохранены. Для гарантированной доставки используйте `WithOutbox()`: события попадают в таблицу в той же транзакции. Outbox processor использует `LockedUntilUtc/AttemptCount` + `FOR UPDATE SKIP LOCKED`-подобный claim для multi-instance.
- **ChangeLog/Outbox** требуют замапленных сущностей `ChangeLogEntry`/`OutboxMessage`. Дифф пишется по всем свойствам Added/Deleted и по изменённым для Modified (включая owned). Для `Added` с DB-генерируемым PK ключ патчится вторым `SaveChanges` в той же транзакции.
- **Кэш второго уровня** — in-memory, на процесс, с `SizeLimit` и инвалидацией на `TransactionCommitted`. Включите `invalidateOnWrites: true`, чтобы записи автоматически чистили кэш после коммита, либо зовите `Invalidate*()` после внешних изменений. Внутри явных транзакций кэш по умолчанию обойдён. В multi-instance используйте `IQueryCacheStore` / Redis.
- **QueryHints** модифицируют текст SQL — тестируйте на целевом провайдере (хинт SQL Server на SQLite даст синтаксическую ошибку; используйте комментарии как в примере). Батчи с несколькими statement не патчатся.
- **ReadOnlyGuard / StrictQueryPolicy** — guard теперь смотрит на `CommandSource` (SaveChanges/Migrations/BulkUpdate) и только для `ExecuteSqlRaw` — на текст через `[GeneratedRegex]` (best-effort). Policy проверяет формы запросов при компиляции.
- **Шифрование `[Encrypted]`** хранит в колонке шифротекст: поиск по равенству/LIKE по зашифрованным колонкам невозможен (nonce случайный). `AesGcmPropertyValueEncryptor` v1: `version|nonce|tag|cipher` + optional AAD (`table|column|pk`) против перестановки шифротекстов; legacy payload дешифруется fallback. Для продакшена берите envelope-шифрование с внешним KMS. Шифрование выполняется на клиенте.
- **ForcedIsolationLevel** применяется ко всем транзакциям контекста, включая неявные SaveChanges — оценивайте влияние на блокировки.
- **Аудит** доверяет `ICurrentUserProvider`. В веб-приложении реализуйте его поверх `IHttpContextAccessor` и регистрируйте интерсептор как Scoped. `WithIdentityResolution(IdentityResolutionMode.Overwrite)` предпочтительнее `bool` overload (избегайте boolean trap). `CreatedAtUtc` не затирается при импорте, если уже заполнено; `UpdatedAtUtc` — `DateTimeOffset?`.
- Interceptor'ы выполняются синхронно в горячем пути запросов — избегайте тяжёлой работы.

## Структура решения

```
src/EfCore.Interceptors/         # библиотека (net10.0)
  Abstractions/                  # контракты сущностей, маркеры, шифрование
  Entities/                      # ChangeLogEntry, OutboxMessage
  Saving/ Commands/ Connections/ # интерсепторы по типам
  Transactions/ Queries/
  Materialization/ Tracking/
  Observability/                 # метрики и watchdog'и
samples/SampleApp/               # демо всех сценариев (SQLite): dotnet run --project samples/SampleApp
tests/EfCore.Interceptors.Tests/ # xUnit: 70 тестов
```

## Проверка

```bash
dotnet build EfCoreInterceptors.sln
dotnet test  tests/EfCore.Interceptors.Tests
dotnet run --project samples/SampleApp     # консольный прогон всех сценариев
```
