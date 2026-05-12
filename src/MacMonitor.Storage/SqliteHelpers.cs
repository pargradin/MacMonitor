using Microsoft.Data.Sqlite;

namespace MacMonitor.Storage;

/// <summary>
/// Thin helpers around <see cref="SqliteCommand"/> to keep the repo implementations from
/// drowning in <c>command.Parameters.AddWithValue(...)</c> noise.
/// </summary>
internal static class SqliteHelpers
{
    public static async Task<int> ExecuteAsync(
        this SqliteConnection conn,
        string sql,
        SqliteTransaction? tx,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static async Task<object?> ExecuteScalarAsync(
        this SqliteConnection conn,
        string sql,
        SqliteTransaction? tx,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    public static SqliteCommand CreateCommand(
        this SqliteConnection conn,
        string sql,
        SqliteTransaction? tx,
        params (string Name, object? Value)[] parameters)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return cmd;
    }

    public static long ToUnixSeconds(this DateTimeOffset dto) => dto.ToUnixTimeSeconds();

    public static DateTimeOffset FromUnixSeconds(long s) => DateTimeOffset.FromUnixTimeSeconds(s);
}
