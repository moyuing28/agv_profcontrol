namespace ProfControl
{
    public partial class SampleProject
    {
        public CargoEditResult AddCargo(string cargoName, string cargoTag, string stationMark)
        {
            lock (cargoBatchSync)
            {
                var result = new CargoEditResult { CargoName = cargoName, CargoTag = cargoTag, StationMark = stationMark };
                if (!ValidateCargoEdit(result)) return result;
                var info = API.GetCargoInfo(cargoName);
                if (info == null) return FailCargoEdit(result, "货物类型不存在，请先配置 CargoInfo");
                if (API.Cargos.Any(c => c.Info?.Name == cargoName && Convert.ToString(c.Tag) == cargoTag))
                    return FailCargoEdit(result, "该类型和编号已存在");
                var station = API.GetStation(stationMark);
                var areaName = GetStationCargoArea(station, out var error);
                if (areaName == null) return FailCargoEdit(result, error);
                var area = station.CargoAreas.FirstOrDefault(a => a.Name == areaName);
                if (area == null) return FailCargoEdit(result, "站点缺少对应朝向的货物区域");
                if (!station.IsEmpty()) return FailCargoEdit(result, "目标站点已有货物或容器");
                var cargo = area.CargoPlace(info, 1);
                if (cargo == null) return FailCargoEdit(result, "宿主放置货物失败");
                cargo.Tag = cargoTag;
                result.Success = true;
                return result;
            }
        }

        public CargoEditResult UpdateCargo(string cargoName, string cargoTag, string newCargoName, string newCargoTag)
        {
            lock (cargoBatchSync)
            {
                var result = new CargoEditResult { CargoName = cargoName, CargoTag = cargoTag };
                if (!ValidateCargoEdit(result)) return result;
                if (string.IsNullOrWhiteSpace(newCargoName) || string.IsNullOrWhiteSpace(newCargoTag))
                    return FailCargoEdit(result, "新类型和新编号不能为空");
                var newInfo = API.GetCargoInfo(newCargoName);
                if (newInfo == null) return FailCargoEdit(result, "新货物类型不存在，请先配置 CargoInfo");
                var cargo = FindEditableCargo(result);
                if (cargo == null) return result;
                if (API.Cargos.Any(c => !object.ReferenceEquals(c, cargo) && c.Info?.Name == newCargoName &&
                    Convert.ToString(c.Tag) == newCargoTag))
                    return FailCargoEdit(result, "新类型和编号已被其他货物使用");
                cargo.Info = newInfo;
                cargo.Tag = newCargoTag;
                result.CargoName = newCargoName;
                result.CargoTag = newCargoTag;
                result.Success = true;
                return result;
            }
        }

        public CargoEditResult DeleteCargo(string cargoName, string cargoTag)
        {
            lock (cargoBatchSync)
            {
                var result = new CargoEditResult { CargoName = cargoName, CargoTag = cargoTag };
                if (!ValidateCargoEdit(result)) return result;
                var cargo = FindEditableCargo(result);
                if (cargo == null) return result;
                // 仅从实际父容器取走指定类型的一件货；不清空货位或删除托盘。
                var parent = cargo.ParentContainer;
                if (parent == null) return FailCargoEdit(result, "货物没有父容器");
                var removed = parent.CargoTake(cargo.Info, 1);
                if (removed == null) return FailCargoEdit(result, "宿主删除货物失败");
                result.Success = true;
                return result;
            }
        }

        bool ValidateCargoEdit(CargoEditResult result)
        {
            if (string.IsNullOrWhiteSpace(result.CargoName) || string.IsNullOrWhiteSpace(result.CargoTag))
                result.Error = "货物类型和编号不能为空";
            else if (HasOpenCargoBatch() || TaskInfos.Any(t => t.Status != TaskStatus.Finished) ||
                PendingClearanceBatches.Count > 0 || PocketLocks.Count > 0)
                result.Error = "当前存在未完成任务、批次或巷道锁，请结束后再编辑货物";
            return result.Error == null;
        }

        ICargo FindEditableCargo(CargoEditResult result)
        {
            var matches = API.Cargos.Where(c => c.Info?.Name == result.CargoName &&
                Convert.ToString(c.Tag) == result.CargoTag).ToList();
            if (matches.Count != 1)
            {
                FailCargoEdit(result, matches.Count == 0 ? "未找到指定货物" : "该类型和编号匹配到多件货物");
                return null;
            }
            var cargo = matches[0];
            if (!(API.GetOwner(cargo) is IStation station))
            {
                FailCargoEdit(result, "货物不在站点，不能编辑 AGV 上的货物");
                return null;
            }
            result.StationMark = station.Mark;
            if (GetStationCargoArea(station, out var error) == null)
            {
                FailCargoEdit(result, error);
                return null;
            }
            // 宿主 CargoTake 按类型取货，因此只允许站点内唯一的一件货物，避免选错同类型实体。
            if (!IsExpectedCargoAtStation(station.Mark, result.CargoName, result.CargoTag))
            {
                FailCargoEdit(result, "站点必须仅有一件数量为 1 的指定货物");
                return null;
            }
            return cargo;
        }

        CargoEditResult FailCargoEdit(CargoEditResult result, string error)
        {
            result.Error = error ?? "货物操作失败";
            return result;
        }

    }
}
 
