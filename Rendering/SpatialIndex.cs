namespace GeoJsonViewer.Rendering;

/// <summary>
/// 空间查询的抽象。
/// </summary>
public interface IFeatureFinder
{
    /// <summary>返回与矩形相交的候选（不保证真的相交，可以多给，不能少给）。</summary>
    IReadOnlyList<GeoPartHit> Query(double minX, double minY, double maxX, double maxY);
}
