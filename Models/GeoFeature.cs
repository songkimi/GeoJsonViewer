namespace GeoJsonViewer.Models;

/// <summary>
/// 经纬度矩形范围（不是屏幕坐标，是真实的经纬度）。
/// record struct 让它天然可比较，测试里可以直接断言相等。
/// </summary>
public readonly record struct GeoRect(double MinLon, double MinLat, double MaxLon, double MaxLat)
{
    public double Width => MaxLon - MinLon;
    public double Height => MaxLat - MinLat;
}

/// <summary>
/// 一行记录：几何 + 属性。
///
/// 为什么属性统一存成 string 字典，而不是保留原始的 JsonElement：
///   GeoJSON 的属性值可以是字符串 / 数字 / 布尔 / null，界面只需要「显示成一行字」。
///   在解析阶段就统一成 string，后面的渲染和悬停提示就不用再做类型分叉——
///   把多样性挡在边界上，内部就只有一种数据。
/// </summary>
public sealed class GeoFeature
{
    public required GeoGeometry Geometry { get; init; }

    /// <summary>原始属性键值。取值用 GetProperty("name")。</summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// 显示用的名字：优先 name，其次 title，最后回落到几何类型的中文名。
    ///
    /// 为什么在这里用 ?? 链一次算清，而不是让界面自己去判断：
    ///   "显示什么名字"是数据自己的事。放在这里，状态栏、悬停提示、以后的图例
    ///   都会拿到同一个结果，不会出现三处各写一套兜底规则、三处不一致的情况。
    /// </summary>
    public string DisplayName
        => GetProperty("name")
        ?? GetProperty("title")
        ?? Geometry.Kind switch
        {
            GeometryKind.Point => "(未命名的点)",
            GeometryKind.LineString => "(未命名的线)",
            GeometryKind.Polygon => "(未命名的面)",
            _ => "(未命名的对象)",
        };

    /// <summary>
    /// 取一个属性；不存在（或只有空白）时返回 null。
    ///
    /// 先按原样查，查不到再忽略大小写查一次：GeoJSON 的键习惯小写（name、title），
    /// 而 shapefile 的 .dbf 字段名习惯大写（NAME、ADMIN）——
    /// 同一套界面代码要能同时认这两种，靠的就是这一行兜底。
    /// </summary>
    public string? GetProperty(string key)
    {
        if (TryRead(key, out var direct)) return direct;

        foreach (var pair in Properties)
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) && TryRead(pair.Key, out var found))
                return found;

        return null;

        bool TryRead(string name, out string? value)
        {
            value = null;
            if (!Properties.TryGetValue(name, out var v)) return false;
            if (string.IsNullOrWhiteSpace(v)) return false;
            value = v;
            return true;
        }
    }
}
