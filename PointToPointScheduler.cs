namespace ProfControl
{
    public partial class SampleProject
    {
        // 接单仅登记客户要求；不创建执行步骤、不预约资源、不派车。
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
                        string.IsNullOrWhiteSpace(request.CargoTag) ||
                        string.IsNullOrWhiteSpace(request.ToMark))
                        return Reject("任务编号、商品车编号和最终目标货位不能为空");
                    if (!taskNos.Add(request.TaskNo)) return Reject($"任务编号重复：{request.TaskNo}");
                    if (!cargoIds.Add((CommodityCargoName, request.CargoTag)))
                        return Reject($"同一货物不能重复安排：{CommodityCargoName}/{request.CargoTag}");
                    if (!targets.Add(request.ToMark)) return Reject($"最终目标货位重复：{request.ToMark}");
                    var matches = API.Cargos.Where(c => c.Info?.Name == CommodityCargoName &&
                        Convert.ToString(c.Tag) == request.CargoTag).ToList();
                    if (matches.Count != 1 || !(API.GetOwner(matches[0]) is IStation source) ||
                        !IsExpectedCargoAtStation(source.Mark, CommodityCargoName, request.CargoTag))
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
                        CargoName = CommodityCargoName, CargoTag = request.CargoTag, TargetMark = request.ToMark,
                        SourceMark = source.Mark, SourcePocketKey = sourcePocket.Key,
                        Priority = request.Priority, SubmissionOrder = batch.Items.Count,
                        ReleaseTime = request.ReleaseTime, RequestedAgv = agvName,
                        PredecessorTaskNos = predecessors.Distinct(StringComparer.Ordinal).ToList(),
                        WaitReason = "已登记，等待调度"
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

        /// <summary>
        /// 使用本次查询时间和实时库存生成下一步候选。返回独立快照；不派车、不预约、不执行 FINALIZE。
        /// 不检查 AGV 可用性和巷道锁准入，这些由后续派车阶段处理。
        /// </summary>
        public PointCandidatePreview GetPointToPointCandidates(string batchId)
        {
            lock (cargoBatchSync)
            {
                var preview = new PointCandidatePreview { BatchId = batchId, GeneratedAt = DateTime.Now };
                var batch = cargoMoveBatches.FirstOrDefault(b => b.BatchId == batchId);
                if (string.IsNullOrWhiteSpace(batchId) || batch == null || batch.MoveType != CargoMoveType.PointToPoint)
                {
                    preview.Error = "点到点批次不存在或批次类型不符";
                    return preview;
                }
                var raw = new List<PointActionCandidate>();
                foreach (var item in batch.Items.OrderBy(i => i.Priority).ThenBy(i => i.SubmissionOrder))
                {
                    var taskPreview = new PointTaskCandidatePreview
                        { ItemId = item.ItemId, TaskNo = item.TaskNo, Status = item.Status };
                    preview.Tasks.Add(taskPreview);
                    BuildPointTaskCandidates(batch, item, preview.GeneratedAt, raw, taskPreview);
                }

                // 只去除同货物、同源位或同目标位的重复冲突；访问路径冲突在派单预约时复核。
                var cargos = new HashSet<(string Name, string Tag)>();
                var sources = new HashSet<string>(StringComparer.Ordinal);
                var targets = new HashSet<string>(StringComparer.Ordinal);
                foreach (var candidate in raw.OrderBy(c => c.Priority).ThenBy(c => c.SubmissionOrder)
                    .ThenBy(c => c.Phase).ThenBy(c => c.ActionType)
                    .ThenBy(c => c.SourcePocketKey, StringComparer.Ordinal)
                    .ThenBy(c => c.FromMark, Comparer<string>.Create(ComparePointStationMarks))
                    .ThenBy(c => c.TargetPocketKey, StringComparer.Ordinal)
                    .ThenBy(c => c.ToMark, Comparer<string>.Create(ComparePointStationMarks)))
                {
                    if (cargos.Contains((candidate.CargoName, candidate.CargoTag)) ||
                        sources.Contains(candidate.FromMark) || targets.Contains(candidate.ToMark))
                    {
                        preview.Tasks.First(t => t.ItemId == candidate.ItemId).Notes
                            .Add($"{candidate.ActionType} 与排序靠前的候选使用同一货物、源位或目标位，已去重");
                        continue;
                    }
                    cargos.Add((candidate.CargoName, candidate.CargoTag));
                    sources.Add(candidate.FromMark);
                    targets.Add(candidate.ToMark);
                    preview.Candidates.Add(candidate);
                }
                foreach (var task in preview.Tasks)
                {
                    task.CandidateCount = preview.Candidates.Count(c => c.ItemId == task.ItemId);
                    if (task.CandidateCount == 0 && task.Status != CargoTaskStatus.Completed)
                        task.WaitReason = task.Notes.Count == 0 ? "当前没有合法候选动作" : string.Join("；", task.Notes.Distinct());
                }
                preview.Success = true;
                return preview;
            }
        }

        void BuildPointTaskCandidates(CargoMoveBatch batch, CargoMoveItem item, DateTime now,
            List<PointActionCandidate> candidates, PointTaskCandidatePreview preview)
        {
            if (item.Status == CargoTaskStatus.Completed) return;
            if (batch.Status == CargoBatchStatus.NeedsReview || item.Status == CargoTaskStatus.NeedsReview)
            {
                preview.Notes.Add("批次或客户任务待现场核对");
                return;
            }
            if (item.Status == CargoTaskStatus.Executing || TaskInfos.Any(t => PointMoveInProgress(t) &&
                ((t.CargoBatchId == batch.BatchId && t.CargoItemId == item.ItemId) ||
                    (t.CargoName == item.CargoName && t.CargoTag == item.CargoTag))))
            {
                preview.Notes.Add("客户任务已有执行动作或货物正在搬运，等待执行反馈");
                return;
            }
            if (item.ReleaseTime.HasValue && now < item.ReleaseTime.Value)
            {
                preview.Notes.Add($"尚未到释放时间 {item.ReleaseTime.Value:yyyy-MM-dd HH:mm:ss}");
                return;
            }
            var pending = item.HardPredecessorTaskNos.Concat(item.PredecessorTaskNos).Distinct()
                .Where(no => !batch.Items.Any(i => i.TaskNo == no && i.Status == CargoTaskStatus.Completed)).ToList();
            if (pending.Count > 0)
            {
                preview.Notes.Add("等待硬前置完成：" + string.Join("、", pending));
                return;
            }
            if (!TryGetPointCargoSource(item.CargoName, item.CargoTag, out var source, out var error))
            {
                preview.Notes.Add(error);
                return;
            }
            preview.CurrentMark = source.Mark;
            if (!TryGetPointPocketState(GetStationPocketKey(source.Mark), out var pick, out error) ||
                !TryGetPointPocketState(GetStationPocketKey(item.TargetMark), out var drop, out error))
            {
                preview.Notes.Add(error);
                return;
            }
            var targetIndex = drop.Pocket.Slots.FindIndex(s => s.Mark == item.TargetMark);
            if (targetIndex < 0)
            {
                preview.Notes.Add("最终目标不是有效货位");
                return;
            }
            if (source.Mark == item.TargetMark)
            {
                if (HasUnfinishedPointCargoBelow(item.TargetMark, item.CargoName, item.CargoTag, out var protectedTask))
                    AddPointRelocation(batch, item, drop.OutermostMark, PointActionPhase.TargetPreparation,
                        $"到位确认前保护深处未完成客户货物（{protectedTask}）", candidates, preview);
                else if (HasPointPendingPickup(item.CargoName, item.CargoTag, source.Mark))
                    preview.Notes.Add("货物或最终目标仍有待取货预约，暂不能 FINALIZE");
                else
                    candidates.Add(NewPointCandidate(batch, item, PointActionType.FINALIZE, PointActionPhase.Customer,
                        item.CargoName, item.CargoTag, source.Mark, item.TargetMark, 0,
                        "货物已在最终目标，释放时间、硬前置、未来货物保护和待取货预约检查通过"));
                return;
            }

            if (pick.Pocket.Key == drop.Pocket.Key)
            {
                if (pick.OutermostMark != source.Mark)
                    AddPointRelocation(batch, item, pick.OutermostMark, PointActionPhase.SourcePreparation,
                        "同巷道搬运先清除源端最外侧阻挡", candidates, preview);
                else if (TryBuildPointMove(batch, item, PointActionType.CUSTOMER_MOVE, PointActionPhase.Customer,
                    item.CargoName, item.CargoTag, item.TargetMark, "同巷道取走后可立即合法放入最终目标", out var move, out error))
                    candidates.Add(move);
                else
                    AddPointRelocation(batch, item, source.Mark, PointActionPhase.SourcePreparation,
                        "同巷道取走后无法立即合法放入最终目标，先暂存客户货物", candidates, preview);
                return;
            }

            var targetReady = false;
            if (targetIndex < drop.OccupiedCount)
                AddPointRelocation(batch, item, drop.OutermostMark, PointActionPhase.TargetPreparation,
                    "最终目标处于占用区域，先移走目标巷道最外侧障碍", candidates, preview);
            else if (HasUnfinishedPointCargoBelow(item.TargetMark, item.CargoName, item.CargoTag, out var protectedTask))
                AddPointRelocation(batch, item, drop.OutermostMark, PointActionPhase.TargetPreparation,
                    $"放入前保护深处未完成客户货物（{protectedTask}）", candidates, preview);
            else if (targetIndex > drop.OccupiedCount)
                AddPointFilling(batch, item, pick, source.Mark, drop.NextDropMark, candidates, preview);
            else targetReady = true;

            // 目标准备优先，但不阻止独立的源端准备动作成为候选。
            if (pick.OutermostMark != source.Mark)
                AddPointRelocation(batch, item, pick.OutermostMark, PointActionPhase.SourcePreparation,
                    "移走源端当前最外侧阻挡货物", candidates, preview);
            else if (targetReady)
            {
                if (TryBuildPointMove(batch, item, PointActionType.CUSTOMER_MOVE, PointActionPhase.Customer,
                    item.CargoName, item.CargoTag, item.TargetMark, "源货物可取且最终目标已准备好", out var move, out error))
                    candidates.Add(move);
                else preview.Notes.Add(error);
            }
        }

        bool HasPointPendingPickup(string cargoName, string cargoTag, string mark)
        {
            return pointMoveReservations.Values.Any(r => !r.SourceReleased &&
                ((r.CargoName == cargoName && r.CargoTag == cargoTag) || r.FromMark == mark)) ||
                TaskInfos.Any(t => t.Status != TaskStatus.Finished &&
                    (t.Status == TaskStatus.None || t.Status == TaskStatus.GoPickEntrance ||
                     t.Status == TaskStatus.GoPick || t.Status == TaskStatus.Picking || t.Status == TaskStatus.NeedsReview) &&
                    ((t.CargoName == cargoName && t.CargoTag == cargoTag) || t.FromMark == mark));
        }

        bool HasUnfinishedPointCargoBelow(string targetMark, string movingName, string movingTag, out string taskNo)
        {
            taskNo = null;
            var pocket = GetCargoPocket(GetStationPocketKey(targetMark));
            var index = pocket.Slots.FindIndex(s => s.Mark == targetMark);
            var deeper = new HashSet<string>(pocket.Slots.Take(index).Select(s => s.Mark), StringComparer.Ordinal);
            var customers = cargoMoveBatches.Where(b => b.MoveType == CargoMoveType.PointToPoint)
                .SelectMany(b => b.Items).Where(i => i.Status != CargoTaskStatus.Completed &&
                    !(i.CargoName == movingName && i.CargoTag == movingTag));
            foreach (var item in customers)
                if (API.Cargos.Any(c => c.Info?.Name == item.CargoName && Convert.ToString(c.Tag) == item.CargoTag &&
                    API.GetOwner(c) is IStation s && deeper.Contains(s.Mark)))
                {
                    taskNo = item.TaskNo;
                    return true;
                }
            return false;
        }

        bool IsPointCustomerCargo(string cargoName, string cargoTag)
        {
            // 普通填充货物不能属于任何已登记客户任务，包括未释放和已完成的任务。
            return cargoMoveBatches.SelectMany(b => b.Items).Any(i => i.CargoName == cargoName && i.CargoTag == cargoTag);
        }

        bool TryBuildPointMove(CargoMoveBatch batch, CargoMoveItem item, PointActionType type, PointActionPhase phase,
            string cargoName, string cargoTag, string targetMark, string reason,
            out PointActionCandidate candidate, out string error)
        {
            candidate = null;
            if (TaskInfos.Any(t => PointMoveInProgress(t) && t.CargoName == cargoName && t.CargoTag == cargoTag))
            {
                error = "候选货物已有执行动作，等待执行反馈";
                return false;
            }
            if (!CanPointCargoMove(cargoName, cargoTag, targetMark, null, out var from, out error)) return false;
            if (HasUnfinishedPointCargoBelow(targetMark, cargoName, cargoTag, out var protectedTask))
            {
                error = $"该放货位置会封住深处未完成客户货物（{protectedTask}）";
                return false;
            }
            if (!TryGetPointLoadedDistance(from, targetMark, out var distance, out error)) return false;
            candidate = NewPointCandidate(batch, item, type, phase, cargoName, cargoTag, from, targetMark, distance, reason);
            return true;
        }

        PointActionCandidate NewPointCandidate(CargoMoveBatch batch, CargoMoveItem item,
            PointActionType type, PointActionPhase phase, string cargoName, string cargoTag,
            string from, string to, double distance, string reason)
        {
            return new PointActionCandidate
            {
                BatchId = batch.BatchId, ItemId = item.ItemId, TaskNo = item.TaskNo,
                Priority = item.Priority, SubmissionOrder = item.SubmissionOrder, ActionType = type, Phase = phase,
                CargoName = cargoName, CargoTag = cargoTag, FromMark = from, ToMark = to,
                SourcePocketKey = GetStationPocketKey(from), TargetPocketKey = GetStationPocketKey(to),
                LoadedDistance = distance, Reason = reason
            };
        }

        void AddPointRelocation(CargoMoveBatch batch, CargoMoveItem item, string fromMark, PointActionPhase phase,
            string reason, List<PointActionCandidate> candidates, PointTaskCandidatePreview preview)
        {
            if (fromMark == null || !TryGetStationCargo(fromMark, out var cargo, out var error))
            {
                preview.Notes.Add("当前没有有效的最外侧障碍货物");
                return;
            }
            if (!CanPickPointCargo(cargo.Info.Name, Convert.ToString(cargo.Tag), null, out _, out error))
            {
                preview.Notes.Add(reason + "：" + error);
                return;
            }
            var possible = new List<PointActionCandidate>();
            var failures = new List<string>();
            var sourceKey = GetStationPocketKey(fromMark);
            foreach (var pocket in cargoPockets.Values.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (pocket.Key == sourceKey ||
                    (phase == PointActionPhase.SourcePreparation && pocket.Key == GetStationPocketKey(item.TargetMark))) continue;
                if (!TryGetPointPocketState(pocket.Key, out var state, out error) || state.NextDropMark == null) continue;
                if (TryBuildPointMove(batch, item, PointActionType.RELOCATION, phase, cargo.Info.Name,
                    Convert.ToString(cargo.Tag), state.NextDropMark, reason, out var candidate, out error)) possible.Add(candidate);
                else failures.Add(error);
            }
            var best = possible.OrderBy(c => c.LoadedDistance).ThenBy(c => c.TargetPocketKey, StringComparer.Ordinal)
                .ThenBy(c => c.ToMark, Comparer<string>.Create(ComparePointStationMarks)).FirstOrDefault();
            if (best != null) candidates.Add(best);
            else preview.Notes.Add(reason + "：无合法临时目标" + PointFailureDetails(failures));
        }

        void AddPointFilling(CargoMoveBatch batch, CargoMoveItem item, CargoPocketState pick, string customerSource,
            string fillMark, List<PointActionCandidate> candidates, PointTaskCandidatePreview preview)
        {
            var possible = new List<PointActionCandidate>();
            var failures = new List<string>();
            foreach (var pocket in cargoPockets.Values.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (pocket.Key == GetStationPocketKey(fillMark) ||
                    !TryGetPointPocketState(pocket.Key, out var state, out _) || state.OutermostMark == null ||
                    !TryGetStationCargo(state.OutermostMark, out var cargo, out _)) continue;
                var tag = Convert.ToString(cargo.Tag);
                if (IsPointCustomerCargo(cargo.Info.Name, tag)) continue;
                if (TryBuildPointMove(batch, item, PointActionType.FILLING, PointActionPhase.TargetPreparation,
                    cargo.Info.Name, tag, fillMark, "用普通货物填入最终目标之前的下一个合法空位", out var candidate, out var error))
                {
                    candidate.ClearsSource = pick.OutermostMark != customerSource && candidate.FromMark == pick.OutermostMark;
                    if (candidate.ClearsSource) candidate.Reason += "，同时清除本任务源端阻挡";
                    possible.Add(candidate);
                }
                else failures.Add(error);
            }
            var best = possible.OrderByDescending(c => c.ClearsSource).ThenBy(c => c.LoadedDistance)
                .ThenBy(c => c.SourcePocketKey, StringComparer.Ordinal)
                .ThenBy(c => c.FromMark, Comparer<string>.Create(ComparePointStationMarks)).FirstOrDefault();
            if (best != null) candidates.Add(best);
            else preview.Notes.Add("需要 Filling，但没有合法的普通货物" + PointFailureDetails(failures));
        }

        static string PointFailureDetails(List<string> failures)
        {
            var details = failures.Where(s => !string.IsNullOrEmpty(s)).Distinct().Take(3).ToList();
            return details.Count == 0 ? "" : "（" + string.Join("；", details) + "）";
        }

        static int ComparePointStationMarks(string left, string right)
        {
            var leftIsNumber = long.TryParse(left, out var l);
            var rightIsNumber = long.TryParse(right, out var r);
            if (leftIsNumber && rightIsNumber)
            {
                var comparison = l.CompareTo(r);
                if (comparison != 0) return comparison;
            }
            if (leftIsNumber != rightIsNumber) return leftIsNumber ? -1 : 1;
            return StringComparer.Ordinal.Compare(left, right);
        }
    }
}
