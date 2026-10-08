namespace GeoJsonViewer.Models;

/// <summary>
/// 一个地理坐标点。
///
/// 为什么经度在前、纬度在后：这是 GeoJSON 规范（RFC 7946）规定的顺序，
/// 也是绝大多数 GIS 数据的顺序。反了就是最经典的那种错——地图整体镜像过去。
///
/// 为什么用 readonly record struct 而不是 class：
///   1. 点会被创建几十万次，struct 不产生 GC 压力，也不会有 null；
///   2. record 自带值相等（==）和 ToString()，调试时打印出来就能看，省事。
/// </summary>
public readonly record struct GeoPoint(double Lon, double Lat);
