namespace ProfControl
{
    public partial class SampleProject
    {
        /// <summary>
        /// 使用已校验的巷道配置，实时查询货物状态。
        /// hasCargo=false 返回每条巷道最深的空货位，不代表可立即派车。
        /// </summary>
        public List<SelectableStationInfo> GetSelectableStations(bool hasCargo)
        {
            var result = new List<SelectableStationInfo>();
            foreach (var pocketName in 巷道子分类名称)
            {
                if (!TryResolveCargoPocket(pocketName, out var key, out _)) continue;
                var stations = GetCargoPocket(key).Slots;
                if (hasCargo)
                {
                    foreach (var station in stations.Where(s => !s.IsEmpty()))
                        result.Add(new SelectableStationInfo
                        {
                            StationMark = station.Mark,
                            PocketName = pocketName,
                            HasCargo = true
                        });
                }
                else
                {
                    var station = stations.FirstOrDefault(s => s.IsEmpty());
                    if (station != null)
                        result.Add(new SelectableStationInfo
                        {
                            StationMark = station.Mark,
                            PocketName = pocketName,
                            HasCargo = false
                        });
                }
            }
            return result;
        }
    }
}