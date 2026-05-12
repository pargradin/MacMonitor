using System.Diagnostics;
using System.Text.RegularExpressions;
using MacMonitor.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Renci.SshNet;

namespace MacMonitor.Ssh;

/// <summary>
/// Renci.SshNet-backed implementation of <see cref="ISshExecutor"/> targeting the
/// local macOS sshd. Holds one persistent <see cref="SshClient"/> across a scan run;
/// callers should call <see cref="ConnectAsync"/> at the start and
/// <see cref="DisconnectAsync"/> at the end.
/// </summary>
public sealed class SshExecutor : ISshExecutor, IAsyncDisposable
{
    private static readonly Regex ParamPattern = new(@"\{(?<name>[a-zA-Z_][a-zA-Z0-9_]*)\}", RegexOptions.Compiled);

    private readonly SshOptions _options;
    private readonly IPrivateKeyProvider _keyProvider;
    private readonly ILogger<SshExecutor> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SshClient? _client;

    public SshExecutor(
        IOptions<SshOptions> options,
        IPrivateKeyProvider keyProvider,
        ILogger<SshExecutor> logger)
    {
        _options = options.Value;
        _keyProvider = keyProvider;
        _logger = logger;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is { IsConnected: true })
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_options.User))
            {
                throw new InvalidOperationException("Ssh:User must be configured (your local macOS username).");
            }

            var keySource = await _keyProvider.GetAsync(ct).ConfigureAwait(false);
            var info = new ConnectionInfo(_options.Host, _options.Port, _options.User,
                new PrivateKeyAuthenticationMethod(_options.User, keySource))
            {
                Timeout = _options.ConnectTimeout,
            };

            _client?.Dispose();
            _client = new SshClient(info);
            _logger.LogInformation("Connecting SSH to {User}@{Host}:{Port}.", _options.User, _options.Host, _options.Port);
            await Task.Run(() => _client.Connect(), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CommandResult> RunAsync(
        string commandId,
        IReadOnlyDictionary<string, string>? args,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(commandId);
        if (!CommandRegistry.Commands.TryGetValue(commandId, out var spec))
        {
            throw new InvalidOperationException(
                $"Command id '{commandId}' is not in the allow-list. Add it to CommandRegistry first.");
        }

        var rendered = Render(spec, args);
        if (_client is null || !_client.IsConnected)
        {
            await ConnectAsync(ct).ConfigureAwait(false);
        }

        var sw = Stopwatch.StartNew();
        // Wrap in `bash -lc` to get a clean shell environment regardless of the user's
        // login shell (zsh rcfiles can pollute stderr, breaking parsers downstream).
        var wrapped = $"/bin/bash -lc {ShellEscape.SingleQuote(rendered)}";

        using var cmd = _client!.CreateCommand(wrapped);
        cmd.CommandTimeout = _options.CommandTimeout;
        // SSH.NET's SshCommand.Execute() is synchronous; CommandTimeout is the actual
        // safety net against runaway commands. Task.Run keeps the executor non-blocking
        // for callers; the cancellation token here only prevents the task from being
        // scheduled if cancellation has already happened.
        var stdout = await Task.Run(() => cmd.Execute(), ct).ConfigureAwait(false);
        var stderr = cmd.Error ?? string.Empty;
        sw.Stop();

        var result = new CommandResult(
            CommandId: commandId,
            ExitStatus: cmd.ExitStatus!.Value,
            StandardOutput: stdout ?? string.Empty,
            StandardError: stderr,
            Duration: sw.Elapsed);

        if (!result.Succeeded)
        {
            _logger.LogWarning("SSH command {CommandId} exited {ExitStatus}: {Stderr}",
                commandId, result.ExitStatus, stderr.Trim());
        }
        else
        {
            _logger.LogDebug("SSH command {CommandId} ok in {Ms} ms.", commandId, sw.ElapsedMilliseconds);
        }
        return result;
    }

    public async ValueTask DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_client is { IsConnected: true })
            {
                _client.Disconnect();
            }
            _client?.Dispose();
            _client = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private static string Render(CommandRegistry.CommandSpec spec, IReadOnlyDictionary<string, string>? args)
    {
        if (spec.ParameterNames.Count == 0)
        {
            return spec.Template;
        }
        return ParamPattern.Replace(spec.Template, m =>
        {
            var name = m.Groups["name"].Value;
            if (!spec.ParameterNames.Contains(name))
            {
                throw new InvalidOperationException(
                    $"Command '{spec.Id}' references unknown parameter '{name}'.");
            }
            if (args is null || !args.TryGetValue(name, out var value))
            {
                throw new InvalidOperationException(
                    $"Command '{spec.Id}' requires parameter '{name}' but none was provided.");
            }
            return ShellEscape.SingleQuote(value);
        });
    }
}
