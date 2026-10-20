namespace GeoJsonViewer.Logic;

using System.IO;
using System.Text;
using GeoJsonViewer.Models;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Esri;
using NetTopologySuite.IO.Esri.Shapefiles.Readers;

/// <summary>.shp 几何不合规时的处理策略，与库的 GeometryBuilderMode 一一对应。</summary>
public enum ShapefileGeometryPolicy
{
    /// <summary>严格：遇到不合规几何直接抛异常。</summary>
    Strict,

    /// <summary>跳过：丢弃整条不合规记录。</summary>
    SkipInvalid,

    /// <summary>忽略：只删掉坏的那个环，同一条记录里能用的部分保留。</summary>
    IgnoreInvalid,

    /// <summary>修补：尝试重拼环，尽量保住形状（较慢）。</summary>
    FixInvalid,

    /// <summary>快速修补：只做最便宜的修补（默认）。</summary>
    QuickFixInvalid,
}

/// <summary>读取 shapefile 的选项。</summary>
/// <param name="Policy">几何不合规时的处理策略。</param>
/// <param name="EncodingName">.dbf 属性表编码（如 "GB18030"）。null 表示交给库判断：先看同名 .cpg，再看 dBASE 头里的语言驱动字节。</param>
public sealed record ShapefileReadOptions(
    ShapefileGeometryPolicy Policy = ShapefileGeometryPolicy.QuickFixInvalid,
    string? EncodingName = null);

/// <summary>
/// 读 .shp（几何）+ .dbf（属性），转成项目的 GeoLayer。
/// 解析交给 NetTopologySuite.IO.Esri，本类只负责三处模型映射：几何、属性、记录数对账。
/// </summary>
public static class ShapefileReader
{
    static ShapefileReader()
    {
        // .NET Core 默认不带非 Unicode 编码，不注册的话 Encoding.GetEncoding("GB18030") 会抛 NotSupportedException。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>读一个 shapefile：参数给 .shp 路径，同名 .dbf/.shx 由库自动查找。</summary>
    /// <exception cref="ShapefileException">文件缺件，或严格策略下遇到不合规几何。</exception>
    public static GeoLayer ReadFile(string path, ShapefileReadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        options ??= new ShapefileReadOptions();

        var libraryOptions = new ShapefileReaderOptions
        {
            GeometryBuilderMode = ToLibraryMode(options.Policy),
            Encoding = options.EncodingName is null ? null : Encoding.GetEncoding(options.EncodingName),
        };

        var features = new List<GeoFeature>();
        foreach (var record in Shapefile.ReadAllFeatures(path, libraryOptions))
        {
            // .dbf 有记录但 .shp 是空形状的条目没有可画内容，略过。
            if (record.Geometry is null || record.Geometry.IsEmpty) continue;

            var geometry = ToGeometry(record.Geometry);
            if (geometry is null) continue;   // 认不出的几何类型只跳过这一条，不影响整个文件

            features.Add(new GeoFeature
            {
                Geometry = geometry,
                Properties = ToProperties(record.Attributes),
            });
        }

        return new GeoLayer
        {
            FileName = Path.GetFileName(path),
            Features = features,
            // 库不报告丢弃的条数，用 .shx 的总记录数对账（含库自己跳过的记录）。
            SkippedCount = Math.Max(0, CountRecordsFromShx(path) - features.Count),
        };
    }

    /// <summary>自定义策略 → 库的枚举，五个值一一对应。</summary>
    private static GeometryBuilderMode ToLibraryMode(ShapefileGeometryPolicy policy) => policy switch
    {
        ShapefileGeometryPolicy.Strict => GeometryBuilderMode.Strict,
        ShapefileGeometryPolicy.SkipInvalid => GeometryBuilderMode.SkipInvalidShapes,
        ShapefileGeometryPolicy.IgnoreInvalid => GeometryBuilderMode.IgnoreInvalidShapes,
        ShapefileGeometryPolicy.FixInvalid => GeometryBuilderMode.FixInvalidShapes,
        ShapefileGeometryPolicy.QuickFixInvalid => GeometryBuilderMode.QuickFixInvalidShapes,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "未知的几何处理策略"),
    };

    /// <summary>
    /// 从 .shx 反推记录数：.shx 是 100 字节文件头 + 每记录 8 字节（偏移 + 内容长度），
    /// 所以 记录数 = (字节数 − 100) / 8。
    /// </summary>
    private static int CountRecordsFromShx(string shpPath)
    {
        var shxPath = Path.ChangeExtension(shpPath, ".shx");
        if (!File.Exists(shxPath)) return 0;

        var length = new FileInfo(shxPath).Length;
        return length < 100 ? 0 : (int)((length - 100) / 8);
    }

    /// <summary>
    /// NTS 几何 → GeoGeometry，认不出的类型返回 null。
    /// 约定：Kind 只取 Point / LineString / Polygon；多部件的每个部件进 Parts，此时 Points 等于 Parts[0]。
    /// </summary>
    private static GeoGeometry? ToGeometry(Geometry geometry)
    {
        if (geometry.IsEmpty) return null;

        switch (geometry)
        {
            case Point p:
                return new GeoGeometry
                {
                    Kind = GeometryKind.Point,
                    Points = new[] { ToPoint(p.Coordinate) },
                };

            case LineString line:
                return new GeoGeometry
                {
                    Kind = GeometryKind.LineString,
                    Points = ToPoints(line.Coordinates),
                };

            case Polygon poly:
                // 只取外环：渲染器一个面只认一个环（没有 even-odd 填充），InteriorRings 丢弃。
                return new GeoGeometry
                {
                    Kind = GeometryKind.Polygon,
                    Points = ToPoints(poly.ExteriorRing.Coordinates),
                };

            case MultiPoint mp:
                return FromParts(GeometryKind.Point,
                    mp.Geometries.Where(g => !g.IsEmpty).Select(g => ToPoints(g.Coordinates)));

            case MultiLineString mls:
                return FromParts(GeometryKind.LineString,
                    mls.Geometries.Where(g => !g.IsEmpty).Select(g => ToPoints(g.Coordinates)));

            case MultiPolygon mpy:
                return FromParts(GeometryKind.Polygon,
                    mpy.Geometries.OfType<Polygon>()
                        .Where(p => !p.IsEmpty)
                        .Select(p => ToPoints(p.ExteriorRing.Coordinates)));

            default:
                // GeometryCollection 等混合类型：跳过这一条。
                return null;
        }
    }

    /// <summary>多部件几何：部件逐个进 Parts，Points 约定为 Parts[0]。</summary>
    private static GeoGeometry FromParts(GeometryKind kind, IEnumerable<IReadOnlyList<GeoPoint>> parts)
    {
        var list = parts.ToList();
        return new GeoGeometry
        {
            Kind = kind,
            Parts = list.AsReadOnly(),
            Points = list.Count > 0 ? list[0] : Array.Empty<GeoPoint>(),
        };
    }

    /// <summary>坐标顺序固定为 (X, Y) = (经度, 纬度)，与 GeoPoint(Lon, Lat) 一致。</summary>
    private static GeoPoint ToPoint(Coordinate coordinate) => new(coordinate.X, coordinate.Y);

    private static IReadOnlyList<GeoPoint> ToPoints(IEnumerable<Coordinate> coordinates)
        => coordinates.Select(ToPoint).ToList().AsReadOnly();

    /// <summary>
    /// .dbf 属性 → 字符串字典（界面与悬停提示只认字符串）。
    /// 注意 IAttributesTable 没有 GetValue，取值用索引器 attributes[name]。
    /// 数值去掉多余小数位（避免 1E+06 之类），日期去掉时区，空值给 ""。
    /// </summary>
    private static IReadOnlyDictionary<string, string> ToProperties(IAttributesTable attributes)
    {
        var dict = new Dictionary<string, string>();
        foreach (var fieldName in attributes.GetNames())
        {
            dict[fieldName] = attributes[fieldName] switch
            {
                null => "",
                DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss"),
                double d => d.ToString("0.##########"),
                { } value => value.ToString() ?? "",
            };
        }

        return dict.AsReadOnly();
    }
}
