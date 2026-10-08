namespace GeoJsonViewer.Logic;

using GeoJsonViewer.Models;

/// <summary>
/// 把球面的经纬度换算成平面数值。
///
/// 只用两种模式：
///   · Linear       —— 直接线性映射，把经纬度当成直角坐标。第一版先用它跑通，
///                     它会暴露"高纬度被压扁"的毛病（地球是球，硬摊平就是会变形）。
///   · WebMercator  —— 在线地图（OpenStreetMap / 谷歌 / 高德）用的就是它。切到它，
///                     形状比例才对得上你在地图上看到的样子。
///
/// 为什么这个类要写成 static + 纯函数：
///   纯函数 = 同样的输入永远给同样的输出、不碰任何外部状态。
///   这样它才是本项目里唯一可以脱离窗口、脱离 Canvas、脱离鼠标直接做单元测试的东西。
///   把「投影」和「映射到屏幕」拆成两个类，就是为了保住这份可测性。
/// </summary>
public static class Projection
{
    /// <summary>
    /// Web 墨卡托的纬度上限（取值 85.05112877980659 度）。
    ///
    /// 为什么有上限：公式里有 ln(tan(45° + lat/2))，lat 趋近 90° 时 tan 趋近 ∞，
    /// 结果是 ±∞，再往下算就是 NaN，整张图会被一个坏点毁掉。
    /// 这个数字不是随手定的：它让投影后的地图恰好是一个正方形（±180 度 ↔ ±85.051... 度），
    /// 所以所有在线地图的瓦片都是方的。超出的纬度直接夹住。
    /// </summary>
    public const double MaxMercatorLatitude = 85.05112877980659;

    /// <summary>线性：原样返回。经纬度直接当平面坐标用（所以高纬度会看起来被压扁）。</summary>
    public static (double X, double Y) ToLinear(GeoPoint p)
    {
        return(p.Lon, p.Lat);
    }

    /// <summary>
    /// Web 墨卡托：**球面**公式（EPSG:3857 就是这么定义的，不是椭球公式）。
    ///   x = 经度（度）
    ///   y = 纬度 → 先夹到 ±85.05112878，再 ln(tan(45° + lat/2))，最后换算成度（×180/π）
    /// Clamp 必须在取 tan 之前，晚一步 tan 就会吃到 ±90°、算出 Infinity/NaN。
    /// 返回的 y 也是"度"量级，这样两种模式可以共用同一套 fit 逻辑。
    /// </summary>
    public static (double X, double Y) ToWebMercator(GeoPoint p)
    {
        var X = p.Lon;
        var latClamped = Math.Clamp(p.Lat, -MaxMercatorLatitude, MaxMercatorLatitude);
        var Yred= latClamped * Math.PI / 180;
        var Y = Math.Log(Math.Tan(Math.PI / 4 + Yred / 2)) * 180 / Math.PI;
        return (X, Y);
    }

    /// <summary>按模式分发。mode 是枚举 ProjectionMode.Linear / WebMercator。</summary>
    public static (double X, double Y) Project(GeoPoint p, ProjectionMode mode) => mode switch
    {
        ProjectionMode.Linear => ToLinear(p),
        ProjectionMode.WebMercator => ToWebMercator(p),
        // 为什么给 default 抛异常而不是默默用线性：加了新模式却忘了在这里接上，
        // 会变成"切了没反应"这种最难查的 bug。
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "未知的投影模式"),
    };
}

/// <summary>投影模式。第一版只做这两种，不做 EPSG 体系。</summary>
public enum ProjectionMode { Linear, WebMercator }
