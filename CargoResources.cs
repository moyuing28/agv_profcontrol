namespace ProfControl
{
    public partial class SampleProject
    {
        // 仅供点到点调度调用。所有入口沿用 cargoBatchSync，不操作 PocketLocks。
        sealed class CargoPocketState
        {
            public CargoPocketConfig Pocket;
            public int OccupiedCount;
            public string OutermostMark => OccupiedCount == 0 ? null : Pocket.Slots[OccupiedCount - 1].Mark;
            public string NextDropMark => OccupiedCount == Pocket.Slots.Count ? null : Pocket.Slots[OccupiedCount].Mark;
        }

        sealed class CargoMoveReservation
        {
            public TaskInfo Owner;
            public string CargoName;
            public string CargoTag;
            public string FromMark;
            public string ToMark;
            public HashSet<string> SourceMarks;
            public HashSet<string> TargetMarks;
            public bool SourceReleased;
        }

        readonly Dictionary<string, CargoMoveReservation> pointMoveReservations =
            new Dictionary<string, CargoMoveReservation>(StringComparer.Ordinal);

        // 只返回实时位置，绝不把客户明细 SourceMark 当作实际起点；车载货物不可生成取货动作。
        bool TryGetPointCargoSource(string cargoName, string cargoTag, out IStation source, out string error)
        {
            lock (cargoBatchSync)
            {
                source = null;
                error = null;
                if (string.IsNullOrWhiteSpace(cargoName) || string.IsNullOrWhiteSpace(cargoTag))
                    error = "货物类型和编号不能为空";
                else
                {
                    var matches = API.Cargos.Where(c => c.Info?.Name == cargoName &&
                        Convert.ToString(c.Tag) == cargoTag).ToList();
                    if (matches.Count != 1) error = "指定货物不存在或编号不唯一";
                    else if (!(API.GetOwner(matches[0]) is IStation station)) error = "货物当前不在站点，可能已在 AGV 上";
                    else if (!IsExpectedCargoAtStation(station.Mark, cargoName, cargoTag)) error = "源货位必须仅有一件数量为 1 的指定货物";
                    else source = station;
                }
                return error == null;
            }
        }

        // 从深到浅校验连续占用；无效初始库存只报告，不自动填洞或改库存。
        bool TryGetPointPocketState(string pocketKey, out CargoPocketState state, out string error)
        {
            lock (cargoBatchSync)
            {
                state = null;
                var pocket = GetCargoPocket(pocketKey);
                error = pocket == null ? "找不到货物巷道" : pocket.Error;
                if (error != null) return false;
                if (pocket.Slots == null || pocket.Slots.Count == 0)
                {
                    error = "巷道没有货位";
                    return false;
                }
                var count = 0;
                var foundEmpty = false;
                foreach (var station in pocket.Slots)
                {
                    if (!stationCargoAreas.ContainsKey(station.Mark))
                    {
                        error = $"货位 {station.Mark} 朝向配置无效";
                        return false;
                    }
                    if (station.IsEmpty())
                    {
                        // 宿主占用状态与货物归属必须一致。
                        if (API.Cargos.Any(c => API.GetOwner(c) is IStation s && s.Mark == station.Mark))
                        {
                            error = $"货位 {station.Mark} 占用状态与货物归属不一致";
                            return false;
                        }
                        foundEmpty = true;
                    }
                    else
                    {
                        if (foundEmpty)
                        {
                            error = $"巷道 {pocketKey} 内存在空洞，不满足 No-hole";
                            return false;
                        }
                        if (!TryGetStationCargo(station.Mark, out _, out error)) return false;
                        count++;
                    }
                }
                state = new CargoPocketState { Pocket = pocket, OccupiedCount = count };
                return true;
            }
        }

        bool IsCompletedPointCargo(string cargoName, string cargoTag)
        {
            return cargoMoveBatches.Where(b => b.MoveType == CargoMoveType.PointToPoint)
                .SelectMany(b => b.Items).Any(i => i.Status == CargoTaskStatus.Completed &&
                    i.CargoName == cargoName && i.CargoTag == cargoTag);
        }

        // 含源/目标位本身及其外侧货位；入口兼作库位时同样预约，独立入口不在 Slots 中。
        HashSet<string> PointAccessMarks(CargoPocketConfig pocket, string mark)
        {
            var index = pocket.Slots.FindIndex(s => s.Mark == mark);
            return index < 0 ? new HashSet<string>() :
                new HashSet<string>(pocket.Slots.Skip(index).Select(s => s.Mark), StringComparer.Ordinal);
        }

        bool PointReservationAvailable(TaskInfo owner, string cargoName, string cargoTag,
            IEnumerable<string> marks, out string error)
        {
            var requested = new HashSet<string>(marks, StringComparer.Ordinal);
            foreach (var reservation in pointMoveReservations.Values)
            {
                if (object.ReferenceEquals(reservation.Owner, owner)) continue;
                if (reservation.CargoName == cargoName && reservation.CargoTag == cargoTag)
                {
                    error = "货物已被其他搬运动作预约";
                    return false;
                }
                if (requested.Overlaps(reservation.SourceMarks) || requested.Overlaps(reservation.TargetMarks))
                {
                    error = "源、目标或外侧访问货位已被其他搬运动作预约";
                    return false;
                }
            }
            error = null;
            return true;
        }

        bool CanPickPointCargo(string cargoName, string cargoTag, TaskInfo owner,
            out string fromMark, out string error)
        {
            lock (cargoBatchSync)
            {
                fromMark = null;
                if (!TryGetPointCargoSource(cargoName, cargoTag, out var source, out error)) return false;
                fromMark = source.Mark;
                if (IsCompletedPointCargo(cargoName, cargoTag))
                {
                    error = "已完成客户任务的货物不允许再次搬动";
                    return false;
                }
                if (!TryGetPointPocketState(GetStationPocketKey(fromMark), out var pick, out error)) return false;
                if (pick.OutermostMark != fromMark)
                {
                    error = "源货物不是当前最外侧货物";
                    return false;
                }
                return PointReservationAvailable(owner, cargoName, cargoTag,
                    PointAccessMarks(pick.Pocket, fromMark), out error);
            }
        }

        // 纯检查，无资源副作用。目标按同巷道取走源货物后的状态判断。
        // 不包含路线、客户前置、未来货物保护和派车检查，后续动作生成器须另外判断。
        bool CanPointCargoMove(string cargoName, string cargoTag, string toMark,
            TaskInfo owner, out string fromMark, out string error)
        {
            lock (cargoBatchSync)
            {
                if (!CanPickPointCargo(cargoName, cargoTag, owner, out fromMark, out error)) return false;
                if (fromMark == toMark)
                {
                    error = "货物已在目标位，应判断 FINALIZE，无需生成搬运动作";
                    return false;
                }
                if (!TryGetPointPocketState(GetStationPocketKey(fromMark), out var pick, out error) ||
                    !TryGetPointPocketState(GetStationPocketKey(toMark), out var drop, out error)) return false;
                var dropCount = drop.OccupiedCount - (pick.Pocket.Key == drop.Pocket.Key ? 1 : 0);
                var legalDrop = dropCount < drop.Pocket.Slots.Count ? drop.Pocket.Slots[dropCount].Mark : null;
                if (legalDrop != toMark)
                {
                    error = "目标不是取货后满足 No-hole 的下一个合法放货位";
                    return false;
                }
                return PointReservationAvailable(owner, cargoName, cargoTag,
                    PointAccessMarks(pick.Pocket, fromMark).Concat(PointAccessMarks(drop.Pocket, toMark)), out error);
            }
        }

        // 动作选定后调用；整组资源一次性取得，失败不留下部分预约。
        bool TryReservePointMove(TaskInfo owner, out string error)
        {
            lock (cargoBatchSync)
            {
                error = null;
                if (owner == null || string.IsNullOrWhiteSpace(owner.TaskId))
                {
                    error = "预约必须关联有效搬运动作";
                    return false;
                }
                if (pointMoveReservations.TryGetValue(owner.TaskId, out var existing))
                {
                    if (object.ReferenceEquals(existing.Owner, owner) && !existing.SourceReleased &&
                        existing.CargoName == owner.CargoName && existing.CargoTag == owner.CargoTag &&
                        existing.FromMark == owner.FromMark && existing.ToMark == owner.ToMark)
                    {
                        if (!CanPointCargoMove(owner.CargoName, owner.CargoTag, owner.ToMark, owner,
                            out var liveSource, out error)) return false;
                        if (liveSource == owner.FromMark) return true;
                        error = "货物实时位置已变化，必须重新生成动作";
                        return false;
                    }
                    error = "搬运编号已有预约，禁止替换或重复使用";
                    return false;
                }
                if (!CanPointCargoMove(owner.CargoName, owner.CargoTag, owner.ToMark, owner, out var from, out error))
                    return false;
                if (from != owner.FromMark)
                {
                    error = "货物实时位置已变化，必须重新生成动作";
                    return false;
                }
                pointMoveReservations.Add(owner.TaskId, new CargoMoveReservation
                {
                    Owner = owner, CargoName = owner.CargoName, CargoTag = owner.CargoTag,
                    FromMark = from, ToMark = owner.ToMark,
                    SourceMarks = PointAccessMarks(GetCargoPocket(GetStationPocketKey(from)), from),
                    TargetMarks = PointAccessMarks(GetCargoPocket(GetStationPocketKey(owner.ToMark)), owner.ToMark)
                });
                return true;
            }
        }

        // 核实 Pickup 完成后调用。仅释放源访问位，目标和货物身份预约继续保留。
        bool ReleasePointSourceReservation(TaskInfo owner)
        {
            lock (cargoBatchSync)
            {
                if (!TryGetOwnedPointReservation(owner, out var reservation)) return false;
                reservation.SourceMarks.Clear();
                reservation.SourceReleased = true;
                return true;
            }
        }

        // 核实 Dropoff 完成，或确认未执行并取消动作后调用；不能用其他对象冒领同编号资源。
        bool ReleasePointMoveReservation(TaskInfo owner)
        {
            lock (cargoBatchSync)
            {
                return TryGetOwnedPointReservation(owner, out _) && pointMoveReservations.Remove(owner.TaskId);
            }
        }

        bool TryGetOwnedPointReservation(TaskInfo owner, out CargoMoveReservation reservation)
        {
            reservation = null;
            return owner != null && !string.IsNullOrEmpty(owner.TaskId) &&
                pointMoveReservations.TryGetValue(owner.TaskId, out reservation) &&
                object.ReferenceEquals(reservation.Owner, owner);
        }

        // Dropoff 前复核仍由原动作持有预约，且当前真实库存允许放入。
        bool CanDropReservedPointMove(TaskInfo owner, out string error)
        {
            lock (cargoBatchSync)
            {
                if (!TryGetOwnedPointReservation(owner, out var reservation) || !reservation.SourceReleased ||
                    reservation.CargoName != owner.CargoName || reservation.CargoTag != owner.CargoTag ||
                    reservation.FromMark != owner.FromMark || reservation.ToMark != owner.ToMark)
                {
                    error = "搬运未完成取货或预约归属、动作内容不一致";
                    return false;
                }
                if (IsCompletedPointCargo(owner.CargoName, owner.CargoTag))
                {
                    error = "已完成客户货物不能继续搬动";
                    return false;
                }
                var cargos = API.Cargos.Where(c => c.Info?.Name == owner.CargoName &&
                    Convert.ToString(c.Tag) == owner.CargoTag).ToList();
                if (owner.WorkAgv == null || cargos.Count != 1 || cargos[0].Quanlity != 1 ||
                    !object.ReferenceEquals(API.GetOwner(cargos[0]), owner.WorkAgv))
                {
                    error = "指定货物不在本动作 AGV 上或身份、数量不符";
                    return false;
                }
                if (!TryGetPointPocketState(GetStationPocketKey(owner.ToMark), out var target, out error)) return false;
                if (target.NextDropMark != owner.ToMark)
                {
                    error = "目标库存已变化，不再是合法放货位";
                    return false;
                }
                return PointReservationAvailable(owner, owner.CargoName, owner.CargoTag, reservation.TargetMarks, out error);
            }
        }
    }
}
