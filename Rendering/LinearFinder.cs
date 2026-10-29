namespace GeoJsonViewer.Rendering;

using GeoJsonViewer.Logic;
using GeoJsonViewer.Models;

/// <summary>
/// **参照实现**：不看矩形，把全部部件的候选全给出去。
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
