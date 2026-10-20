namespace GeoJsonViewer.Models;

/// <summary>支持的几何类型。第一版只有这三种，多出的一律跳过。</summary>
public enum GeometryKind { Point, LineString, Polygon }

/// <summary>
/// 一个几何。
///
/// 为什么让三种类型共用一个类、用 Kind 区分，而不是写 Point/LineString/Polygon 三个类 + 接口：
///   因为第一版它们的差别只有「有几个点」，渲染时反正都要走同一套「投影 → 连折线」的流程。
///   过早引入类型体系，会在 WPF 里多出三份几乎一样的画图代码。等真的需要各自不同的行为
///   （比如面要支持洞、线要支持多段）时，再拆也不迟。
/// </summary>
public sealed class GeoGeometry
{
    public required GeometryKind Kind { get; init; }

    /// <summary>Point：1 个。LineString：≥2 个。Polygon：外环（GeoJSON 规定首尾相同，闭合）。</summary>
    public required IReadOnlyList<GeoPoint> Points { get; init; }

    /// <summary>
    /// 多部件几何（MultiPolygon / MultiLineString / MultiPoint）的每个部件；空表示单部件。
    ///
    /// 为什么需要它：一个 MultiPolygon 是"好几块互不相连的面"（岛国就是典型）。
    /// 把这几个部件的点按顺序串成一个列表，画出来会多出几条跨部件的假边；
    /// 而如果只取第一块，其余的就凭空消失了。两种做法都会让图上出现"数据里没有的图形"。
    ///
    /// 约定：非空时 Points 等于 Parts[0]（兼容只认 Points 的旧路径），
    ///      要遍历真实存在的点请用 AllPoints()，否则第一块会被数两次。
    /// </summary>
    public IReadOnlyList<IReadOnlyList<GeoPoint>> Parts { get; init; } = Array.Empty<IReadOnlyList<GeoPoint>>();

    /// <summary>真实存在的所有点：单部件走 Points，多部件逐部件走（不会重复数 Parts[0]）。</summary>
    public IEnumerable<GeoPoint> AllPoints()
    {
        if (Parts.Count == 0)
        {
            foreach (var p in Points) yield return p;
            yield break;
        }

        foreach (var part in Parts)
            foreach (var p in part)
                yield return p;
    }
}
