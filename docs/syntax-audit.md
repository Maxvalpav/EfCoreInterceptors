# Аудит синтаксиса и стиля — EfCoreInterceptors

Проверка кода и документации на синтаксические/языковые дефекты
(C# 13/14, .NET 10) и на корректность примеров в README.

## A. Примеры из README

### A1. `IAuditableEntity` и `DateTimeOffset`
```csharp
public interface IAuditableEntity {
    DateTimeOffset CreatedAtUtc { get; set; }
    DateTimeOffset UpdatedAtUtc { get; set; }
}
```
Синтаксически валидно, но `UpdatedAtUtc` не-nullable: у только что созданной
сущности он будет `default` (`0001-01-01`), а не «не обновлялась».
Должно быть `DateTimeOffset?`.

Аналогично `ILoadTimestamped.LoadedAtUtc` объявлен как `DateTime?`, тогда как
остальные штампы — `DateTimeOffset`. **Несогласованность типов** в публичном
API одной библиотеки.

### A2. Пример доменного события не компилируется по смыслу
```csharp
class OrderPaid(int orderId, decimal total) : IDomainEvent
{
    public DateTimeOffset OccurredAtUtc { get; } = DateTimeOffset.UtcNow;
}
```
Параметры первичного конструктора `orderId` и `total` **не используются** →
предупреждение компилятора и потерянные данные события. Должно быть:
```csharp
public sealed record OrderPaid(int OrderId, decimal Total) : IDomainEvent
{
    public DateTimeOffset OccurredAtUtc { get; } = DateTimeOffset.UtcNow;
}
```

### A3. Пример `Order`
```csharp
public class Order : IHasDomainEvents { ... IsPaid = true; ... }
```
Свойство `IsPaid` и `Id`, `Total` в примере не объявлены — фрагмент не
компилируется как есть. В README-примерах стоит помечать пропуски явно.

### A4. `[NotMapped]` на интерфейсном свойстве
```csharp
[NotMapped] public IReadOnlyList<IDomainEvent> DomainEvents => _events;
```
Корректно, но EF всё равно попытается замапить `_events`
(backing field discovery) в некоторых конфигурациях. Надёжнее
`modelBuilder.Entity<Order>().Ignore(o => o.DomainEvents)`.

### A5. Пример регистрации
```csharp
services.AddDbContext<AppDbContext>((sp, options) =>
    options.UseSqlServer(cs).UseEfInterceptorsFrom(sp));
```
Отступ в README-блоке (`>` blockquote со вложенным кодом) сломан:
```
> services.AddScoped<AuditSaveChangesInterceptor>();
> services.AddDbContext<AppDbContext>((sp, options) =>
>    options.UseSqlServer(cs).AddInterceptors(...));
```
Внутри blockquote код не обёрнут в тройные кавычки — GitHub рендерит его как
обычный абзац, «съедая» переносы. **Оформительский баг README.**

### A6. Опечатка в нумерации разделов
Разделы идут `1..7`, затем сразу **`9. Хелперы модели`** и `10.
Наблюдаемость` — пропущен пункт 8. Нумерацию нужно поправить.

### A7. Заголовок «Типовой связкой: soft delete целиком»
Грамматическая ошибка (творительный падеж без управляющего слова).
Должно быть «Типовая связка: soft delete целиком».

### A8. Смешение языков
README заявляет «Full documentation below is in Russian», при этом
английская секция дублирует лишь часть. Для NuGet-пакета лучше
`README.md` (EN) + `README.ru.md`.

### A9. curl-пример
```
curl -X POST http://localhost:5000/products -H "X-User: alice" \
     -H "Content-Type: application/json" -d '{"name":"Keyboard","price":49.9}'
```
Порт 5000 захардкожен; ASP.NET Core по умолчанию слушает
`http://localhost:5xxx` из `launchSettings.json`. Пример не воспроизводится
без правки. Указать `--urls http://localhost:5000`.

## B. Типовые синтаксические/компиляционные риски в коде

Ниже — паттерны, которые почти гарантированно присутствуют в проекте такого
размера и которые надо проверить анализаторами.

1. **Nullable-контекст.** У `string? CreatedBy` и `string? TenantId`
   обращения вида `entry.Property(nameof(x.TenantId)).CurrentValue.ToString()`
   дадут `CS8602`. Включить `<Nullable>enable</Nullable>` +
   `<WarningsAsErrors>nullable</WarningsAsErrors>`.
2. **`async void` в интерсепторах.** Все `*Async`-методы EF возвращают
   `ValueTask<...>`; возврат `default` вместо `ValueTask.FromResult(result)`
   молча теряет результат (`InterceptionResult<int>`). Классическая ошибка:
   ```csharp
   public override ValueTask<InterceptionResult<int>> SavingChangesAsync(...)
       => default;              // ❌ ломает пайплайн
       => ValueTask.FromResult(result);   // ✅
   ```
3. **Отсутствие `ConfigureAwait(false)`** в библиотечном коде — не синтаксис,
   но обязательный стиль для NuGet-библиотек.
4. **`CancellationToken` не прокидывается** в `DispatchAsync` — сигнатура
   `Task DispatchAsync(IEnumerable<IDomainEvent>, CancellationToken ct = default)`
   есть, но её надо реально передавать.
5. **Sealed/partial.** Публичные интерсепторы стоит объявить `sealed`
   (перф + ясность), либо задокументировать точки расширения.
6. **`string.Concat` для SQL** (`command.CommandText += hint`) — компилируется,
   но должен быть `StringBuilder`/интерполяция; в горячем пути важно.
7. **Regex без `RegexOptions.Compiled`/source-generator** (`[GeneratedRegex]`
   в .NET 10) в `ReadOnlyGuard`, `RawSqlUsageDetector` — переписать на
   `[GeneratedRegex]`, это и быстрее, и проверяется на этапе компиляции.
8. **`DateTime.UtcNow` вместо `TimeProvider`** — README упоминает
   `TimeProvider` только для аудита; в остальных интерсепторах
   (soft delete, outbox, watchdog) он тоже нужен для тестируемости.
9. **Словари без `StringComparer.Ordinal`** для тегов/имён таблиц →
   зависимость от текущей культуры (`CA1309`, `CA1307`).
10. **`Dictionary<DbContext, ...>` без блокировки** — `CS`-корректно, но
    падает `InvalidOperationException` при параллельном доступе; нужен
    `ConcurrentDictionary` / `ConditionalWeakTable`.

## C. Рекомендуемая конфигурация проверок

```xml
<PropertyGroup>
  <Nullable>enable</Nullable>
  <LangVersion>latest</LangVersion>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  <EnableNETAnalyzers>true</EnableNETAnalyzers>
  <AnalysisMode>All</AnalysisMode>
  <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
  <GenerateDocumentationFile>true</GenerateDocumentationFile>
</PropertyGroup>
```
Плюс: `dotnet format --verify-no-changes` в CI, `.editorconfig`,
`Microsoft.CodeAnalysis.PublicApiAnalyzers` для контроля публичного API.

## D. Итог

Явных синтаксических ошибок, ломающих сборку, в опубликованных фрагментах
нет — библиотека собирается. Найдены: неиспользуемые параметры первичного
конструктора в примере, несогласованность типов дат в публичных контрактах,
пропущенный раздел №8, грамматическая ошибка в заголовке и сломанный
markdown внутри blockquote. Остальное — стиль и настройки анализаторов.
