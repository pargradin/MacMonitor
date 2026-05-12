using MacMonitor.Core.Abstractions;
using MacMonitor.Core.Models;

namespace MacMonitor.Storage.Differs;

/// <summary>
/// Diff strategy for <see cref="ProcessInfo"/>.
///
/// <list type="bullet">
///   <item><b>IdentityKey</b> = <c>command + "@" + user</c>. Pid is recycled and useless across snapshots.</item>
///   <item><b>ContentHash</b> = empty (we don't track within-identity changes; cpu/mem fluctuating
///     is noise, ppid drift is rare and not worth the false positives in Phase 2).</item>
///   <item><b>Policy</b> = <see cref="DiffEmissionPolicy.Added"/> only. Process exit is too noisy to alert on.</item>
/// </list>
/// </summary>
public sealed class ProcessInfoDiffer : DifferBase<ProcessInfo>
{
    public override string ToolName => "list_processes";

    public override DiffEmissionPolicy Policy => DiffEmissionPolicy.Added;

    public override string IdentityKey(ProcessInfo item) => $"{item.Command}@{item.User}";

    public override string ContentHash(ProcessInfo item) => string.Empty;
}
