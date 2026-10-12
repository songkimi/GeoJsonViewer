namespace GeoJsonViewer.Logic;

using GeoJsonViewer.Models;

/// <summary>
/// 把球面的经纬度换算成平面数值。
///
/// 有三种模式：
///   · Linear       —— 直接线性映射，把经纬度当成直角坐标。第一版先用它跑通，
///                     它会暴露"高纬度被压扁"的毛病（地球是球，硬摊平就是会变形）。
///   · WebMercator  —— 在线地图（OpenStreetMap / 谷歌 / 高德）用的就是它。切到它，
///                     形状比例才对得上你在地图上看到的样子。
///   · GaussKruger  —— 椭球面上的等角横轴切圆柱投影，中国的大比例尺地形图都用它。
///                     它必须分带，所以比前两种多两个参数（见 ProjectionSettings）。
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

    /// <summary>
    /// 高斯-克吕格：把椭球面上的经纬度投到平面上，再用它接住屏幕流水线。
    ///
    /// ★ 这里唯一容易错、也最要紧的一步是**换轴**。
    ///   GaussKruger.Forward 返回的是测绘坐标：X = 北（Northing）、Y = 东（Easting，含 500 公里东偏）。
    ///   而本项目的屏幕流水线要的是 (水平, 垂直)：X 往右、Y 往下。
    ///   所以本方法的返回值必须是 (东, 北) —— 把 Forward 的两个数调个位置。
    ///
    ///   忘了换的症状：整张图被"转置"，东西方向被当成南北方向画（图会歪着倒下来）。
    ///   它还特别隐蔽：单独看数字都合理，只有画出来或者拿"两点只差纬度"去比才知道错了。
    ///
    /// 另外，500 公里东偏在这里**不用减掉**：它只是个常数平移，fit 会因为居中把它整块吃掉。
    /// 反过来，反算读数时必须把它带着（GaussKruger.Inverse 内部会减），否则经度会偏。
    /// </summary>
    public static (double X, double Y) ToGaussKruger(GeoPoint p, ProjectionSettings settings)
    {
        var (northing, easting) = GaussKruger.Forward(p, settings.Ellipsoid, settings.CentralMeridianDegrees);
        return (easting, northing);
    }

    /// <summary>按模式分发。settings 里既有模式，也有高斯-克吕格要用的椭球与中央经线。</summary>
    public static (double X, double Y) Project(GeoPoint p, ProjectionSettings settings) => settings.Mode switch
    {
        ProjectionMode.Linear => ToLinear(p),
        ProjectionMode.WebMercator => ToWebMercator(p),
        ProjectionMode.GaussKruger => ToGaussKruger(p, settings),
        // 为什么给 default 抛异常而不是默默用线性：加了新模式却忘了在这里接上，
        // 会变成"切了没反应"这种最难查的 bug。
        _ => throw new ArgumentOutOfRangeException(nameof(settings), settings.Mode, "未知的投影模式"),
    };
    /// <summary>
    /// 根据传入的参数构建投影设置。
    /// </summary>
    /// <param name="EllipsoidMode"></param>
    /// <param name="ZoneWidthDegreesMode"></param>
    /// <param name="CentralMeridianDegrees"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public static ProjectionSettings BuildSettings(int projectionMode, int ellipsoidMode, int zoneWidthDegreesMode, GeoRect geoRect)
    {
        if (projectionMode == 0)
        {
            return ProjectionSettings.Linear;
        }
        else if(projectionMode == 1)
        {
            return ProjectionSettings.WebMercator;
        }
        else if (projectionMode == 2)
        {
            int zoneWidthDegrees = zoneWidthDegreesMode switch
            {
                0 => 3,
                1 => 6,
                _ => throw new ArgumentOutOfRangeException(nameof(zoneWidthDegreesMode), "不合理的分带宽度选择")
            };
            Ellipsoid ellipsoid = ellipsoidMode switch
            {
                0 => Ellipsoid.CGCS2000,
                1 => Ellipsoid.WGS84,
                2 => Ellipsoid.Beijing54,
                3 => Ellipsoid.Xian80,
                _ => throw new ArgumentOutOfRangeException(nameof(ellipsoidMode), "不合理的椭球选择")
            };
            var midLon = (geoRect.MinLon + geoRect.MaxLon) / 2.0;
            var centralMeridian = GaussKruger.CentralMeridian(midLon, zoneWidthDegrees);
            return new ProjectionSettings(ProjectionMode.GaussKruger, ellipsoid, zoneWidthDegrees, centralMeridian);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(projectionMode), "不合理的投影模式选择");
        }
        
    }
}

/// <summary>投影模式。</summary>
public enum ProjectionMode { Linear, WebMercator, GaussKruger }
