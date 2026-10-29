namespace GeoJsonViewer.Rendering;

using GeoJsonViewer.Models;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using GeoJsonViewer.Logic;
using System.Diagnostics;

/// <summary>
/// 空间索引的实现：按**部件**建 R 树，查询返回与矩形相交的候选。
/// </summary>
public sealed class StrtreeFinder : IFeatureFinder
{
    private readonly GeoLayer _layer;
    private readonly ProjectionSettings _settings;
    private readonly STRtree<GeoPartHit>? _strTree;

    /// <summary>
    /// 建索引花掉的时间（毫秒），状态栏与 --bench 都要它。
    /// </summary>
    public double BuildMilliseconds { get; private set; } = -1;

    /// <summary>
    /// 已建好的索引里有多少个条目（对象级 = 特征数；部件级 = 部件数）。
    /// </summary>
    public int EntryCount { get; private set; }


    public StrtreeFinder(GeoLayer layer, ProjectionSettings settings)
    {
        _layer = layer ?? throw new ArgumentNullException(nameof(layer));
        _settings = settings;   // 结构体，不能判空（它永远有值）
        _strTree = new STRtree<GeoPartHit>();
        int insertCount = 0;
        Stopwatch sw = Stopwatch.StartNew();
        foreach (var geofeature in _layer.Features)
        {
            var feature = geofeature.Geometry;
            IReadOnlyList<GeoPoint> featurePoints;
            if (feature.Parts.Count == 0)
            {
                featurePoints = feature.Points;
                var geoRect = GetGeoRect(featurePoints);
                if (geoRect is null)
                {
                    continue;
                }
                //注意，这里的包围盒用的全是投影坐标而不是显示的Lon和Lat
                var featureEnvelope = new Envelope(geoRect.Value.MinLon, geoRect.Value.MaxLon, geoRect.Value.MinLat, geoRect.Value.MaxLat);
                _strTree.Insert(featureEnvelope, new GeoPartHit(geofeature, featurePoints));
                insertCount++;
            }
            else
            {
                foreach (var part in feature.Parts)
                {
                    var geoRect = GetGeoRect(part);
                    if (geoRect is null)
                    {
                        continue;
                    }
                    //同上
                    var featureEnvelope = new Envelope(geoRect.Value.MinLon, geoRect.Value.MaxLon, geoRect.Value.MinLat, geoRect.Value.MaxLat);
                    _strTree.Insert(featureEnvelope, new GeoPartHit(geofeature, part));
                    insertCount++;
                }
            }
        }
        sw.Stop();
        BuildMilliseconds = sw.Elapsed.TotalMilliseconds;
        EntryCount = insertCount;
    }

    public GeoRect? GetGeoRect(IReadOnlyList<GeoPoint> featurePoints)
    {
        List<GeoPoint> projectPoints = new List<GeoPoint>();
        foreach (var point in featurePoints)
        {
            var projection = Projection.Project(point, _settings);
            projectPoints.Add(new GeoPoint(projection.X, projection.Y));
        }
        var geoRect = ComputeLonLatBounds(projectPoints);
        if (geoRect is null)
        {
            return null;//没有有效点就跳过
        }
        return geoRect;
    }
    public IReadOnlyList<GeoPartHit> Query(double minX, double minY, double maxX, double maxY)
    {
        if (_strTree is null)
        {
            return new List<GeoPartHit>();
        }
        var screemEnvelope = new Envelope(minX, maxX, minY, maxY);
        var candidates = _strTree.Query(screemEnvelope);
        return candidates.AsReadOnly();
    }
    /// <summary>
    /// 这里得到的范围并不是地理坐标而是投影坐标，请注意放进来的坐标单位
    /// </summary>
    /// <param name="feature"></param>
    /// <returns></returns>
    private static GeoRect? ComputeLonLatBounds(List<GeoPoint> feature)
    {
        if (feature.Count == 0) return null;

        double minLon = double.MaxValue, minLat = double.MaxValue;
        double maxLon = double.MinValue, maxLat = double.MinValue;
        var any = false;

        foreach (var f in feature)
        {
                any = true;
                if (f.Lon < minLon) minLon = f.Lon;
                if (f.Lon > maxLon) maxLon = f.Lon;
                if (f.Lat < minLat) minLat = f.Lat;
                if (f.Lat > maxLat) maxLat = f.Lat;
            
        }

        return any ? new GeoRect(minLon, minLat, maxLon, maxLat) : null;
    }
}
