using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Commands;

/// <summary>
/// Read-through second-level cache for queries: identical SELECTs (same SQL + parameter values)
/// are served from an in-memory buffer without touching the database.
/// The interceptor executes the command itself into a buffer and hands EF a
/// <see cref="CachedDataReader"/> — both on misses and on hits — so the live reader semantics stay intact.
/// Entries expire after the configured TTL. Caching is skipped inside explicit transactions by
/// default to avoid dirty reads.
/// </summary>
public class CachingCommandInterceptor : DbCommandInterceptor
{
    private sealed class CacheEntry(CachedQueryResult result, DateTimeOffset expiresAtUtc)
    {
        public CachedQueryResult Result { get; } = result;
        public DateTimeOffset ExpiresAtUtc { get; set; } = expiresAtUtc;
    }

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly TimeSpan _timeToLive;
    private readonly bool _skipInsideTransactions;
    private readonly bool _invalidateOnWrites;
    private readonly int _maxEntries;

    public CachingCommandInterceptor(
        TimeSpan? timeToLive = null,
        bool skipInsideTransactions = true,
        bool invalidateOnWrites = false,
        int maxEntries = 1000)
    {
        _timeToLive = timeToLive ?? TimeSpan.FromSeconds(30);
        _skipInsideTransactions = skipInsideTransactions;
        _invalidateOnWrites = invalidateOnWrites;
        _maxEntries = Math.Max(1, maxEntries);
    }

    /// <summary>Number of currently cached query results.</summary>
    public int Count => _cache.Count;

    /// <summary>Drops every cached query result.</summary>
    public void InvalidateAll() => _cache.Clear();

    /// <summary>Drops cached results whose SQL contains the given fragment (e.g. a table name).</summary>
    public void Invalidate(string sqlFragment)
    {
        foreach (var key in _cache.Keys.Where(k => k.Contains(sqlFragment, StringComparison.OrdinalIgnoreCase)))
        {
            _cache.TryRemove(key, out _);
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        if (!Cacheable(eventData, command))
        {
            return base.ReaderExecuting(command, eventData, result);
        }

        var key = BuildKey(command);
        if (TryGet(key, out var cached))
        {
            return InterceptionResult<DbDataReader>.SuppressWithResult(new CachedDataReader(cached));
        }

        using (var reader = command.ExecuteReader())
        {
            var snapshot = Buffer(reader);
            AddOrEvict(key, snapshot);
            return InterceptionResult<DbDataReader>.SuppressWithResult(new CachedDataReader(snapshot));
        }
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (!Cacheable(eventData, command))
        {
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        var key = BuildKey(command);
        if (TryGet(key, out var cached))
        {
            return InterceptionResult<DbDataReader>.SuppressWithResult(new CachedDataReader(cached));
        }

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            var snapshot = await BufferAsync(reader, cancellationToken);
            AddOrEvict(key, snapshot);
            return InterceptionResult<DbDataReader>.SuppressWithResult(new CachedDataReader(snapshot));
        }
    }

    /// <summary>
    /// Optional write-through invalidation: when enabled, any non-query (INSERT/UPDATE/DELETE,
    /// including raw SQL) clears the cache so subsequent reads see fresh data.
    /// Covers NonQuery, Scalar and Reader writes (INSERT RETURNING).
    /// </summary>
    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        if (_invalidateOnWrites)
        {
            InvalidateAll();
        }

        return base.NonQueryExecuted(command, eventData, result);
    }

    public override async ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        if (_invalidateOnWrites)
        {
            InvalidateAll();
        }

        return await base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        if (_invalidateOnWrites && SqlWriteDetector.IsWrite(command.CommandText))
        {
            InvalidateAll();
        }

        return base.ReaderExecuted(command, eventData, result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        if (_invalidateOnWrites && SqlWriteDetector.IsWrite(command.CommandText))
        {
            InvalidateAll();
        }

        return base.ScalarExecuted(command, eventData, result);
    }

    private void AddOrEvict(string key, CachedQueryResult snapshot)
    {
        if (_cache.Count >= _maxEntries)
        {
            // Evict expired entries first
            foreach (var kvp in _cache)
            {
                if (kvp.Value.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                {
                    _cache.TryRemove(kvp.Key, out _);
                }
            }

            // Still over limit: remove oldest arbitrary entry
            if (_cache.Count >= _maxEntries)
            {
                var first = _cache.Keys.FirstOrDefault();
                if (first is not null)
                {
                    _cache.TryRemove(first, out _);
                }
            }
        }

        _cache[key] = new CacheEntry(snapshot, DateTimeOffset.UtcNow.Add(_timeToLive));
    }

    private bool Cacheable(CommandEventData eventData, DbCommand command)
        => IsSelect(command.CommandText)
           && (!_skipInsideTransactions || eventData.Context?.Database.CurrentTransaction is null)
           && command.Transaction is null;

    private static bool IsSelect(string sql)
    {
        var trimmed = sql.TrimStart();
        // Interleaved skip of line and block comments
        while (true)
        {
            if (trimmed.StartsWith("--", StringComparison.Ordinal))
            {
                var newline = trimmed.IndexOf('\n');
                if (newline < 0) return false;
                trimmed = trimmed[(newline + 1)..].TrimStart();
                continue;
            }

            if (trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                var end = trimmed.IndexOf("*/", StringComparison.Ordinal);
                if (end < 0) return false;
                trimmed = trimmed[(end + 2)..].TrimStart();
                continue;
            }

            break;
        }

        return trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryGet(string key, out CachedQueryResult result)
    {
        result = null!;

        if (!_cache.TryGetValue(key, out var entry))
        {
            return false;
        }

        if (entry.ExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            result = entry.Result;
            return true;
        }

        _cache.TryRemove(key, out _);
        return false;
    }

    internal static string BuildKey(DbCommand command)
    {
        var sb = new System.Text.StringBuilder(command.CommandText);
        foreach (var parameter in command.Parameters.OfType<DbParameter>())
        {
            var val = parameter.Value;
            string valStr;
            if (val is null or DBNull)
            {
                valStr = "NULL";
            }
            else if (val is byte[] bytes)
            {
                valStr = $"[bytes:{bytes.Length}:hash:{System.Security.Cryptography.SHA256.HashData(bytes)[0]:X2}]";
            }
            else
            {
                var s = Convert.ToString(val, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                if (s.Length > 256)
                {
                    // Use hash to prevent collisions on same prefix
                    var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s));
                    valStr = $"[len:{s.Length}:hash:{Convert.ToHexString(hash)[..8]}:{s[..64]}...]";
                }
                else
                {
                    valStr = s;
                }
            }

            sb.Append('|').Append(parameter.ParameterName).Append('=').Append(valStr);
        }

        return sb.ToString();
    }

    private static CachedQueryResult Buffer(DbDataReader source)
    {
        var names = Enumerable.Range(0, source.FieldCount).Select(source.GetName).ToArray();
        var types = Enumerable.Range(0, source.FieldCount).Select(source.GetFieldType).ToArray();
        var rows = new List<object[]>();
        while (source.Read())
        {
            var row = new object[source.FieldCount];
            source.GetValues(row);
            Normalize(row);
            rows.Add(row);
        }

        return new CachedQueryResult(names, types, rows);
    }

    private static async Task<CachedQueryResult> BufferAsync(DbDataReader source, CancellationToken ct)
    {
        var names = Enumerable.Range(0, source.FieldCount).Select(source.GetName).ToArray();
        var types = Enumerable.Range(0, source.FieldCount).Select(source.GetFieldType).ToArray();
        var rows = new List<object[]>();
        while (await source.ReadAsync(ct))
        {
            var row = new object[source.FieldCount];
            source.GetValues(row);
            Normalize(row);
            rows.Add(row);
        }

        return new CachedQueryResult(names, types, rows);
    }

    private static void Normalize(object[] row)
    {
        for (var i = 0; i < row.Length; i++)
        {
            row[i] ??= DBNull.Value;
        }
    }
}

/// <summary>Buffered snapshot of a query result set (columns + rows + types) stored in the cache.</summary>
public sealed record CachedQueryResult(string[] ColumnNames, Type[] FieldTypes, List<object[]> Rows)
{
    public CachedQueryResult(string[] columnNames, List<object[]> rows)
        : this(columnNames, rows.Select(_ => typeof(object)).ToArray(), rows) { }
}

/// <summary>A repeatable DbDataReader over a buffered query result (used for every cache serve).</summary>
public sealed class CachedDataReader : DbDataReader
{
    private readonly CachedQueryResult _result;
    private int _rowIndex = -1;

    public CachedDataReader(CachedQueryResult result) => _result = result;

    private object[] Current =>
        _rowIndex >= 0 && _rowIndex < _result.Rows.Count
            ? _result.Rows[_rowIndex]
            : throw new InvalidOperationException("No current row.");

    private bool _closed;

    public override int FieldCount => _result.ColumnNames.Length;
    public override bool HasRows => _result.Rows.Count > 0;
    public override bool IsClosed => _closed;
    public override int Depth => 0;
    public override int RecordsAffected => -1;

    public override void Close() => _closed = true;

    protected override void Dispose(bool disposing)
    {
        _closed = true;
        base.Dispose(disposing);
    }

    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read() => ++_rowIndex < _result.Rows.Count;

    public override string GetName(int ordinal) => _result.ColumnNames[ordinal];

    public override string GetDataTypeName(int ordinal) => _result.FieldTypes[ordinal].Name;

    public override int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < count; i++)
        {
            values[i] = GetValue(i);
        }

        return count;
    }

    public override int GetOrdinal(string name)
    {
        for (var i = 0; i < _result.ColumnNames.Length; i++)
        {
            if (string.Equals(_result.ColumnNames[i], name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        for (var i = 0; i < _result.ColumnNames.Length; i++)
        {
            if (string.Equals(_result.ColumnNames[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(name), $"Column '{name}' not found.");
    }

    public override object GetValue(int ordinal) => Current[ordinal];
    public override bool IsDBNull(int ordinal) => Current[ordinal] is DBNull;
    public override Type GetFieldType(int ordinal) => _result.FieldTypes[ordinal];

    public override T GetFieldValue<T>(int ordinal) => (T)ConvertValue(GetValue(ordinal), typeof(T))!;
    public override bool GetBoolean(int ordinal) => (bool)ConvertValue(GetValue(ordinal), typeof(bool))!;
    public override byte GetByte(int ordinal) => (byte)ConvertValue(GetValue(ordinal), typeof(byte))!;
    public override char GetChar(int ordinal) => (char)ConvertValue(GetValue(ordinal), typeof(char))!;
    public override DateTime GetDateTime(int ordinal) => (DateTime)ConvertValue(GetValue(ordinal), typeof(DateTime))!;
    public override decimal GetDecimal(int ordinal) => (decimal)ConvertValue(GetValue(ordinal), typeof(decimal))!;
    public override double GetDouble(int ordinal) => (double)ConvertValue(GetValue(ordinal), typeof(double))!;
    public override float GetFloat(int ordinal) => (float)ConvertValue(GetValue(ordinal), typeof(float))!;
    public override Guid GetGuid(int ordinal) => (Guid)ConvertValue(GetValue(ordinal), typeof(Guid))!;
    public override short GetInt16(int ordinal) => (short)ConvertValue(GetValue(ordinal), typeof(short))!;
    public override int GetInt32(int ordinal) => (int)ConvertValue(GetValue(ordinal), typeof(int))!;
    public override long GetInt64(int ordinal) => (long)ConvertValue(GetValue(ordinal), typeof(long))!;
    public override string GetString(int ordinal) => (string)ConvertValue(GetValue(ordinal), typeof(string))!;

    public override System.Collections.IEnumerator GetEnumerator() => _result.Rows.GetEnumerator();
    public override bool NextResult() => false;

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var value = GetValue(ordinal);
        if (value is DBNull) return 0;
        var bytes = value as byte[] ?? (value is string s ? System.Text.Encoding.UTF8.GetBytes(s) : throw new InvalidCastException($"Column {ordinal} is {value.GetType().Name}, not bytes."));
        if (buffer is null) return bytes.Length;
        var available = bytes.Length - (int)dataOffset;
        if (available <= 0) return 0;
        var toCopy = Math.Min(available, length);
        Array.Copy(bytes, (int)dataOffset, buffer, bufferOffset, toCopy);
        return toCopy;
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var value = GetValue(ordinal);
        if (value is DBNull) return 0;
        var str = value as string ?? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        if (buffer is null) return str.Length;
        var available = str.Length - (int)dataOffset;
        if (available <= 0) return 0;
        var toCopy = Math.Min(available, length);
        str.CopyTo((int)dataOffset, buffer, bufferOffset, toCopy);
        return toCopy;
    }

    public override DataTable? GetSchemaTable()
    {
        var table = new DataTable();
        for (var i = 0; i < _result.ColumnNames.Length; i++)
        {
            table.Columns.Add(_result.ColumnNames[i], _result.FieldTypes[i]);
        }

        return table;
    }
    public override Stream GetStream(int ordinal)
    {
        var value = GetValue(ordinal);
        if (value is DBNull) return Stream.Null;
        if (value is byte[] bytes) return new MemoryStream(bytes, writable: false);
        if (value is Stream s) return s;
        throw new InvalidCastException($"Column {ordinal} is {value.GetType().Name}, not Stream/byte[].");
    }
    public override TextReader GetTextReader(int ordinal) => new StringReader(IsDBNull(ordinal) ? string.Empty : GetString(ordinal));

    private static object? ConvertValue(object? value, Type targetType)
        => value switch
        {
            null or DBNull => null,
            { } when targetType.IsInstanceOfType(value) => value,
            _ => Convert.ChangeType(value, Nullable.GetUnderlyingType(targetType) ?? targetType,
                    System.Globalization.CultureInfo.InvariantCulture)
        };
}
