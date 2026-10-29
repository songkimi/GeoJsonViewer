namespace GeoJsonViewer.Rendering;

using GeoJsonViewer.Logic;
using GeoJsonViewer.Models;

/// <summary>
/// **参照实现**：不看矩形，把全部部件的候选全给出去。
///
/// 它的价值不在快，而在"一定对"：
///   第 2 步接上它之后，画面必须和改动之前**逐像素一致**（因为它返回的是全集，
///   渲染路径和原来的 foreach 完全等价）。这一步先把"接口接线"这件事证明掉，
///   第 3 步才去证明"视野矩形算得对"，第 4 步才敢换成 STRtree。
///   如果一上来就接树，将来画面少了一块，就分不清是"框算错了"、"树查漏了"还是"渲染改坏了"。
///
/// **现状（第 4 步起）**：主路径已经不是它了 —— 窗口里创建的是 `StrtreeFinder`。
/// 这个类留着有两个用处：① 读起来最快的一份"候选长什么样"的定义；
/// ② `_bench\step4-check --self` 拿它当"一定对"的对照组，去证明验收工具本身没写坏。
///
/// 为什么参数一律不用：故意留着、故意不用。它是一句"我知道该用、但现在还不该用"的声明，
/// 比把参数删掉更能说明这一版只是个基准位。
/// </summary>
public sealed class LinearFinder : IFeatureFinder
{
    private readonly GeoLayer _layer;
    private readonly ProjectionSettings _settings;

    public LinearFinder(GeoLayer layer, ProjectionSettings settings)
    {
        _layer = layer ?? throw new ArgumentNullException(nameof(layer));
        _settings = settings;   // 结构体，不能判空（它永远有值）
    }

    public IReadOnlyList<GeoPartHit> Query(double minX, double minY, double maxX, double maxY)
    {
        // 预分配一个接近"部件总数"的容量：这个实现注定要把所有部件都放进去，
        // 中途扩容反而白搬一遍（ne_10m 那个文件有 8531 个部件）。
        var hits = new List<GeoPartHit>(_layer.Features.Count);

        foreach (var feature in _layer.Features)
        {
            var geometry = feature.Geometry;

            // 和 SimpleRenderer 保持同一个"部件"的定义，两边不许各自理解一遍：
            // Parts 为空 = 单部件，Points 就是全部；否则逐个 part 各算一个部件。
            if (geometry.Parts.Count == 0)
            {
                if (geometry.Points.Count > 0) hits.Add(new GeoPartHit(feature, geometry.Points));
                continue;
            }

            foreach (var part in geometry.Parts)
                if (part.Count > 0) hits.Add(new GeoPartHit(feature, part));
        }

        return hits;
    }
}
