namespace ProfControl
{
    public partial class SampleProject
    {

        void GenerateCargo(List<IStation> targetStations)
        {
            var validStations = targetStations
                .Where(s => s.CargoAreas != null && s.CargoAreas.Count > 0)
                .ToList();

            if (validStations.Count == 0) return;

            API.ClearCargos(validStations.SelectMany(s => s.CargoAreas).Distinct().ToList());

            var cargoNumber = 1;
            foreach (var sta in validStations)
            {
                if (!sta.IsEmpty()) continue;

                var area = sta.CargoAreas.First();
                var cargo = area.CargoPlace("迈巴赫", 1);
                if (cargo == null) continue;

                cargo.Tag = cargoNumber.ToString("D6");
                cargoNumber++;
            }
            Tools.trace($"GenerateCargo");
            TraceCargoLocations();
        }

        // 返回指定货物类型当前存在的非空 Tag，用于编号下拉列表。
        public List<string> GetCargoTags(string cargoName)
        {
            lock (cargoBatchSync)
            {
                if (string.IsNullOrWhiteSpace(cargoName)) return new List<string>();

                return API.Cargos
                    .Where(c => c.Info?.Name == cargoName)
                    .Select(c => Convert.ToString(c.Tag))
                    .Where(tag => !string.IsNullOrWhiteSpace(tag))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(tag => tag, StringComparer.Ordinal)
                    .ToList();
            }
        }

        // cargoTag 为空时打印全部货物；传入编号时只查询指定货物。
        void TraceCargoLocations(string cargoTag = null)
        {
            var found = 0;
            foreach (var cargo in API.Cargos)
            {
                var tag = Convert.ToString(cargo.Tag);
                if (!string.IsNullOrEmpty(cargoTag) && tag != cargoTag) continue;

                found++;
                var cargoName = cargo.Info?.Name ?? "未知类型";
                var owner = API.GetOwner(cargo);

                if (owner is IStation station)
                    Tools.trace($"[货物位置] Tag={tag}，类型={cargoName}，位置=站点 {station.Mark}");
                else if (owner is IAgv agv)
                    Tools.trace($"[货物位置] Tag={tag}，类型={cargoName}，位置=AGV {agv.Name}");
                else
                    Tools.trace($"[货物位置] Tag={tag}，类型={cargoName}，位置=未知");
            }

            if (found == 0)
                Tools.trace(string.IsNullOrEmpty(cargoTag)
                    ? "[货物查询] 当前没有货物"
                    : $"[货物查询] 未找到 Tag={cargoTag} 的货物");
        }

        void ClearCargo(List<IStation> dropStations)
        {
            var validStations = dropStations
                .Where(s => s.CargoAreas != null && s.CargoAreas.Count > 0)
                .ToList();

            if (validStations.Count == 0) return;

            API.ClearCargos(validStations.SelectMany(s => s.CargoAreas).Distinct().ToList());
        }

    }
}
