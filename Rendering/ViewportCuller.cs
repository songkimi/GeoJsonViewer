namespace GeoJsonViewer.Rendering;

using GeoJsonViewer.Logic;
using GeoJsonViewer.Models;
using NetTopologySuite.Geometries;

/// <summary>
/// **第二道漏斗**：候选是"包围盒碰到视野"的部件，这里回答"它真的碰到视野了吗"。
/// </summary>
public static class ViewportCuller
{
    /// <summary>
    /// 把一个部件投影后，判断它是否真的碰到矩形（矩形已含外扩）。
    /// 碰到 → true（要画）；完全不碰 → false（可以剔掉）。
    /// 该方法应该依赖已经有标准的NTS的Intersects方法进行辅助
    /// </summary>
    public static bool Intersects(
        GeoPartHit part,
        ProjectionSettings settings,
        double minX,
        double minY,
        double maxX,
        double maxY)
    {
        //此方法依旧依赖投影坐标进行计算，请注意甄别
        var scrEnv = new List<GeoPoint>() { new GeoPoint(minX, minY), new GeoPoint(minX, maxY), new GeoPoint(maxX, maxY), new GeoPoint(maxX, minY) }.AsReadOnly();
        var projectionPoints = new List<GeoPoint>();
        foreach (var point in part.Points)
        {
            var temppoint = Projection.Project(point, settings);
            projectionPoints.Add(new GeoPoint(temppoint.X,temppoint.Y));
        }
        var scrEnvCoords = CloseRing(scrEnv.Select(p => new Coordinate(p.Lon, p.Lat)).ToArray());
        var ring = new LinearRing(scrEnvCoords);
        Geometry box = new Polygon(ring);
        var featureCoords = projectionPoints.Select(p => new Coordinate(p.Lon, p.Lat)).ToArray();
        Geometry NTSfrature;
        //点线面判断
        if (part.Feature.Geometry.Kind == GeometryKind.Point)
        {
            Point ntspoint = new Point(featureCoords[0]);
            NTSfrature = ntspoint;
        }
        else if (part.Feature.Geometry.Kind == GeometryKind.LineString)
        {
            LineString ntsline = new LineString(featureCoords);
            NTSfrature = ntsline;
        }
        else if (part.Feature.Geometry.Kind == GeometryKind.Polygon)
        {
            // 必须用 Polygon（而不是 LinearRing）：Ring 只是"边界"，Boundary.Intersects(box)
            // 在"视野整个落在面内部"时是 false —— 那正是③要处理的情况。
            Geometry ntsPol = new Polygon(new LinearRing(CloseRing(featureCoords)));
            NTSfrature = ntsPol;
        }
        else
        {
            return false;
        }
        return NTSfrature.Intersects(box);
    }

    /// <summary>把顶点序列补成首尾闭合（输入可能已经闭合，那就原样返回）。</summary>
    private static Coordinate[] CloseRing(Coordinate[] coords)
    {
        if (coords.Length == 0) return coords;
        var first = coords[0];
        var last = coords[^1];
        if (first.X == last.X && first.Y == last.Y) return coords;
        var closed = new Coordinate[coords.Length + 1];
        Array.Copy(coords, closed, coords.Length);
        closed[^1] = new Coordinate(first.X, first.Y);
        return closed;
    }

    /// <summary>
    /// 第二道漏斗的批处理：逐个问 <see cref="Intersects"/>，过了的留下。
    ///
    /// 这个方法是**给你的**（不用改）：它只做一件事——把不要的摘掉，顺序保持不变。
    /// 为什么保持顺序：渲染顺序决定了"谁盖住谁"，候选的顺序一变，重叠区域的画面就可能变。
    /// 没有候选时返回空数组（不是 null），调用方不用判空。
    /// </summary>
    public static IReadOnlyList<GeoPartHit> Cull(
        IReadOnlyList<GeoPartHit> candidates,
        ProjectionSettings settings,
        double minX,
        double minY,
        double maxX,
        double maxY)
    {
        if (candidates.Count == 0) return Array.Empty<GeoPartHit>();

        var kept = new List<GeoPartHit>(candidates.Count);
        foreach (var part in candidates)
        {
            if (Intersects(part, settings, minX, minY, maxX, maxY)) kept.Add(part);
        }
        return kept;
    }
}
