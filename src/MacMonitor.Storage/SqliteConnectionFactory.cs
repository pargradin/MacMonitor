using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace MacMonitor.Storage;

/// <summary>
/// Creates open <see cref="SqliteConnection"/>s with the configured database path.
/// Tilde expansion happens here so callers don't repeat themselves. Connections are
/// cheap with the default ADO.NET pool, so each repo opens-uses-disposes per call.
///
/// Every opened connection has a <c>regexp(pattern, input)</c> user function registered
/// so the SQL <c>X REGEXP Y</c> operator works for free-text findings search. The
/// function is case-insensitive and bounded to a 1-second match timeout to mitigate
/// catastrophic-backtracking patterns. Invalid patterns return false (the user-facing
/// regex validation happens before the SQL ever runs).
/// </summary>
public sealed class SqliteConnectionFactory
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private readonly StorageOptions _options;

    public SqliteConnectionFactory(IOptions<StorageOptions> options)
    {
        _options = options.Value;
    }

    public string ResolvedPath => ExpandTilde(_options.DatabasePath);

    public SqliteConnection Create()
    {
        var path = ResolvedPath;
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
        };
        return new SqliteConnection(csb.ToString());
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = Create();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        RegisterRegexp(conn);
        return conn;
    }

    private static void RegisterRegexp(SqliteConnection conn)
    {
        // SQLite's `X REGEXP Y` operator is implemented as `regexp(Y, X)`.
        // Function args are (pattern, input). Both nullable; null input → no match.
        conn.CreateFunction(
            name: "regexp",
            function: (string? pattern, string? input) =>
            {
                if (string.IsNullOrEmpty(pattern) || input is null) return false;
                try
                {
                    return Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase, RegexTimeout);
                }
                catch (ArgumentException)
                {
                    // Bad pattern at this layer means upstream validation slipped; bail safely.
                    return false;
                }
                catch (RegexMatchTimeoutException)
                {
                    return false;
                }
            },
            isDeterministic: true);
    }

    private static string ExpandTilde(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '~')
        {
            return path;
        }
        var home = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, path.TrimStart('~').TrimStart('/'));
    }
}
