namespace GeoJsonViewer.Logic;

/// <summary>
/// 一个大地基准（datum）= 椭球 + 它到 CGCS2000 的七参数 + 一句备注。
///
/// 与 Ellipsoid 分开的原因（这是最容易混淆的一对概念）：
///   · 椭球只描述**形状**（长半轴 a、扁率 1/f）——"地面上的点在这把尺子上如何描述"；
///   · 基准还包含**地心位置与三轴指向**——"这把尺子摆在地球的哪里、朝向哪"。
///   因此"换椭球"只改变大地高与几米内的经纬度；要让地面点移动上百米，必须换基准（= 提供七参数）。
///   自检 [12] 给出了这一定量对比：只换椭球，平面只挪 2.667 米、大地高从 50 米变成 159 米。
///
/// 七参数挂在基准上、而不是由调用方传入的原因：
///   界面上的下拉框选的是"这个数据属于哪个基准"，而这句话背后必须自带参数，
///   否则「北京54」就只是个名字，转换时还得再问一句"那参数呢"——参数是基准的一部分，不应分开。
/// </summary>
public sealed record Datum(string Name, Ellipsoid Ellipsoid, DatumParameters ToCgcs2000, string Note);

/// <summary>
/// 基准清单：界面上「源基准」下拉框的四个选项。
///
/// 目标一律取 CGCS2000 的原因：本项目内部统一使用这一套（现行国家标准，也是投影椭球的默认值）。
/// 有了"都先换到 CGCS2000"这条规则，任意两个基准之间都能转换：源 → CGCS2000 → 目标，
/// 中间这一步就是两套坐标系的**共同语言**——即"两个都不是 CGCS2000"分支的处理方式。
/// </summary>
public static class DatumCatalog
{
    public static readonly Datum Cgcs2000 = new(
        "CGCS2000", Ellipsoid.CGCS2000, DatumParameters.Identity,
        "本项目内部统一的目标基准");

    public static readonly Datum Wgs84 = new(
        "WGS84", Ellipsoid.WGS84, DatumParameters.Identity,
        "与 CGCS2000 的短半轴只差 0.1 毫米：差的是厘米级的框架与板块运动，不是椭球形状");

    public static readonly Datum Beijing54 = new(
        "北京54", Ellipsoid.Beijing54, DatumParameters.Beijing54ToCgcs2000Example,
        "参数是**示例参数**；真实七参数是分区域的测绘成果，用之前必须写明来源");

    public static readonly Datum Xian80 = new(
        "西安80", Ellipsoid.Xian80, DatumParameters.Xian80ToCgcs2000Example,
        "参数是**示例参数**；真实七参数是分区域的测绘成果，用之前必须写明来源");

    /// <summary>下拉框按这个顺序显示（CGCS2000 在前，因为它是目标、也是最常打交道的那个）。</summary>
    public static readonly Datum[] All = { Cgcs2000, Wgs84, Beijing54, Xian80 };

    /// <summary>按名字找基准；找不到就回落到 CGCS2000（界面下拉框的兜底，宁可不转换也不要崩）。</summary>
    public static Datum ByName(string name) => Array.Find(All, d => d.Name == name) ?? Cgcs2000;
}
