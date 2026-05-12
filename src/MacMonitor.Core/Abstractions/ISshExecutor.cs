namespace MacMonitor.Core.Abstractions;

/// <summary>
/// Executes allow-listed commands over an SSH channel. Implementations must reject
/// any command id that is not registered, and must safely substitute parameters
/// (no shell metacharacter passthrough) into the command template.
/// </summary>
public interface ISshExecutor
{
    /// <summary>
    /// Open the underlying SSH connection. Idempotent.
    /// </summary>
    Task ConnectAsync(CancellationToken ct);

    /// <summary>
    /// Run the command registered under <paramref name="commandId"/>, optionally
    /// substituting <paramref name="args"/> into its template.
    /// </summary>
    Task<CommandResult> RunAsync(
        string commandId,
        IReadOnlyDictionary<string, string>? args,
        CancellationToken ct);

    /// <summary>
    /// Close the underlying SSH connection if open. Idempotent.
    /// </summary>
    ValueTask DisconnectAsync();
}
