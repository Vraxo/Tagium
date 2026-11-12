namespace Tagium.Core.Tags;

public sealed record TagGraphEdge(Guid SourceTagId, Guid TargetTagId, int SharedFileCount, bool IsHierarchy);

public sealed record TagGraphData(IReadOnlyList<Tag> Nodes, IReadOnlyList<TagGraphEdge> Edges);
