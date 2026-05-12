using MacMonitor.Core.Abstractions;

namespace MacMonitor.Storage;

/// <summary>
/// Resolves an <see cref="IDiffer{T}"/> for a given tool name. Populated from DI at startup.
/// The orchestrator looks up the differ by <see cref="ITool.Name"/> after running the tool.
///
/// Skeleton — implementation is a few lines wrapping a Dictionary; the interesting bit is
/// that the differ has to be discovered without the orchestrator knowing T at compile time.
/// We expose a non-generic <see cref="IDiffer"/> view (already in Core) for that case.
/// </summary>
public sealed class DifferRegistry
{
    private readonly IReadOnlyDictionary<string, IDiffer> _byTool;

    public DifferRegistry(IEnumerable<IDiffer> differs)
    {
        _byTool = differs.ToDictionary(d => d.ToolName, d => d, StringComparer.Ordinal);
    }

    public bool TryGet(string toolName, out IDiffer differ)
    {
        return _byTool.TryGetValue(toolName, out differ!);
    }

    public IEnumerable<string> ToolNames => _byTool.Keys;
}
