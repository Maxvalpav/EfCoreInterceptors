using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Commands;

/// <summary>Thrown when a query exceeds its row budget (03.6).</summary>
public sealed class QueryBudgetExceededException(string message) : InvalidOperationException(message);

/// <summary>
/// Query row budget (03.6, pragmatic subset): aborts result streaming once a single
/// result set exceeds <paramref name="maxRows"/> rows — a user-facing query must never
/// weigh 50k rows. Tag web-request queries and keep reports on a separate (higher)
/// budget via the <c>tagFilter</c>. Duration budgets belong to
/// <c>WithCommandTimeout</c> / <c>WithSlowQueryWarning</c>.
/// </summary>
public class QueryBudgetCommandInterceptor(
    int maxRows,
    Func<CommandEventData, bool>? scopeFilter = null) : DbCommandInterceptor
{
    private readonly int _maxRows = maxRows <= 0
        ? throw new ArgumentOutOfRangeException(nameof(maxRows), "Row budget must be positive.")
        : maxRows;
    private readonly Func<CommandEventData, bool>? _scopeFilter = scopeFilter;

    private bool AppliesTo(CommandEventData eventData) => _scopeFilter?.Invoke(eventData) ?? true;

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        => AppliesTo(eventData) ? new BudgetDbDataReader(result, _maxRows) : base.ReaderExecuted(command, eventData, result);

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
        CancellationToken cancellationToken = default)
        => AppliesTo(eventData)
            ? new BudgetDbDataReader(result, _maxRows)
            : await base.ReaderExecutedAsync(command, eventData, result, cancellationToken).ConfigureAwait(false);
}

/// <summary>Counting decorator: throws past the row budget, delegates everything else.</summary>
public sealed class BudgetDbDataReader(DbDataReader inner, int maxRows) : DbDataReader
{
    private int _rows;

    private void Count()
    {
        _rows++;
        if (_rows > maxRows)
            throw new QueryBudgetExceededException(
                $"Query exceeded its row budget of {maxRows} rows (row {_rows}). " +
                "Add pagination (Take/Skip), narrow the projection, or raise the budget.");
    }

    public override bool Read()
    {
        var has = inner.Read();
        if (has) Count();
        return has;
    }

    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        var has = await inner.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (has) Count();
        return has;
    }

    public override int FieldCount => inner.FieldCount;
    public override bool HasRows => inner.HasRows;
    public override bool IsClosed => inner.IsClosed;
    public override int Depth => inner.Depth;
    public override int RecordsAffected => inner.RecordsAffected;
    public override void Close() => inner.Close();
    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
    public override object this[int ordinal] => inner[ordinal];
    public override object this[string name] => inner[name];
    public override string GetName(int ordinal) => inner.GetName(ordinal);
    public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
    public override int GetOrdinal(string name) => inner.GetOrdinal(name);
    public override object GetValue(int ordinal) => inner.GetValue(ordinal);
    public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
    public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
    public override int GetValues(object[] values) => inner.GetValues(values);
    public override bool NextResult() => inner.NextResult();
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => inner.NextResultAsync(cancellationToken);
    public override System.Collections.IEnumerator GetEnumerator() => inner.GetEnumerator();
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
        => inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
        => inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
    public override Stream GetStream(int ordinal) => inner.GetStream(ordinal);
    public override TextReader GetTextReader(int ordinal) => inner.GetTextReader(ordinal);
    public override DataTable? GetSchemaTable() => inner.GetSchemaTable();
    public override T GetFieldValue<T>(int ordinal) => inner.GetFieldValue<T>(ordinal);
    public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
    public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
    public override char GetChar(int ordinal) => inner.GetChar(ordinal);
    public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
    public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
    public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
    public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
    public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
    public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
    public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
    public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
    public override string GetString(int ordinal) => inner.GetString(ordinal);
    public override object GetProviderSpecificValue(int ordinal) => inner.GetProviderSpecificValue(ordinal);
    public override int GetProviderSpecificValues(object[] values) => inner.GetProviderSpecificValues(values);
}
