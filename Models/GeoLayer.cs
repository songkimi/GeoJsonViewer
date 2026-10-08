namespace GeoJsonViewer.Models;

/// <summary>
/// 一次打开的完整结果。
///
/// 为什么把「文件名 + 对象列表 + 经纬度范围 + 跳过的对象数」打包成一个对象，
/// 而不是让 MainWindow 分别持有四个变量：
///   这四样东西是同生共死的——换文件就一起换。打包之后，界面只持有一个 GeoLayer? 字段，
///   "还没有打开文件"就是那个字段为 null，状态收敛成一个，不会出现"文件换了但范围还是旧的"。
/// </summary>
public sealed class GeoLayer
{
    public required string FileName { get; init; }
    public required IReadOnlyList<GeoFeature> Features { get; init; }

    /// <summary>
    /// 解析时跳过了多少个不支持的对象（MultiPoint、GeometryCollection、坏掉的 feature…）。
    /// 界面要据此告诉用户「跳过了 N 个不支持的对象」，所以这个数字必须跟着图层一起走。
    /// </summary>
    public int SkippedCount { get; init; }

    private GeoRect? _lonLatBounds;
    private bool _lonLatBoundsComputed;

    /// <summary>
    /// 所有对象的经纬度范围。空图层时返回 null。
    ///
    /// 为什么延迟计算（第一次访问才算）：它要遍历所有点，而「打开文件」这条路径上
    /// 不一定需要它（fit 用的是投影后的范围）。算早了就是白遍历一遍。
    /// </summary>
    public GeoRect? LonLatBounds
    {
        get
        {
            if (!_lonLatBoundsComputed)
            {
                _lonLatBounds = ComputeLonLatBounds();
                _lonLatBoundsComputed = true;
            }
            return _lonLatBounds;
        }
    }

    private GeoRect? ComputeLonLatBounds()
    {
        if (Features.Count == 0) return null;

        double minLon = double.MaxValue, minLat = double.MaxValue;
        double maxLon = double.MinValue, maxLat = double.MinValue;
        var any = false;

        foreach (var f in Features)
        {
            foreach (var p in f.Geometry.Points)
            {
                any = true;
                if (p.Lon < minLon) minLon = p.Lon;
                if (p.Lon > maxLon) maxLon = p.Lon;
                if (p.Lat < minLat) minLat = p.Lat;
                if (p.Lat > maxLat) maxLat = p.Lat;
            }
        }

        return any ? new GeoRect(minLon, minLat, maxLon, maxLat) : null;
    }

    /// <summary>把所有点按指定模式投影一遍（渲染、fit、命中测试都要用）。</summary>
    public IEnumerable<GeoPoint> AllPoints()
    {
        foreach (var f in Features)
            foreach (var p in f.Geometry.Points)
                yield return p;
    }
}
