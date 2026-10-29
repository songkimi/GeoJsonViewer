namespace GeoJsonViewer.Rendering;

using GeoJsonViewer.Logic;
using GeoJsonViewer.Models;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using System.Net.Http.Headers;
using System.Runtime;
using System.Runtime.InteropServices;

/// <summary>
/// **第二道漏斗**：候选是"包围盒碰到视野"的部件，这里回答"它真的碰到视野了吗"。
///
/// ★ 这个类**留给你写**（第八节第 5 步）。`Cull` 已经给你了，要写的只有 `Intersects` 里那三段。
///
/// ============================ 为什么第二道漏斗不会白干 ============================
/// 第 4 步的候选是**包围盒**相交。对一个横跨全国的对象，包围盒盖住整个视野是常态，
/// 但它的几何体可能只出现在视野的一个角上——剩下的部分全是白画。
/// ne_10m 里最极端的对象有 3.8 万个点，这种白画一次就是几毫秒。
///
/// ============================ 输入是什么、不是什么 ============================
/// 四个数（minX / minY / maxX / maxY）是**平面坐标**（`Projection.Project` 之后的那套），
/// 而且**外扩已经加过了**（按最大点半径 26 px 换算成平面单位）。
/// 所以这个类里**不要再算像素、不要再乘半径**——那是调用方的事，位置不同责任不同。
///
/// ============================ 三段怎么判 ============================
/// 按 `part.Feature.Geometry.Kind` 分三种，判完了返回"这个部件要不要画"：
///
///   ① Point（一个点）：点落在矩形内就画。这一种不需要别的判断。
///   ② LineString（一串点，按顺序连线）：
///      任一顶点在矩形内 → 画；
///      否则还要看**有没有线段穿过矩形**（两端都在外面、中间穿过的情况）。
///      ★ 只判顶点是不够的：一条横穿屏幕的长线，两个端点都在屏幕外。
///   ③ Polygon（一串点，闭合）：
///      边穿过矩形 → 画；
///      任一顶点在矩形内 → 画；
///      否则还要看**矩形有没有整个落在多边形里**（放得很近时，一个省可能盖满整个视野，
///      这时它的顶点一个都不在屏幕上，但它必须被画出来，否则画面就是一片空白）。
///
/// ============================ 三个容易写错的地方 ============================
/// 1. **顶点坐标没有闭合**：`Polygon` 的点列表首尾**不一定重复**（看 `GeoJsonReader` 怎么读的，
///    别假设它已经闭合）。判"边"的时候要自己把最后一点连回第一点。
/// 2. **点在多边形内**用射线法即可，注意点在边上算不算内——两种答案都能用，
///    但要在注释里写清你选了哪个（选了"不算"而边界又正好在屏幕上，视觉上是半像素的差）。
/// 3. **不要 `ToList()`、不要在循环里 new 对象**：第 5 步的收益全在"省投影"，
///    这个类自己不投影是对的，但多分配一次就把省下的又赔进去了。
///
/// ============================ 验收 ============================
/// `dotnet run --project _bench\step5-check`，两个判据：
///   · **不缺**：投影后任意一个顶点落在视野（含外扩）里的部件，一个都不许被剔掉；
///   · **合成用例**：长线穿屏、大面盖屏、完全在外，三个手工构造的部件必须判对。
/// 先跑 check 再上 UI：判错了在 UI 上的表现是"边缘一块白白净净"或"整个视野空了"。
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
