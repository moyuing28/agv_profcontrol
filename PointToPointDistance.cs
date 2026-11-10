namespace ProfControl
{
    public partial class SampleProject
    {
        // 与距离导出模块使用同一个有向导航图；不使用几何 API.Dist 或改动全局距离开关。
        bool TryGetPointLoadedDistance(string fromMark, string toMark, out double distance, out string error)
        {
            distance = double.NaN;
            error = null;
            try
            {
                var from = API.GetStation(fromMark);
                var to = API.GetStation(toMark);
                var graph = MapAnalysisInfo.Instance;
                if (from == null || to == null || from.CurrentIdx < 0 || to.CurrentIdx < 0 ||
                    graph[from.CurrentIdx] != from || graph[to.CurrentIdx] != to)
                {
                    error = "导航站点索引尚未正确初始化";
                    return false;
                }
                distance = from == to ? 0 : graph.GetKnownDistance(from.CurrentIdx, to.CurrentIdx);
                if (double.IsNaN(distance) || double.IsInfinity(distance) || distance < 0 || distance == double.MaxValue)
                {
                    error = $"载货路线不可达：{fromMark} → {toMark}";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = $"载货路线查询失败：{fromMark} → {toMark}（{ex.Message}）";
                return false;
            }
        }
    }
}
