using System.Globalization;
using MacMonitor.Core.Abstractions;
using MacMonitor.Core.Models;

namespace MacMonitor.Storage.Differs;

/// <summary>
/// Diff strategy for <see cref="DownloadedFile"/>.
///
/// <list type="bullet">
///   <item><b>IdentityKey</b> = path.</item>
///   <item><b>ContentHash</b> = mtime + size + hasQuarantine. The quarantine bit is included so
///     stripping it (the classic Gatekeeper bypass) shows up as a Changed event.</item>
///   <item><b>Policy</b> = Added | Changed. Removed downloads are not interesting.</item>
/// </list>
/// </summary>
public sealed class DownloadedFileDiffer : DifferBase<DownloadedFile>
{
    public override string ToolName => "recent_downloads";

    public override DiffEmissionPolicy Policy => DiffEmissionPolicy.Added | DiffEmissionPolicy.Changed;

    public override string IdentityKey(DownloadedFile item) => item.Path;

    public override string ContentHash(DownloadedFile item) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{item.ModifiedAt.ToUnixTimeSeconds()}|{item.SizeBytes}|{item.QuarantineAttribute is not null}");
}
