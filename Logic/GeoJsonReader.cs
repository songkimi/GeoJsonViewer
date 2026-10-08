namespace GeoJsonViewer.Logic;

using GeoJsonViewer.Models;
using System.Globalization;
using System.IO;
using System.Text.Json;
// Path 有两层歧义要挡：①WPF 的隐式 using 会带进 System.Windows.Shapes.Path（画矢量图的那个），
// ②这里显式写一行别名，既解决歧义，也顺手把这层对 WPF 的依赖摘掉——
// 解析层只认字符串和 JSON，不该认识任何界面类型。
using Path = System.IO.Path;
/// <summary>
/// GeoJSON 文本 → GeoLayer。
///
/// 这一层的设计原则只有一条：**宽容**。
///   坏 JSON / 文件不存在 → 抛 GeoJsonException（带中文说明，界面直接显示这句话）；
///   个别不支持的对象 → 跳过它并计数，其余照常显示。
///   为什么区分这两种：前者是"整个文件都用不了"，只能告诉用户；后者是"文件里有一小块我不认识"，
///   为了那一小块把整张图丢掉，用户会觉得这软件坏了。
///
/// coordinates 的嵌套层数按几何类型而不同，这是本层最容易踩的坑：
///   Point      → [lon, lat]                 一层，元素是数字
///   LineString → [[lon,lat], [lon,lat]]     两层，元素是数组
///   Polygon    → [[[lon,lat], …], …]        三层（第一版只读外环）
///   三种类型不能都丢给同一个"读一串点"的读法：Point 那样读，遍历到的元素是数字而不是数组，
///   于是点全部被丢掉、SkippedCount 多出点的个数。
///   判断当前拿到的是哪种：看 coordinates 第一个元素是 Number 还是 Array。
///
/// MultiPoint / MultiLineString / MultiPolygon 不是新的几何类型，只是同一层再套一个数组，
/// 1 个 JSON 要素因此展开成 N 个对象（见 ParseFeatures）。
/// </summary>
public static class GeoJsonReader
{

    /// <summary>解析文件。文件不存在 / JSON 坏了 / 结构不认识 → 抛 GeoJsonException（带中文说明）。</summary>
    public static GeoLayer ReadFile(string path)
    {
        if (!File.Exists(path))
            throw new GeoJsonException($"找不到文件：{path}");

        string text;
        try
        {
            // 用 ReadAllText 而不是流式：GeoJSON 是纯文本，几十 MB 以内一次性读完最简单，
            // 也避免"流读了一半才发现 JSON 坏了"这种半吊子状态。
            text = File.ReadAllText(path);

            //分析Json数据
            if(string.IsNullOrWhiteSpace(text))
                throw new GeoJsonException($"文件内容为空：{path}");
            
        }
        catch (IOException ex)
        {
            throw new GeoJsonException($"文件读不了（可能被别的程序占用）：{ex.Message}");
        }

        var layer = ReadString(text, System.IO.Path.GetFileName(path));
        return layer;
    }

    /// <summary>解析字符串（给测试用）。</summary>
    public static GeoLayer ReadString(string text, string fileName = "(内存)")
    {
        System.Text.Json.JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            // 语法坏是"整个文件都用不了"，按"宽容"那条线该抛我们自己的异常，
            // 让界面拿到一句中文，而不是把 System.Text.Json 的英文原文甩给用户。
            //
            // 为什么只转述"第几行第几列"、不把 ex.Message 一起拼进来：
            //   英文原文长这样："The JSON array contains a trailing comma at the end which is not
            //   supported in this mode. Change the reader options. LineNumber: 8 | BytePositionInLine: 2."
            //   对用户有用的只有行号和列号；剩下那句是写给调用者的（连"换个 reader options"这种
            //   我们根本不会去做的建议都夹在里面），拼进中文提示里就是一行中英夹杂的噪音。
            //   异常本身仍带着原始信息，调试时 Console 里看得到。
            var where = ex.LineNumber is { } line
                ? $"第 {line + 1} 行第 {(ex.BytePositionInLine ?? 0) + 1} 个字符附近"
                : "位置不明";
            throw new GeoJsonException($"JSON 格式不对（{where}）：这个文件不是合法、完整的 JSON。");
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            var features = new List<GeoFeature>();
            int skipped = 0;
            if (root.TryGetProperty("features", out JsonElement featuresElement) && featuresElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var feature in featuresElement.EnumerateArray())
                {
                    if (feature.ValueKind != JsonValueKind.Object)
                    {
                        skipped++;
                        continue;
                    }
                    // 一个 JSON 要素可能展开成多个对象（MultiPolygon → N 个面），所以是 AddRange。
                    // 注意 skipped 数的是 **JSON 要素**，不是展开后的对象：
                    // 一个读不出任何子几何的要素算"跳过了 1 个"。
                    var parsedFeatures = ParseFeatures(feature);
                    if (parsedFeatures.Count > 0)
                    {
                        features.AddRange(parsedFeatures);
                    }
                    else
                    {
                        skipped++;
                    }
                }
                // 这里**故意不**因为空数组抛异常。
                //   「空的 FeatureCollection」在 GeoJSON 里是**合法**文件：
                //   {"type":"FeatureCollection","features":[]} 语法没错、结构也对，
                //   只是里面恰好没有东西。它不是"解析失败"，而是"解析成功，结果为空"。
                //   界面给这两种情况的待遇也是分开的：
                //     · 空 FeatureCollection → 绘图区显示「这个文件里没有可显示的对象」
                //     · 坏 JSON              → 显示一句中文的解析错误
                //   如果在这里抛异常，界面走的就是"打开失败"那条路，用户看到的是
                //   "features 数组下没有任何内容"——把"文件是空的"说成了"文件坏了"。
                //
                //   为什么守卫应该放在 GeoBounds 而不是这里（本文件最早的写法就是在这抛）：
                //   崩溃的真实原因是「拿 0 个点去 Min/Max」。修在数据源头治不了本——
                //   换个入口（比如全是不支持的几何类型、跳过完一个不剩）照样崩。
                //   修在 GeoBounds 是把那个方法变成"对空输入也成立"，一次修好全都安全。
                //   解析层只回答"文件里有什么"，"空文件该显示什么话"是界面的决定。
            }
            else
            {
                throw new GeoJsonException($"找不到 features 数组：{fileName}");
            }            return new GeoLayer
            {
                FileName = fileName,
                Features = features,
                SkippedCount = skipped,
            };
            
        }
    }

    /// <summary>
    /// 把一个 Feature 节点解析成 **0..N 个** GeoFeature；一个也读不出来时返回空列表（由调用方计数）。
    /// 返回空列表而不是抛异常，是本文件"宽容"策略的落点。
    ///
    /// 为什么是"多个"：MultiPolygon 是"数组里装着若干个 Polygon"，
    ///   [[[lon,lat],…], [[lon,lat],…], …]     ← 贵州那 9 个市州里有 4 个是这种飞地集合
    /// 而 GeoFeature.Geometry 只有一个点表，渲染器会把一个点表当成**一条连续环**画。
    /// 硬把 19 个子多边形拼成一个点表，飞地之间就会被连出根本不存在的边界线。
    /// 所以正确的展开方式是：1 个 JSON 要素 → N 个 GeoFeature（属性共用同一份）。
    /// </summary>
    private static List<GeoFeature> ParseFeatures(JsonElement feature)
    {
        var result = new List<GeoFeature>();

        if (feature.ValueKind != JsonValueKind.Object) return result;   // 不是对象 → 跳过（宽容）

        // geometry.type 决定怎么读；不认识的类型返回空列表让调用方计数
        if (!feature.TryGetProperty("geometry", out JsonElement geometryElement)
            || geometryElement.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        if (!geometryElement.TryGetProperty("type", out JsonElement geometryTypeElement)
            || geometryTypeElement.ValueKind != JsonValueKind.String)
        {
            return result;
        }

        string geometryType = geometryTypeElement.GetString() ?? "";

        if (!geometryElement.TryGetProperty("coordinates", out JsonElement coordinatesElement))
            return result;

        // properties 只读一次：同一个要素展开出来的 N 个子几何，名字本来就是同一个
        var properties = ReadProperties(feature);

        // 每种类型的读取器都返回 0..N 个几何；Kind 由读取器当场写死。
        // 为什么不在这里再用 geometryType 反推 Kind：加了 Multi* 之后，
        // 一个要素里的子几何可能是混合的，"不是 Point/LineString 就当 Polygon"这种兜底会算错。
        List<GeoGeometry> geometries = geometryType switch
        {
            "Point" => ReadPoint(coordinatesElement),
            "LineString" => ReadLineString(coordinatesElement),
            "Polygon" => ReadPolygon(coordinatesElement),
            "MultiPoint" => ReadMultiPoint(coordinatesElement),
            "MultiLineString" => ReadMultiLineString(coordinatesElement),
            "MultiPolygon" => ReadMultiPolygon(coordinatesElement),
            _ => new List<GeoGeometry>(),   // GeometryCollection 等 → 跳过计数（它没有 coordinates，结构也不同）
        };

        foreach (var geometry in geometries)
        {
            result.Add(new GeoFeature { Geometry = geometry, Properties = properties });
        }
        return result;
    }

    /// <summary>
    /// 读 properties。
    /// properties 在 feature 这一层，**不在 geometry 里**。之前写成
    ///   geometryElement.TryGetProperty("properties", …) 恒为 false，
    ///   于是每个对象都"没有 name"、连同整个对象一起被丢掉。
    /// </summary>
    private static Dictionary<string, string> ReadProperties(JsonElement feature)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (feature.TryGetProperty("properties", out JsonElement propsElement)
            && propsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in propsElement.EnumerateObject())
            {
                // "type" 留给要素类型用，不当普通属性，避免和 DisplayName 的兜底打架
                if (string.Equals(prop.Name, "type", StringComparison.OrdinalIgnoreCase)) continue;

                properties[prop.Name] = ReadPropertyValue(prop.Value);
            }
        }
        return properties;
    }

    // ── 六个读取器：把"某一种 geometry 的 coordinates"读成 0..N 个几何 ──────────
    //   Point / LineString / Polygon 最多产出 1 个；Multi* 产出多个。
    //   产出 0 个 = 这个要素读不出来，交给调用方 skipped++。
    //   Multi* 与单数版的区别只有一处：外面多套了一层"每个子几何"的循环，
    //   循环体里调用的还是同一批帮手（TryReadPoint / AddCoordinates / TryReadRing）——
    //   它们的入参本来描述的就是"一个子几何"，所以这套代码一个字都不用改。

    private static List<GeoGeometry> ReadPoint(JsonElement coordinates)
    {
        var point = TryReadPoint(coordinates);
        if (point is null) return new List<GeoGeometry>();
        return new List<GeoGeometry>
        {
            new GeoGeometry { Kind = GeometryKind.Point, Points = new List<GeoPoint> { point.Value } },
        };
    }

    private static List<GeoGeometry> ReadLineString(JsonElement coordinates)
    {
        var points = new List<GeoPoint>();
        if (AddCoordinates(coordinates, points) < 2) return new List<GeoGeometry>();
        return new List<GeoGeometry>
        {
            new GeoGeometry { Kind = GeometryKind.LineString, Points = points },
        };
    }

    private static List<GeoGeometry> ReadPolygon(JsonElement coordinates)
    {
        var points = new List<GeoPoint>();
        if (TryReadRing(coordinates, points) == 0) return new List<GeoGeometry>();
        return new List<GeoGeometry>
        {
            new GeoGeometry { Kind = GeometryKind.Polygon, Points = points },
        };
    }

    private static List<GeoGeometry> ReadMultiPoint(JsonElement coordinates)
    {
        var result = new List<GeoGeometry>();
        if (coordinates.ValueKind != JsonValueKind.Array) return result;

        foreach (var item in coordinates.EnumerateArray())
        {
            // 某一个子几何坏掉就丢掉它自己，不牵连兄弟——这一层是"宽容"落到子几何粒度上
            var point = TryReadPoint(item);
            if (point is null) continue;
            result.Add(new GeoGeometry { Kind = GeometryKind.Point, Points = new List<GeoPoint> { point.Value } });
        }
        return result;
    }

    private static List<GeoGeometry> ReadMultiLineString(JsonElement coordinates)
    {
        var result = new List<GeoGeometry>();
        if (coordinates.ValueKind != JsonValueKind.Array) return result;

        foreach (var item in coordinates.EnumerateArray())
        {
            var points = new List<GeoPoint>();
            if (AddCoordinates(item, points) < 2) continue;
            result.Add(new GeoGeometry { Kind = GeometryKind.LineString, Points = points });
        }
        return result;
    }

    private static List<GeoGeometry> ReadMultiPolygon(JsonElement coordinates)
    {
        var result = new List<GeoGeometry>();
        if (coordinates.ValueKind != JsonValueKind.Array) return result;

        foreach (var item in coordinates.EnumerateArray())
        {
            // 每个 item 是"一个 Polygon 的 coordinates"，正是 TryReadRing 想要的入参
            var points = new List<GeoPoint>();
            if (TryReadRing(item, points) == 0) continue;
            result.Add(new GeoGeometry { Kind = GeometryKind.Polygon, Points = points });
        }
        return result;
    }

    /// <summary>
    /// 把一个 [lon, lat]（或 [lon, lat, alt]）数组读成一个点；坏值返回 null。
    ///
    /// 注意 Point 和 LineString 的 coordinates **不是同一层结构**：
    ///     Point      → [lon, lat]              ← 元素是数字
    ///     LineString → [[lon,lat], [lon,lat]]  ← 元素是数组
    ///   所以"一个点"要用这个方法，而"一串点"用下面的 AddCoordinates。
    ///   判断当前拿到的是哪种：看第一个元素是 Number 还是 Array。
    /// </summary>
    private static GeoPoint? TryReadPoint(JsonElement coordinate)
    {
        if (coordinate.ValueKind != JsonValueKind.Array) return null;

        // 宽容点一：coordinates 可能是 [lon,lat]，也可能是 [lon,lat,alt]（带高程）。
        // 我们只关心前两个，后面多出来的直接无视。
        var nums = coordinate.EnumerateArray()
                             .Where(e => e.ValueKind == JsonValueKind.Number)
                             .Select(e => e.GetDouble())
                             .ToList();

        if (nums.Count < 2) return null;                                            // 宽容点二：少一个数就当这个点坏了
        if (double.IsNaN(nums[0]) || double.IsNaN(nums[1])) return null;
        if (double.IsInfinity(nums[0]) || double.IsInfinity(nums[1])) return null;  // ∞ 会毁掉 fit

        return new GeoPoint(nums[0], nums[1]);
    }

    /// <summary>把"点的数组"里的点全部读进 list。返回成功添加的个数。</summary>
    private static int AddCoordinates(JsonElement coordinates, List<GeoPoint> list)
    {
        // 不是数组（比如 coordinates 被写成字符串 "116,39"）→ 当它坏掉，返回 0。
        // 少了这道守卫，EnumerateArray() 会抛 InvalidOperationException，一路冲到界面变成未捕获异常。
        if (coordinates.ValueKind != JsonValueKind.Array) return 0;

        var added = 0;
        foreach (var item in coordinates.EnumerateArray())
        {
            var p = TryReadPoint(item);
            if (p is null) continue;
            list.Add(p.Value);
            added++;
        }
        return added;
    }

    /// <summary>
    /// 把一个 Polygon 的 coordinates 读进 list。
    ///
    /// Polygon 比 LineString **多一层**：coordinates[0] 是外环，[1..] 是洞。
    ///   Point      → [lon, lat]                        一层
    ///   LineString → [[lon,lat], …]                    两层
    ///   Polygon    → [[[lon,lat], …], [洞], …]          三层
    /// 第一版不处理洞，只读第一个环。
    ///
    /// 自校验：合法外环至少 4 个点（三角形也得首尾重复一次把环闭死）。
    /// 少于 4 个说明数据本身有问题，返回 0 让调用方把整个要素跳过。
    /// </summary>
    private static int TryReadRing(JsonElement coordinates, List<GeoPoint> list)
    {
        if (coordinates.ValueKind != JsonValueKind.Array) return 0;

        var rings = coordinates.EnumerateArray().ToList();
        if (rings.Count == 0) return 0;

        var outer = new List<GeoPoint>();
        if (AddCoordinates(rings[0], outer) < 4) return 0;

        list.AddRange(outer);
        return outer.Count;
    }

    //GeoGeometry暂未支持内外环的区分，暂时不做处理

    /// <summary>
    /// 把一个属性值统一转成字符串。
    /// 宽容点三：GeoJSON 的 properties 值可以是字符串/数字/布尔/null，
    /// 在边界上统一成 string，内部就只用处理一种类型（见 GeoFeature 的注释）。
    /// </summary>
    private static string ReadPropertyValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetDouble().ToString(CultureInfo.InvariantCulture),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "",
        _ => value.GetRawText(),
    };
}
