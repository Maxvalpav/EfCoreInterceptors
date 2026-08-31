# Аудит производительности — EfCoreInterceptors

Интерсепторы работают на горячем пути SQL-команд и `SaveChanges`.
Даже линейные накладные расходы умножаются на QPS × число интерсепторов
в цепочке. Разбор по компонентам.

## 1. Стоимость самой цепочки

EF применяет интерсепторы виртуальными вызовами, обёрнутыми в `ValueTask`.
Полный набор из README (~15 активных для типового приложения) добавляет
на каждый `SaveChanges`:
- ~15 виртуальных диспетчеризаций × N сущностей для перебора `ChangeTracker`;
- 3–5 линейных сканов `Entries()` в разных интерсепторах;
- аллокации на замыкания в fluent-конфиге.

**Оптимизация.**
- Кэшировать `Entries<T>()` один раз в токене и передавать по цепочке
  через `SaveChangesEventData.State`.
- Метод-цель для аудита: **один** проход по `ChangeTracker.Entries()`
  на `SavingChanges`, все интерсепторы читают из общего снапшота.

## 2. `ChangeTracker.DetectChanges`

Многие интерсепторы вызывают `entry.Properties`, что триггерит
`DetectChanges` (если не отключён). На `SaveChanges` он и так вызывается
один раз — но обращение к `Property("X").IsModified` до этого приводит к
двойному запуску. На 10 000 сущностей — двузначные миллисекунды.

**Оптимизация.** Работать через `ctx.ChangeTracker.Entries()` внутри
`SavingChanges` — там `DetectChanges` уже отработал.

## 3. `MetricsCommandInterceptor` — теги и кардинальность

Гистограмма `ef.command.duration` полезна только если теги маленькой
кардинальности. Если добавить `db.statement` или `TagWith`-значения —
Prometheus/Otel-коллектор захлебнётся: миллион уникальных запросов =
миллион временных рядов, GC на клиенте + падение коллектора.

**Оптимизация.**
- Теги: `db.system`, `db.operation` (SELECT/INSERT/UPDATE/DELETE),
  `db.context`, `ef.command_source`.
- Отдельный `Meter` для «медленных» — там теги пожирнее, но объёмы
  на порядок меньше.

## 4. `SqlLoggingCommandInterceptor` sampling

Sampling через `Random.Shared.NextDouble() < rate` — правильно, но:
- лог полного текста SQL + параметров при `rate = 1.0` = ~1 КБ на запрос;
- при QPS = 5 000 это 5 МБ/сек в лог → I/O становится узким местом.

**Оптимизация.**
- Всегда сэмплировать *в структурированный* лог; текстовый рендер только
  для медленных/ошибочных запросов (`always-log-errors`).
- `sampleRate` — не просто rate, а «токен-бакет» с burst-лимитом, чтобы
  не пропустить весь всплеск при равномерной выборке.

## 5. `SlowQueryCommandInterceptor`

Использует `CommandExecutedEventData.Duration` — 0 аллокаций, идеально.
Единственный риск: `Warning`-лог с полным текстом SQL на каждом медленном
запросе. При деградации БД (например, статистика устарела) все запросы
станут медленными → лог-шторм → I/O-затор → дальнейшая деградация.

**Оптимизация.** Токен-бакет и агрегация: «за последнюю минуту 3 421
запросов превысили порог».

## 6. `CachingCommandInterceptor`

Компромиссы:
- **Ключ.** `string.Concat(SQL, params)` без нормализации — каждый
  запрос строит длинную строку. Использовать `Span<byte>` +
  `XxHash3` для 64-битного ключа.
- **Хранилище.** `MemoryCache` с TTL — GC-friendly, но без ограничения по
  размеру = OOM на длинных запросах. Обязательно `SizeLimit` + оценка
  веса записи.
- **Материализация reader.** Полная буферизация 100k-строчного `SELECT`
  в память ради кэша — хуже, чем повторный запрос.

**Оптимизация.** Кэшировать только запросы с явным `TagWith("cache")`;
максимальный размер результата — конфигурируемый.

## 7. `NPlusOneDetectorCommandInterceptor`

Хранит `ConcurrentDictionary<string, int>` шаблонов SQL на экземпляр
контекста. Для веб-приложения с 10k RPS и 20 уникальными шаблонами это
десятки миллионов операций словаря/сек.

**Оптимизация.**
- Ключ — не сам SQL, а его хэш (`XxHash3`).
- Сброс словаря по времени, а не только по времени жизни контекста
  (Blazor Server держит контекст часами).

## 8. `ValidationSaveChangesInterceptor`

DataAnnotations-валидация обходит все свойства через reflection. Для
10 000 сущностей с 20 свойствами каждый `SaveChanges` — 200k reflection-
вызовов.

**Оптимизация.**
- Кэшировать список `ValidationAttribute` на тип (`ConcurrentDictionary<Type, ValidatorDelegate>`).
- Проверять только `Added`/`Modified` (`Deleted` валидировать не нужно —
  но текущий код часто проверяет всё подряд).

## 9. `AuditSaveChangesInterceptor`

Поле-цель: `entry.Property("CreatedAtUtc").IsModified = false`. Каждое
обращение к `Property(name)` — словарный поиск по имени. На 10 колонках
и 10 000 сущностей — 100 000 словарных поисков.

**Оптимизация.** Кэшировать `IProperty` через
`entityType.FindProperty(name)` **один раз на тип** и обращаться через
`entry.Property(iProperty)` — прямой доступ к `PropertyEntry`.

## 10. `DomainEventsSaveChangesInterceptor` / `OutboxSaveChangesInterceptor`

Оба сканируют `Entries<IHasDomainEvents>()` дважды: снапшот и очистка.

**Оптимизация.** Один проход, результат в `SaveChangesEventData.State` /
`ConditionalWeakTable`. Плюс `List.Capacity` под известный размер.

## 11. `ImmutableEntityGuard` / `DeleteGuard` — cheap wins

Обходят `ChangeTracker.Entries()` без фильтра, потом фильтруют. Быстрее:
`Entries<IImmutableEntity>()` (обобщённая версия использует внутренний
индекс).

## 12. Async-путь и `ConfigureAwait`

Отсутствие `ConfigureAwait(false)` в библиотечных `await` = переключение
контекста в UI/ASP.NET-приложениях (в новом ASP.NET Core уже без
`SynchronizationContext`, но библиотека продаётся и старым потребителям).

## 13. AOT-friendliness

- Reflection на публичные свойства сущностей — OK с
  `DynamicallyAccessedMembers(All)`.
- `JsonSerializer.Serialize(diff)` без `JsonSerializerContext` — падение
  в AOT. `ChangeLog`/`Outbox` должны иметь source-generated контекст.
- LINQ-выражения в query filter → работают.
- `Activator.CreateInstance` в `FactoryMethodInstantiationBindingInterceptor`
  — заменить на пользовательскую фабрику (уже делается).

## 14. Аллокации fluent-API

`UseEfInterceptors(s => s.WithA().WithB().WithC()…)` вызывается **на
каждое** построение опций (в `AddDbContext` per-scope). Каждый вызов —
десятки замыканий и `List<IInterceptor>`.

**Оптимизация.** Собрать массив интерсепторов один раз в singleton и
переиспользовать.

## 15. `LongRunningTransactionDetector` и watchdog-таймеры

Один `Timer` на все транзакции? — нужен приоритетный список,
`PriorityQueue<TxId, expiresAt>`, иначе O(N) на каждый tick.

## Микро-бенчмарки (что стоит опубликовать)

Из директории `benchmarks/` разумно вытащить и держать в README:

| Сценарий | Baseline | +Interceptors set | Δ |
|---|---:|---:|---:|
| `SaveChanges` 1 Added | X µs | Y µs | +% |
| `SaveChanges` 1000 Modified | X ms | Y ms | +% |
| `Find` warm | X µs | Y µs | +% |
| `ToListAsync` 100 rows | X µs | Y µs | +% |
| `ExecuteUpdate` 10k rows | X ms | Y ms | +% |

Цифры без базового «без интерсепторов» бессмысленны — читателю нужен
референс.

## Профиль «быстрых побед»

1. Один проход по `ChangeTracker` вместо 5 (–30% для тяжёлых `SaveChanges`).
2. `[GeneratedRegex]` вместо runtime-regex (–микросекунды × N).
3. Кэш `IProperty` в аудите (–10–20% на save для широких таблиц).
4. Массив интерсепторов один раз (–аллокации на scope).
5. `XxHash3` вместо string-ключа в кэше и N+1 (–аллокации).
