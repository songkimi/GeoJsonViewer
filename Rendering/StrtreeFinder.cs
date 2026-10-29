namespace GeoJsonViewer.Rendering;

using GeoJsonViewer.Models;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using GeoJsonViewer.Logic;
using System.Diagnostics;

/// <summary>
/// 空间索引的**第一个真实实现**（第八节第 4 步）。
///
/// ✅ 已实现（2026-10-22，按部件级建索引）。实测：
///   建索引 30 ms / 1,313 部件（中国）、266 ms / 8,531 部件（ne_10m，129 万点）；
///   一次 Query 0.02~0.03 ms；候选 20/1,313（1.5%）、13/8,531（0.2%）；`step4-check` 缺 0。
///   ⚠️ 在 app 里这些候选数只会在**恒等基准**下出现：`DatumTransform` 会把多部件拍平
///   （`Logic\DatumTransform.cs:281` 丢 `Parts`），走了基准转换就退化成对象级、8,531 → 4,595。
///   对比数字要用 `_bench\step4-check`（它不转换）。
///
/// ============================ 契约（三句话） ============================
/// 1. **不能少给**。矩形真的碰到的部件，必须一个不漏。少给 = 画面缺一块，
///    而且缺的地方往往在屏幕边上，最容易漏看。
/// 2. **可以多给**。多给的代价只是白画几个图元，画面上看不出来 —— 所以先求"不漏"，
///    再求"少给"。这也是为什么验收工具只把"缺"当失败。
/// 3. **返回的必须是 `GeoLayer` 里那份 point 列表本身，不能拷贝。**
///    渲染和命中测试都是拿这份列表去投影的；拷一份既浪费，也让验收工具对不上号。
///
/// ============================ 写序（2026-10-22 已按此顺序完成） ============================
/// 每写完一小步都能跑 `_bench\step4-check` 验收，别一次写完再跑：
///   ① **对象级**：一个 feature 一个外包矩形，插树；查到对象就把它的全部部件都返回。
///      先做到这一步，check 会说"缺 0，但候选偏大"——这是对的，不是 bug。
///   ② **部件级**：一个 part 一个外包矩形。候选数会明显掉下来。
///      PROJECT.md 第 4 步要的正是"两种颗粒度的候选数对比"，把这个数字记进注释。
///   ③ 再把 `Query` 返回的容器处理一下（每次 Query 都会调用一次，别每次 new 一堆大对象）。
///
/// ============================ 要你自己拿主意的四件事 ============================
/// 下面每一条都没有标准答案，你要在注释里写清"我为什么这么选"：
///   · 树是**构造时一次建完**，还是**第一次 Query 才懒建**？前者让"打开文件"更慢、
///     "第一帧"更早；后者反过来。第 8 步要量"建索引"这个数字，你选哪个？
///   · 坐标用**平面坐标**（投影后）还是**经纬度**？提示：`Query` 的四个参数来自
///     `MainWindow.VisibleRect()`，那是投影之后的坐标；而第 7 步换投影会让全部坐标变一遍。
///   · `Parts.Count == 0`（单部件）、只有一个点的部件、空部件 —— 各算什么？
///     `LinearFinder` 里有一份"部件的定义"，两边的理解必须一样。
///   · 树的节点上除了"部件"还该存什么？只存够不够回答 `Query`？
///
/// ============================ 包里现成的入口 ============================
/// 下面这些都在已经引用的 NetTopologySuite 里，签名以你装的版本为准（用 IDE 的转到定义确认）：
///   `NetTopologySuite.Index.Strtree.STRtree<T>`
///       `new STRtree<T>()`、`Insert(Envelope, T)`、`Query(Envelope) → IList<T>`
///       （`Build()` 可以提前把树整理好；不调用也行，Query 会自己处理）
///   `NetTopologySuite.Geometries.Envelope`
///       `new Envelope(minX, maxX, minY, maxY)` ← **注意是 x 先 y 后**，这个顺序进错一次就是全空白
///       `ExpandBy(delta)`
/// 先跑 check 再上 UI：顺序错了在 UI 上表现是"什么都没画"，很容易误判成索引查漏了。
///
/// ============================ 第 5 步会接着用你这里的东西 ============================
/// 第 5 步要在这个矩形上**外扩一圈**（点半径最大 26 px、线宽最大 6 px），
/// 然后才是"真的相交吗"的判定。所以 `Query` 收的是**没外扩**的矩形，
/// 外扩这件事不要写在这个类里 —— 位置不同，责任不同。
/// </summary>
public sealed class StrtreeFinder : IFeatureFinder
{
    private readonly GeoLayer _layer;
    private readonly ProjectionSettings _settings;
    private readonly STRtree<GeoPartHit>? _strTree;

    /// <summary>
    /// 建索引花掉的时间（毫秒）。第 4 步的状态栏、第 8 步的压测都要它。
    /// 构建时造
    /// </summary>
    public double BuildMilliseconds { get; private set; } = -1;

    /// <summary>
    /// 已建好的索引里有多少个条目（对象级 = 特征数；部件级 = 部件数）。
    /// 验收工具拿它和你实际放进树的数量对一眼，防止"只插了一半"这种错。
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
