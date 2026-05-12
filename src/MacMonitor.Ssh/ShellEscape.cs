using System.Text;

namespace MacMonitor.Ssh;

/// <summary>
/// Single-quote-wrap a value for safe inclusion in a POSIX shell command. The only
/// metacharacter that can break out of single-quotes is the single-quote itself,
/// which we escape via the standard <c>'\''</c> idiom.
/// </summary>
internal static class ShellEscape
{
    public static string SingleQuote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('\'');
        foreach (var c in value)
        {
            if (c == '\'')
            {
                sb.Append("'\\''");
            }
            else
            {
                sb.Append(c);
            }
        }
        sb.Append('\'');
        return sb.ToString();
    }
}
