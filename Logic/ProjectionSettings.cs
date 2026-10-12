namespace GeoJsonViewer.Logic;

/// <summary>
/// "这一次到底怎么投影"的完整说明：模式 + 椭球 + 分带。
///
/// 为什么把参数打包成一个类型，而不是给 Project 多传两个参数：
///   投影的调用方有三个（渲染器、命中测试、状态栏读数），它们都只是把 settings 原样往下传，
///   谁都不关心里面装了什么。参数一多，每加一个字段就得改三处签名；打包成一个值以后，
///   加字段只改这个文件和界面两处。
///   另外 record struct 自带值相等（按字段逐个比较），"这次和上次是不是同一套设置"可以直接写 == 。
///
/// 为什么椭球和中央经线要跟着设置走，而不是做成 GaussKruger 里的全局变量：
///   同一个经纬度，换椭球、换中央经线，算出来的平面坐标就是不同的数值。
///   把"用哪把尺子"和坐标数据绑在一起传递，才不会出现"渲染用一把尺子、
///   命中测试用另一把尺子"这种极难查的错。
/// </summary>
public readonly record struct ProjectionSettings(
    ProjectionMode Mode,
    Ellipsoid Ellipsoid,
    int ZoneWidthDegrees,
    double CentralMeridianDegrees)
{
    /// <summary>线性：经纬度直接当平面坐标，不需要椭球也不需要中央经线，后两个字段只是占位。</summary>
    public static ProjectionSettings Linear => new(ProjectionMode.Linear, Ellipsoid.CGCS2000, 6, 0.0);

    /// <summary>Web 墨卡托：球面公式，椭球与带号都不参与运算（这正是它"只管图好看"的原因）。</summary>
    public static ProjectionSettings WebMercator => new(ProjectionMode.WebMercator, Ellipsoid.CGCS2000, 6, 0.0);

    /// <summary>高斯-克吕格：椭球与中央经线都参与运算。</summary>
    public static ProjectionSettings GaussKruger(Ellipsoid ellipsoid, int zoneWidthDegrees, double centralMeridianDegrees)
        => new(ProjectionMode.GaussKruger, ellipsoid, zoneWidthDegrees, centralMeridianDegrees);

    /// <summary>状态栏里显示的名字。</summary>
    public string DisplayName => Mode switch
    {
        ProjectionMode.Linear => "线性",
        ProjectionMode.WebMercator => "Web 墨卡托",
        ProjectionMode.GaussKruger =>
            $"高斯-克吕格（{Ellipsoid.Name}，{ZoneWidthDegrees}° 带，中央经线 {CentralMeridianDegrees:0.####}°）",
        _ => Mode.ToString(),
    };
}
