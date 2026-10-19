using System.Text;
using Microsoft.Data.Sqlite;

namespace Pi.Durable.Storage;

internal sealed class SqliteCommandExecutor(SqliteConnection connection, SqliteTransaction? transaction = null)
{
    public async Task ExecuteAsync(string sql, CancellationToken cancellationToken = default, params object?[] values)
    {
        await using var command = CreateCommand(sql, values);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<T?> GetAsync<T>(
        string sql,
        Func<SqliteDataReader, T> read,
        CancellationToken cancellationToken = default,
        params object?[] values)
        where T : class
    {
        await using var command = CreateCommand(sql, values);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? read(reader) : null;
    }

    public async Task<IReadOnlyList<T>> AllAsync<T>(
        string sql,
        Func<SqliteDataReader, T> read,
        CancellationToken cancellationToken = default,
        params object?[] values)
    {
        await using var command = CreateCommand(sql, values);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<T>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(read(reader));
        return result;
    }

    public async Task<long?> ScalarInt64Async(
        string sql,
        CancellationToken cancellationToken = default,
        params object?[] values)
    {
        await using var command = CreateCommand(sql, values);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result switch
        {
            long value => value,
            int value => value,
            _ => null,
        };
    }

    public async Task<string?> ScalarStringAsync(
        string sql,
        CancellationToken cancellationToken = default,
        params object?[] values)
    {
        await using var command = CreateCommand(sql, values);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result as string;
    }

    private SqliteCommand CreateCommand(string sql, IReadOnlyList<object?> values)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        var rewritten = new StringBuilder(sql.Length + values.Count * 4);
        var parameterIndex = 0;
        foreach (var character in sql)
        {
            if (character == '?')
            {
                if (parameterIndex >= values.Count)
                {
                    command.Dispose();
                    throw new ArgumentException("SQL has more placeholders than bound values", nameof(sql));
                }
                rewritten.Append("$p").Append(parameterIndex++);
            }
            else
            {
                rewritten.Append(character);
            }
        }
        if (parameterIndex != values.Count)
        {
            command.Dispose();
            throw new ArgumentException("Bound value count does not match SQL placeholders", nameof(values));
        }

        command.CommandText = rewritten.ToString();
        for (var index = 0; index < values.Count; index++)
            command.Parameters.AddWithValue($"$p{index}", values[index] ?? DBNull.Value);
        return command;
    }
}
