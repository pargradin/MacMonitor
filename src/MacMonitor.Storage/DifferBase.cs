using System.Text.Json;
using System.Text.Json.Serialization;
using MacMonitor.Core.Abstractions;
using MacMonitor.Core.Models;

namespace MacMonitor.Storage;

/// <summary>
/// Shared scaffolding for typed differs. Concrete differs only need to provide
/// <see cref="ToolName"/>, <see cref="IdentityKey"/>, <see cref="ContentHash"/> and
/// <see cref="Policy"/>; this base class handles JSON deserialization of snapshot
/// payloads and the conversion from the typed <c>Diff&lt;T&gt;</c> to the runtime-typed
/// <see cref="DiffResult"/> the orchestrator consumes.
/// </summary>
public abstract class DifferBase<T> : IDiffer<T>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public abstract string ToolName { get; }
    public abstract DiffEmissionPolicy Policy { get; }
    public abstract string IdentityKey(T item);
    public abstract string ContentHash(T item);

    public DiffResult Compute(string previousJson, string currentJson)
    {
        var previous = Deserialize(previousJson);
        var current = Deserialize(currentJson);
        var typed = DiffEngine.Compute(previous, current, this);

        return new DiffResult(
            typed.Added.Select(i => new DiffItem(IdentityKey(i), i!)).ToList(),
            typed.Removed.Select(i => new DiffItem(IdentityKey(i), i!)).ToList(),
            typed.Changed.Select(c => new DiffItemChange(c.IdentityKey, c.Previous!, c.Current!)).ToList());
    }

    private static IReadOnlyList<T> Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<T>();
        }
        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, JsonOptions) ?? new List<T>();
        }
        catch (JsonException)
        {
            // A malformed snapshot is treated as empty — safer than throwing inside a scan run.
            return Array.Empty<T>();
        }
    }
}
