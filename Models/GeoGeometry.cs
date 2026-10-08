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
}
