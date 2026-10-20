namespace GeoJsonViewer.Logic;

using System.IO;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;

/// <summary>
/// 从 .prj（WKT）解析出的坐标系声明。shapefile 的坐标本身不带基准信息，全靠同名 .prj 说明。
/// </summary>
/// <param name="RawWkt">原始 WKT，界面的“坐标系详情”直接展示。</param>
/// <param name="IsProjected">true = PROJCS（平面坐标，单位可能是米）；false = GEOGCS（经纬度）。</param>
/// <param name="DatumName">DATUM["…"] 的名字，ESRI 常带 D_ 前缀。</param>
/// <param name="SpheroidName">SPHEROID["…"] 的名字。</param>
/// <param name="SemiMajorAxis">长半轴 a（米）。</param>
/// <param name="InverseFlattening">扁率倒数 1/f。</param>
/// <param name="HasToWgs84">WKT 是否带 TOWGS84 七参数：带 = 有官方转换，不带 = 只能按椭球形状推算。</param>
public sealed record PrjInfo(
    string RawWkt,
    bool IsProjected,
    string? DatumName,
    string? SpheroidName,
    double? SemiMajorAxis,
    double? InverseFlattening,
    bool HasToWgs84 = false);

/// <summary>基准匹配结果：FromName = true 表示按 DATUM 名字认出，false 表示只按椭球参数猜出。</summary>
public readonly record struct DatumMatch(Datum Datum, bool FromName);

/// <summary>
/// .prj（WKT）→ 是否投影坐标系、哪套基准、哪个椭球。
/// 国内 WGS84 与 CGCS2000 椭球几乎相同（a 都是 6378137，1/f 只差约 1.46e-6），
/// 所以先信 DATUM 名字，名字认不出再退到椭球参数，并告诉调用方结果是猜的。
/// </summary>
public static class PrjReader
{
    /// <summary>长半轴容差（米）：WKT 里可能写成 6378137 / 6378137.0 / 6378137.0000001。</summary>
    private const double SemiMajorTolerance = 1e-3;

    /// <summary>扁率倒数容差：必须小于 WGS84 与 CGCS2000 的差（1.46e-6），否则两者会认成一个。</summary>
    private const double InverseFlatteningTolerance = 1e-9;

    /// <summary>
    /// 读与 shpPath 同名的 .prj。没有该文件、读不了或内容认不出 → null（不抛异常：
    /// 第三方数据的 .prj 缺失/乱码很常见，不能因此让“打开地图”失败）。
    /// </summary>
    public static PrjInfo? TryRead(string shpPath)
    {
        var prjPath = Path.ChangeExtension(shpPath, ".prj");
        if (!File.Exists(prjPath)) return null;

        try
        {
            return Parse(File.ReadAllText(prjPath));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 解析 WKT。认不出 → null（调用方只想知道“知不知道”，不想处理异常）。
    /// 投影坐标系里也嵌着 GEOGCS，所以按解析结果的类型判断，不要靠文本里先出现哪个关键字。
    /// </summary>
    public static PrjInfo? Parse(string wkt)
    {
        if (string.IsNullOrWhiteSpace(wkt)) return null;

        CoordinateSystem crs;
        try
        {
            crs = new CoordinateSystemFactory().CreateFromWkt(wkt);
        }
        catch
        {
            return null;
        }

        return crs switch
        {
            ProjectedCoordinateSystem projCs => Describe(wkt, isProjected: true, projCs.HorizontalDatum),
            GeographicCoordinateSystem geoCs => Describe(wkt, isProjected: false, geoCs.HorizontalDatum),
            _ => null,
        };
    }

    private static PrjInfo? Describe(string wkt, bool isProjected, HorizontalDatum? datum)
    {
        if (datum is null) return null;

        var ellipsoid = datum.Ellipsoid;
        return new PrjInfo(
            RawWkt: wkt,
            IsProjected: isProjected,
            DatumName: datum.Name,
            SpheroidName: ellipsoid.Name,
            SemiMajorAxis: ellipsoid.SemiMajorAxis,
            InverseFlattening: ellipsoid.InverseFlattening,
            HasToWgs84: datum.Wgs84Parameters is not null);
    }

    /// <summary>
    /// 认基准：先按名字（包含匹配，因为 D_WGS_1984 / WGS_1984 / GCS_WGS_1984 等都是同一套），
    /// 名字认不出再按椭球参数猜，此时 FromName = false。两样都对不上 → null。
    /// </summary>
    public static DatumMatch? MatchDatum(PrjInfo info)
    {
        var byName = MatchDatumName(info.DatumName) ?? MatchDatumName(info.SpheroidName);
        if (byName is not null) return new DatumMatch(byName, true);

        if (info.SemiMajorAxis is not { } a || info.InverseFlattening is not { } invf) return null;

        foreach (var datum in DatumCatalog.All)
        {
            if (Math.Abs(datum.Ellipsoid.A - a) < SemiMajorTolerance
                && Math.Abs(datum.Ellipsoid.InverseFlattening - invf) < InverseFlatteningTolerance)
            {
                return new DatumMatch(datum, false);
            }
        }

        return null;
    }

    private static Datum? MatchDatumName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var upper = name.ToUpperInvariant();
        if (upper.Contains("WGS")) return DatumCatalog.Wgs84;
        if (upper.Contains("CHINA") || upper.Contains("CGCS") || upper.Contains("2000")) return DatumCatalog.Cgcs2000;
        if (upper.Contains("BEIJING") || upper.Contains("北京")) return DatumCatalog.Beijing54;
        if (upper.Contains("XIAN") || upper.Contains("西安")) return DatumCatalog.Xian80;
        return null;
    }
}
