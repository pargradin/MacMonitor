using System.Globalization;
using MacMonitor.Core.Abstractions;
using MacMonitor.Core.Models;

namespace MacMonitor.Storage.Differs;

/// <summary>
/// Diff strategy for <see cref="LaunchItem"/> — persistence entries on disk.
///
/// <list type="bullet">
///   <item><b>IdentityKey</b> = path. Paths are stable.</item>
///   <item><b>ContentHash</b> = mtime + size. If a plist's mtime or size changed, malware
///     may have updated its loader; both are interesting.</item>
///   <item><b>Policy</b> = Added | Removed | Changed. Persistence is the highest-value signal.</item>
/// </list>
/// </summary>
public sealed class LaunchItemDiffer : DifferBase<LaunchItem>
{
    public override string ToolName => "list_launch_agents";

    public override DiffEmissionPolicy Policy => DiffEmissionPolicy.All;

    public override string IdentityKey(LaunchItem item) => item.Path;

    public override string ContentHash(LaunchItem item) =>
        string.Create(CultureInfo.InvariantCulture, $"{item.ModifiedAt.ToUnixTimeSeconds()}|{item.SizeBytes}");
}
