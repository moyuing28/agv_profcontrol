namespace ProfControl
{
    public partial class SampleProject
    {
        // 第一阶段仅登记客户要求；不创建执行步骤、不预约资源、不派车。
        public PointToPointCreateResult CreateCargoMoveTask(IEnumerable<CargoMoveRequest> tasks)
        {
            lock (cargoBatchSync)
            {
                PointToPointCreateResult Reject(string error) => new PointToPointCreateResult { Error = error };
                if (tasks == null) return Reject("任务列表不能为空");
                var requests = tasks.ToList();
                if (requests.Count == 0) return Reject("任务列表不能为空");
                if (HasOpenCargoBatch() || TaskInfos.Any(t => t.Status != TaskStatus.Finished) ||
                    PendingClearanceBatches.Count > 0)
                    return Reject("当前已有未完成任务或搬运批次，请在结束后提交");

                var batch = new CargoMoveBatch { MoveType = CargoMoveType.PointToPoint };
                var taskNos = new HashSet<string>(StringComparer.Ordinal);
                var cargoIds = new HashSet<(string Name, string Tag)>();
                var targets = new HashSet<string>(StringComparer.Ordinal);
                foreach (var request in requests)
                {
                    if (request == null || string.IsNullOrWhiteSpace(request.TaskNo) ||
                        string.IsNullOrWhiteSpace(request.CargoName) || string.IsNullOrWhiteSpace(request.CargoTag) ||
                        string.IsNullOrWhiteSpace(request.ToMark))
                        return Reject("任务编号、货物类型、货物编号和最终目标货位不能为空");
                    if (!taskNos.Add(request.TaskNo)) return Reject($"任务编号重复：{request.TaskNo}");
                    if (!cargoIds.Add((request.CargoName, request.CargoTag)))
                        return Reject($"同一货物不能重复安排：{request.CargoName}/{request.CargoTag}");
                    if (!targets.Add(request.ToMark)) return Reject($"最终目标货位重复：{request.ToMark}");
                    var matches = API.Cargos.Where(c => c.Info?.Name == request.CargoName &&
                        Convert.ToString(c.Tag) == request.CargoTag).ToList();
                    if (matches.Count != 1 || !(API.GetOwner(matches[0]) is IStation source) ||
                        !IsExpectedCargoAtStation(source.Mark, request.CargoName, request.CargoTag))
                        return Reject($"任务 {request.TaskNo} 的货物必须唯一且在站点上，数量为 1");
                    var sourcePocket = GetCargoPocket(GetStationPocketKey(source.Mark));
                    var targetPocket = GetCargoPocket(GetStationPocketKey(request.ToMark));
                    if (sourcePocket == null || sourcePocket.Error != null ||
                        !sourcePocket.Slots.Any(s => s.Mark == source.Mark) ||
                        targetPocket == null || targetPocket.Error != null ||
                        !targetPocket.Slots.Any(s => s.Mark == request.ToMark) ||
                        !stationCargoAreas.ContainsKey(source.Mark) || !stationCargoAreas.ContainsKey(request.ToMark))
                        return Reject($"任务 {request.TaskNo} 的源货位或最终目标货位配置无效");
                    var agvName = string.IsNullOrWhiteSpace(request.AgvName) ? "00" : request.AgvName;
                    if (agvName != "00" && !API.Agvs.Any(a => a.Name == agvName))
                        return Reject($"任务 {request.TaskNo} 找不到指定 AGV：{agvName}");
                    var predecessors = request.PredecessorTaskNos?.ToList() ?? new List<string>();
                    if (predecessors.Any(string.IsNullOrWhiteSpace))
                        return Reject($"任务 {request.TaskNo} 的前置任务编号不能为空");
                    batch.Items.Add(new CargoMoveItem
                    {
                        BatchId = batch.BatchId, TaskNo = request.TaskNo,
                        CargoName = request.CargoName, CargoTag = request.CargoTag, TargetMark = request.ToMark,
                        SourceMark = source.Mark, SourcePocketKey = sourcePocket.Key,
                        Priority = request.Priority, SubmissionOrder = batch.Items.Count,
                        ReleaseTime = request.ReleaseTime, RequestedAgv = agvName,
                        PredecessorTaskNos = predecessors.Distinct(StringComparer.Ordinal).ToList(),
                        WaitReason = "已登记，等待点到点动态调度接入"
                    });
                }

                foreach (var item in batch.Items)
                {
                    if (item.PredecessorTaskNos.Any(p => p == item.TaskNo || !taskNos.Contains(p)))
                        return Reject($"任务 {item.TaskNo} 的前置必须是本次提交的其他任务");
                    var pocket = GetCargoPocket(GetStationPocketKey(item.TargetMark));
                    var index = pocket.Slots.FindIndex(s => s.Mark == item.TargetMark);
                    item.HardPredecessorTaskNos = item.PredecessorTaskNos.Concat(batch.Items
                        .Where(other => GetStationPocketKey(other.TargetMark) == pocket.Key &&
                            pocket.Slots.FindIndex(s => s.Mark == other.TargetMark) < index)
                        .Select(other => other.TaskNo)).Distinct(StringComparer.Ordinal).ToList();
                }
                // 拓扑检查同时包含业务前置和目标巷道深浅前置。
                var remaining = batch.Items.ToDictionary(i => i.TaskNo,
                    i => new HashSet<string>(i.HardPredecessorTaskNos), StringComparer.Ordinal);
                while (remaining.Count > 0)
                {
                    var ready = remaining.Where(p => p.Value.Count == 0).Select(p => p.Key).ToList();
                    if (ready.Count == 0) return Reject("业务前置与目标巷道深浅前置存在循环，整批未登记");
                    foreach (var taskNo in ready) remaining.Remove(taskNo);
                    foreach (var predecessors in remaining.Values) predecessors.ExceptWith(ready);
                }
                cargoMoveBatches.Add(batch);
                return new PointToPointCreateResult
                {
                    Success = true, BatchId = batch.BatchId,
                    Tasks = batch.Items.Select(i => new PointToPointTaskReceipt
                        { TaskNo = i.TaskNo, ItemId = i.ItemId }).ToList()
                };
            }
        }
    }
}
