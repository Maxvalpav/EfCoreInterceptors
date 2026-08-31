# Аудит безопасности — EfCoreInterceptors

Углублённый разбор атак и рисков, которые не всегда очевидны из README.
Модель угроз — многотенантный SaaS с недоверенным трафиком и внутренними
операторами с ограниченными правами.

## 1. Cross-tenant data leak (высочайший риск)

**Вектор.** `HasQueryFilter(e => e.TenantId == provider.CurrentTenantId)`
+ кэш модели EF, единственный на тип `DbContext`. Первый запрос фиксирует
значение тенанта в скомпилированной модели.

**Сценарий эксплуатации.**
1. Приложение стартует, первый запрос идёт под системным тенантом `null`.
2. EF компилирует фильтр `TenantId == null` в дерево запроса.
3. Все последующие запросы, включая пользовательские, применяют этот
   фильтр — данные обычных тенантов не видны, а «системные» строки
   (`TenantId is null`) отдаются всем.

Обратный сценарий: первый запрос — под тенантом `A`. Тенант `B` получает
данные `A` до перезапуска процесса.

**Смягчение.**
- `IModelCacheKeyFactory`, включающий `TenantId` в ключ модели.
- Либо `HasQueryFilter(e => e.TenantId == EF.Property<string>(context, "CurrentTenantId"))`
  с чтением из свойства контекста.
- Обязательный контрактный тест: параллельные скоупы двух тенантов не
  видят строк друг друга.

## 2. Cross-tenant write через `Attach`

`MultiTenancySaveChangesInterceptor` проверяет `TenantId` только у
модифицируемых сущностей. Атакующий с валидной сессией тенанта `A`
может:

1. Загрузить свою сущность.
2. Подменить `TenantId = "B"` (свойство ведь замаплено).
3. `SaveChanges` — интерсептор увидит `EntityState.Modified` и проверит
   «текущий tenant == B → нельзя», *но* если в приложении есть код,
   выполняющий `ctx.Set<Order>().Update(dto)` без перезагрузки, свойство
   может проехать проверку из-за отсутствия `OriginalValues`.

**Смягчение.** Сравнивать `TenantId` с `OriginalValues["TenantId"]` и
запрещать любое изменение колонки тенанта после создания:

```csharp
var prop = entry.Property(nameof(ITenantEntity.TenantId));
if (entry.State == EntityState.Modified &&
    !Equals(prop.OriginalValue, prop.CurrentValue))
    throw new CrossTenantAccessException("TenantId is immutable.");
```

## 3. Cross-column ciphertext swap (шифрование)

AES-GCM без AAD допускает **перестановку шифротекстов** между строками и
колонками. Атакующий с доступом к БД (backup, insider, SQL-инъекция в
соседнем сервисе) переносит `EncryptedSsn` пользователя A в строку
пользователя B — расшифровка проходит корректно, приложение отдаёт чужой
SSN как свой.

**Смягчение.**

```csharp
// AAD = имя таблицы + имя колонки + сериализованный PK
var aad = Encoding.UTF8.GetBytes($"{table}|{column}|{pk}");
aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, aad);
```

Формат шифротекста: `version(1) || nonce(12) || tag(16) || ct(...)` — без
версии ключа ротация невозможна.

## 4. Timing attack на equality-фильтры зашифрованных колонок

README предупреждает: «поиск по равенству по зашифрованным колонкам
невозможен». Практика: разработчики всё равно фильтруют в памяти —
`ToList().Where(x => x.EncryptedEmail == input)`. Сравнение обычным `==`
через `SequenceEqual` — не constant-time, даёт таймингу узнать длину и
префикс совпадения.

**Смягчение.** `CryptographicOperations.FixedTimeEquals` + документация
запрещающая equality-поиск в приложении.

## 5. Log injection через `SqlLoggingCommandInterceptor`

Параметры логируются «как есть» (при `includeParameterValues: true`).
Пользователь передаёт `name = "Иван\n2026-01-01 12:00:00 [INFO] admin
promoted"`. В централизованном логе появляется поддельная запись,
которую SIEM классифицирует как административное действие.

**Смягчение.** Сериализовать значения параметров как JSON-строку в одну
строку лога (без переносов) или структурированно (Serilog properties),
никогда не конкатенировать текстом.

## 6. PII в трейсах и метриках

`MetricsCommandInterceptor` добавляет теги. Если в теги попадает
нормализованный SQL с литералами — это высокая кардинальность **и** PII.
`ef.command.duration{query="SELECT WHERE email='alice@x'"}` уходит в
Prometheus навсегда.

**Смягчение.** Теги: `db.system`, `db.operation` (SELECT/INSERT), имя
DbContext. **Никогда** — `db.statement` с параметрами.

## 7. Regex ReDoS в ReadOnlyGuard / RawSqlUsageDetector

Регексы без флага `Compiled`/без source-generator и, что важнее, без
таймаута — на конструированной входной строке SQL могут дать
катастрофический backtracking.

**Смягчение.**

```csharp
[GeneratedRegex(@"...", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
```

## 8. Migrations из рантайма — RCE-класс угроз

`CommandSourceBlocker` по умолчанию блокирует миграции — правильно. Но
если приложение включает `Database.Migrate()` при старте (частый анти-
паттерн), атакующий с доступом к образу контейнера может подложить
свою миграцию в сборку и получить произвольное SQL-выполнение под
DB-пользователем приложения (обычно с правами `db_owner`).

**Смягчение.** Миграции — отдельный процесс/job с отдельным DB-
пользователем, у рантайма — только DML-права.

## 9. `WithForcedIsolationLevel(Serializable)` — DoS

Принудительный `Serializable` на всех транзакциях быстро приводит к
эскалации блокировок и deadlock-шторму. Атакующий, знающий этот факт,
может открывать длинные транзакции чтения (`SELECT … WITH (HOLDLOCK)`)
и парализовать сервис.

**Смягчение.** Не устанавливать глобально. Использовать selectors по тегу
`TagWith("critical-write")`.

## 10. Outbox: replay-атаки

Если хендлер outbox не идемпотентен, ретрай воркера = повторная
доставка события = двойное списание/двойная отправка письма. Атакующий
может вручную обновить `ProcessedAtUtc = NULL` (при наличии прав) и
получить повтор.

**Смягчение.**
- Идемпотентный ключ в самом событии (`EventId`) и таблица `processed_events`
  на стороне подписчика.
- Отдельные права на UPDATE outbox — только у воркера.

## 11. Second-level cache: cache poisoning через параметры

Если ключ кэша строится по `SQL + values`, а типы параметров не
включены, злоумышленник, контролирующий один параметр, может добиться
коллизии ключей (`'1'` vs `1`, `DBNull` vs `""`) и получить кэшированный
ответ на другой запрос.

**Смягчение.** Ключ = `SQL + [(name, DbType, value, size)]` в
детерминированном порядке; отдельные ключи для `DBNull` и `""`.

## 12. Session-init / `ExecuteSqlRaw` при открытии соединения

`WithSessionInit` часто используется для `SET SESSION AUTHORIZATION`,
`SET app.current_tenant`. Если строка формируется конкатенацией —
классическая SQL-инъекция уровня сессии (влияет на все запросы до
закрытия соединения).

**Смягчение.** Только параметризованные вызовы; `SET LOCAL` вместо
`SET SESSION` там, где возможно (сбрасывается на конце транзакции).

## 13. `PropertyEncryption` и рекламируемая AOT-совместимость

Reflection-путь через `EF.Property<T>` совместим с AOT, но кастомные
`IPropertyValueEncryptor` часто используют `JsonSerializer` без
`JsonSerializerContext` — на AOT падает в рантайме. Не безопасность в
классическом смысле, но обход шифрования на некоторых сущностях =
записываются в открытом виде.

## 14. Утечка секретов в логах соединения

Маскирование по подстроке `Password/Pwd/Secret/Token` не покрывает:
- `AccountKey=` (Azure Storage/CosmosDB через ADO.NET Provider);
- `Api Key=` (с пробелом);
- `Authentication=Active Directory Password` + `Password=…` — второй
  ключ маскируется, первый утекает как метаданные;
- экранированные значения `Password='a;b';User=…` — regex сожрёт
  лишнее и оставит хвост.

**Смягчение.** `DbConnectionStringBuilder`, whitelist безопасных ключей.

## 15. `ChangeLog` как канал утечки

Аудит-трейл пишет `old/new` значения всех изменённых полей. Оператор
поддержки с доступом к таблице `ChangeLogEntry` читает старые пароли,
токены, PII — включая те поля, что позже были обнулены/переохешированы.

**Смягчение.**
- `[Sensitive]` маркер, свойство пишется как `***`.
- Ретенция ChangeLog отдельная и короче, чем у бизнес-данных.
- Row-level security на таблицу лога.

## Матрица приоритетов

| # | Уязвимость | CVSS-ориентир | Приоритет |
|---|---|---|---|
| 1 | Cross-tenant leak через кэш модели | 8.6 (H) | 🔴 |
| 3 | Ciphertext swap | 7.5 (H) | 🔴 |
| 2 | Cross-tenant write через Attach | 7.1 (H) | 🟠 |
| 8 | Runtime-migrations | 7.0 (H) | 🟠 |
| 5 | Log injection | 5.4 (M) | 🟠 |
| 15 | ChangeLog как канал утечки | 5.2 (M) | 🟠 |
| 12 | SQL-injection в session-init | 6.1 (M) | 🟠 |
| 11 | Cache poisoning | 4.8 (M) | 🟡 |
| 6 | PII в метриках | 4.5 (M) | 🟡 |
| 14 | Секреты в логах | 4.2 (M) | 🟡 |
| 9 | Serializable DoS | 4.0 (M) | 🟡 |
| 7 | ReDoS | 3.5 (L) | 🟡 |
| 4 | Timing на equality | 3.1 (L) | 🟢 |
| 10 | Outbox replay | зависит от хендлера | 🟢 |
| 13 | AOT + JSON без context | доступность | 🟢 |
