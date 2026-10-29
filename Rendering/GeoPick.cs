namespace GeoJsonViewer.Rendering;

using GeoJsonViewer.Models;

/// <summary>
/// 一次空间查询的返回项：一个**部件**及其所属的对象。
/// </summary>
public sealed record GeoPartHit(GeoFeature Feature, IReadOnlyList<GeoPoint> Points);
