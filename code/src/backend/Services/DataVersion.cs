using System.Collections.Concurrent;
using Bento.Api.Services.Workspaces;

namespace Bento.Api.Services;

/// <summary>
/// A counter per workspace that goes up with every change of its data or its
/// model (Step 5.8): a table made from a saved query knows from it whether it
/// is still current.
/// </summary>
public sealed class DataVersion
{
    private readonly ConcurrentDictionary<long, long> _versions = new();

    public long Current => _versions.GetValueOrDefault(WorkspaceContext.Id);

    public void Bump() => _versions.AddOrUpdate(WorkspaceContext.Id, 1, (_, v) => v + 1);
}
