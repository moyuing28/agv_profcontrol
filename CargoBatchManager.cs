namespace ProfControl
{
    public partial class SampleProject
    {
        readonly object cargoBatchSync = new object();
        readonly List<CargoMoveBatch> cargoMoveBatches = new List<CargoMoveBatch>();

        bool HasOpenCargoBatch() => cargoMoveBatches.Any(b => b.Status != CargoBatchStatus.Completed);

        /// <summary>
        /// 返回所有内存批次的进度快照，最新创建的在前；无批次时返回空列表。
        /// 仅查询，不派车、不推进状态。完成数包含车辆返回停车位。
        /// </summary>
        public List<CargoBatchSummary> GetBatches()
        {
            lock (cargoBatchSync)
            {
                return cargoMoveBatches.OrderByDescending(b => b.CreatedTime)
                    .Select(b => CopyBatchSummary(b, new CargoBatchSummary())).ToList();
            }
        }

        /// <summary>
        /// 返回指定批次及全部明细的独立快照，明细按来源巷道、取货顺序排列。
        /// 编号为空或不存在时返回 null；不会创建或执行任务。
        /// 状态来自最近一次调度更新，返回后不会自动变化，需再次调用获取新快照。
        /// </summary>
        public CargoBatchDetail GetBatchDetail(string batchId)
        {
            lock (cargoBatchSync)
            {
                if (string.IsNullOrWhiteSpace(batchId)) return null;
                var batch = cargoMoveBatches.FirstOrDefault(b => b.BatchId == batchId);
                if (batch == null) return null;
                var detail = new CargoBatchDetail();
                CopyBatchSummary(batch, detail);
                detail.Items = batch.Items.OrderBy(i => i.SourcePocketKey).ThenBy(i => i.PickSequence)
                    .Select(i => new CargoItemDetail
                    {
                        ItemId = i.ItemId,
                        BatchId = i.BatchId,
                        CargoName = i.CargoName,
                        CargoTag = i.CargoTag,
                        TargetMark = i.TargetMark,
                        TargetPocketNames = i.TargetPocketNames.ToList(),
                        Steps = i.ExecutionSteps.Select((s, index) => new CargoStepDetail
                        {
                            TaskId = s.TaskId, Sequence = index + 1, StepType = s.StepType,
                            CargoName = s.CargoName, CargoTag = s.CargoTag,
                            FromMark = s.FromMark, ToMark = s.ToMark, DropPocketKey = s.DropPocketKey,
                            AgvName = s.WorkAgv?.Name, Activated = TaskInfos.Any(t => t.TaskId == s.TaskId),
                            Status = s.Status, StatusText = TaskInfos.Any(t => t.TaskId == s.TaskId) ? CargoStageText(s.Status) : "等待前序步骤",
                            WaitReason = s.LastWaitReason
                        }).ToList(),
                        SourcePocketKey = i.SourcePocketKey,
                        SourceMark = i.SourceMark,
                        PickSequence = i.PickSequence,
                        AssignedDropPocketKey = i.AssignedDropPocketKey,
                        AssignedDropMark = i.AssignedDropMark,
                        Status = i.Status,
                        StatusText = i.Status switch
                        {
                            CargoTaskStatus.Waiting => "等待调度",
                            CargoTaskStatus.Executing => "执行中",
                            CargoTaskStatus.Completed => "已完成",
                            CargoTaskStatus.NeedsReview => "待现场核对",
                            _ => "未知状态"
                        },
                        WaitReason = i.WaitReason,
                        ExecutionTaskId = i.ExecutionTaskId,
                        AssignedAgv = i.AssignedAgv,
                        ExecutionStatus = i.ExecutionStatus,
                        ExecutionStatusText = i.ExecutionStatus.HasValue ? CargoStageText(i.ExecutionStatus.Value) : null,
                        CompletedTime = i.CompletedTime
                    }).ToList();
                return detail;
            }
        }

        CargoBatchSummary CopyBatchSummary(CargoMoveBatch batch, CargoBatchSummary snapshot)
        {
            snapshot.BatchId = batch.BatchId;
            snapshot.MoveType = batch.MoveType;
            snapshot.SourceScopeId = batch.SourceScopeId;
            snapshot.TargetScopeId = batch.TargetScopeId;
            snapshot.SourcePocketNames = batch.SourcePocketNames.ToList();
            snapshot.TargetPocketNames = batch.TargetPocketNames.ToList();
            snapshot.TargetCapacities = GetTargetCapacities(batch);
            snapshot.Status = batch.Status;
            snapshot.StatusText = batch.Status switch
            {
                CargoBatchStatus.Waiting => "等待调度",
                CargoBatchStatus.Running => "执行中",
                CargoBatchStatus.Completed => "已完成",
                CargoBatchStatus.NeedsReview => "待现场核对",
                _ => "未知状态"
            };
            snapshot.CreatedTime = batch.CreatedTime;
            snapshot.CompletedTime = batch.CompletedTime;
            snapshot.TotalCount = batch.TotalCount;
            snapshot.WaitingCount = batch.Items.Count(i => i.Status == CargoTaskStatus.Waiting);
            snapshot.ExecutingCount = batch.Items.Count(i => i.Status == CargoTaskStatus.Executing);
            snapshot.CompletedCount = batch.CompletedCount;
            snapshot.NeedsReviewCount = batch.Items.Count(i => i.Status == CargoTaskStatus.NeedsReview);
            return snapshot;
        }

        string CargoStageText(TaskStatus status)
        {
            switch (status)
            {
                case TaskStatus.None: return "等待派车";
                case TaskStatus.GoParkingExit: return "车辆正在驶出停车区";
                case TaskStatus.GoPickEntrance: return "前往取货巷道入口";
                case TaskStatus.GoPick: return "进入巷道，前往取货位";
                case TaskStatus.Picking: return "正在取货";
                case TaskStatus.Picked: return "已取货，等待目标巷道准入和卸货位分配";
                case TaskStatus.GoPickExit: return "卸货位已确定，正在驶出取货巷道";
                case TaskStatus.GoDropEntrance: return "前往卸货巷道入口";
                case TaskStatus.GoDrop: return "进入巷道，前往卸货位";
                case TaskStatus.Dropping: return "正在卸货";
                case TaskStatus.Dropped: return "货物已放下，车辆尚未退出";
                case TaskStatus.GoDropExit: return "货物已放下，车辆正在退出卸货巷道";
                case TaskStatus.ExitedDrop: return "已退出卸货巷道，优先续接取货，否则安排停车";
                case TaskStatus.GoHome: return "搬运已结束，车辆正在返回停车位";
                case TaskStatus.Finished: return "任务完成，车辆已续单或就位";
                case TaskStatus.GoMove: return "车辆正在前往指定站点";
                case TaskStatus.NeedsReview: return "已暂停，需要现场核对";
                default: return "未知执行阶段";
            }
        }

        // 创建只建立内存清单；后续由调度循环激活，不在创建函数中派车。
        // 对外参数为巷道子分类名称，例如 "01"、"02"，不是站点 Mark。
        public CargoBatchCreateResult CreatePocketMoveBatch(IEnumerable<string> sourcePocketNames,
            IEnumerable<string> targetPocketNames)
        {
            lock (cargoBatchSync)
            {
                if (HasOpenCargoBatch())
                    return RejectCargoBatch("当前搬运批次尚未结束，不能重复创建或创建其他批次");
                if (TaskInfos.Any(t => t.Status != TaskStatus.Finished) || PendingClearanceBatches.Count > 0)
                    return RejectCargoBatch("当前已有执行任务或清障队列，请完成后再创建批次");
                if (!TryResolveBatchPockets(sourcePocketNames, out var sourceNames, out var sourceKeys, out var error))
                    return RejectCargoBatch("来源：" + error);
                if (!TryResolveBatchPockets(targetPocketNames, out var targetNames, out var targetKeys, out error))
                    return RejectCargoBatch("目标：" + error);
                if (sourceKeys.Intersect(targetKeys).Any())
                    return RejectCargoBatch("来源和目标列表不能包含同一条物理巷道");

                var batch = new CargoMoveBatch
                {
                    MoveType = CargoMoveType.PocketToPocket,
                    SourceScopeId = string.Join(",", sourceNames),
                    TargetScopeId = string.Join(",", targetNames),
                    SourcePocketNames = sourceNames,
                    TargetPocketNames = targetNames,
                    SourcePocketKeys = sourceKeys,
                    TargetPocketKeys = targetKeys,
                    TargetPocketKey = targetKeys.Count == 1 ? targetKeys[0] : null
                };
                foreach (var sourceKey in sourceKeys)
                {
                    var cargoStations = GetCargoPocket(sourceKey).Slots.Where(s => !s.IsEmpty()).Reverse().ToList();
                    for (var i = 0; i < cargoStations.Count; i++)
                    {
                        if (!TryGetStationCargo(cargoStations[i].Mark, out var cargo, out var cargoError))
                            return RejectCargoBatch(cargoError);
                        batch.Items.Add(new CargoMoveItem
                        {
                            CargoName = cargo.Info.Name,
                            CargoTag = Convert.ToString(cargo.Tag),
                            TargetPocketNames = targetNames.ToList(),
                            BatchId = batch.BatchId,
                            SourcePocketKey = sourceKey,
                            SourceMark = cargoStations[i].Mark,
                            PickSequence = i + 1,
                            WaitReason = i == 0 ? "等待调度" : "等待外侧货物搬离"
                        });
                    }
                }
                if (batch.TotalCount == 0) return RejectCargoBatch("所有来源巷道均没有待搬运货物");
                var capacities = GetTargetCapacities(batch);
                var available = capacities.Sum(c => c.AvailableCount);
                if (available < batch.TotalCount)
                {
                    var rejected = RejectCargoBatch($"目标容量不足：待搬 {batch.TotalCount} 件，可用容量 {available} 件，缺少 {batch.TotalCount - available} 个货位");
                    rejected.RequiredCount = batch.TotalCount;
                    rejected.TargetCapacities = capacities;
                    return rejected;
                }
                cargoMoveBatches.Add(batch);
                return new CargoBatchCreateResult
                {
                    Success = true, Batch = batch, RequiredCount = batch.TotalCount, TargetCapacities = capacities
                };
            }
        }

        bool TryResolveBatchPockets(IEnumerable<string> input, out List<string> names,
            out List<string> keys, out string error)
        {
            names = new List<string>();
            keys = new List<string>();
            error = null;
            if (input == null) { error = "巷道列表不能为空"; return false; }
            foreach (var value in input.ToList())
            {
                var name = value?.Trim();
                if (!TryResolveCargoPocket(name, out var key, out error)) return false;
                if (keys.Contains(key)) continue; // 按物理巷道去重，保留首次出现的顺序。
                names.Add(name);
                keys.Add(key);
            }
            if (keys.Count > 0) return true;
            error = "巷道列表不能为空";
            return false;
        }

        List<CargoPocketCapacity> GetTargetCapacities(CargoMoveBatch batch)
        {
            return batch.TargetPocketKeys.Select((key, index) => new CargoPocketCapacity
            {
                PocketName = batch.TargetPocketNames[index],
                PocketKey = key,
                ReachableEmptyCount = DropSlots(new TaskInfo { DropPocketKey = key }).Count,
                ReservedCount = TaskInfos.Count(t => t.DropCapacityReserved && t.DropPocketKey == key)
            }).ToList();
        }
        CargoBatchCreateResult RejectCargoBatch(string reason)
        {
            Tools.trace($"[批次创建失败] {reason}");
            return new CargoBatchCreateResult { Success = false, Error = reason };
        }

        void SetCargoItemWaiting(CargoMoveItem item, string reason)
        {
            if (item.WaitReason == reason) return;
            item.WaitReason = reason;
            Tools.trace($"[任务等待] 第{item.PickSequence}项（取货位 {item.SourceMark}）：{reason}");
        }

        void ReviewCargoItem(CargoMoveBatch batch, CargoMoveItem item, string reason)
        {
            batch.Status = CargoBatchStatus.NeedsReview;
            item.Status = CargoTaskStatus.NeedsReview;
            SetCargoItemWaiting(item, reason);
        }

        bool ValidateBatchPickStation(TaskInfo info)
        {
            if (string.IsNullOrEmpty(info.CargoItemId)) return true;
            var batch = cargoMoveBatches.FirstOrDefault(b => b.BatchId == info.CargoBatchId);
            var item = batch?.Items.FirstOrDefault(i => i.ItemId == info.CargoItemId);
            var station = API.GetStation(info.FromMark);
            if (item != null && station != null && !station.IsEmpty()) return true;
            // 已派车后不擅自释放资源或重发指令，留待现场核对。
            info.Status = TaskStatus.NeedsReview;
            WaitReason(info, "任务明细丢失、来源站点不存在或已无货，暂停取货");
            if (item != null) ReviewCargoItem(batch, item, info.LastWaitReason);
            return false;
        }

        // 仅在 ExecuteMoveTasks 的同一临界区调用，不与执行器并行修改资源。
        void DispatchCargoBatch(IAgv preferredAgv = null)
        {
            var batch = cargoMoveBatches.FirstOrDefault(b => b.Status != CargoBatchStatus.Completed);
            if (batch == null || batch.Status == CargoBatchStatus.NeedsReview || batch.MoveType == CargoMoveType.PointToPoint) return;
            var start = batch.NextSourceIndex;
            for (var offset = 0; offset < batch.SourcePocketKeys.Count; offset++)
            {
                var index = (start + offset) % batch.SourcePocketKeys.Count;
                var key = batch.SourcePocketKeys[index];
                var item = batch.Items.Where(i => i.SourcePocketKey == key && i.Status == CargoTaskStatus.Waiting)
                    .OrderBy(i => i.PickSequence).FirstOrDefault();
                if (item == null) continue;
                DispatchCargoItem(batch, item, preferredAgv);
                if (item.Status == CargoTaskStatus.Executing)
                    batch.NextSourceIndex = (index + 1) % batch.SourcePocketKeys.Count;
                if (preferredAgv != null && TaskInfos.Any(t => t.WorkAgv == preferredAgv &&
                    t.Status != TaskStatus.Finished)) return;
                if (batch.Status == CargoBatchStatus.NeedsReview) return;
                if (TaskInfos.Count(t => t.Status != TaskStatus.Finished) >= MaxParallelMoveTask) return;
            }
        }

        void DispatchCargoItem(CargoMoveBatch batch, CargoMoveItem item, IAgv preferredAgv = null)
        {
            if (!string.IsNullOrEmpty(item.ExecutionTaskId))
            {
                ReviewCargoItem(batch, item, "等待明细已有执行关联，需核对，禁止重复下发");
                return;
            }
            var sourceStation = API.GetStation(item.SourceMark);
            if (!IsExpectedCargoAtStation(item.SourceMark, item.CargoName, item.CargoTag))
            {
                ReviewCargoItem(batch, item, "指定货物不在计划源货位，需核对");
                return;
            }
            if (sourceStation == null || sourceStation.IsEmpty())
            {
                ReviewCargoItem(batch, item, "来源站点不存在或已无货，暂停批次");
                return;
            }
            var sourcePocket = GetCargoPocket(item.SourcePocketKey);
            var entrances = GetMapCategory("入口");
            if (sourcePocket == null || sourcePocket.Error != null || entrances == null)
            {
                ReviewCargoItem(batch, item, "来源巷道或入口配置丢失");
                return;
            }
            var sourceStations = sourcePocket.Stations;
            var sourceIndex = sourceStations.FindIndex(s => s.Mark == item.SourceMark);
            if (sourceIndex < 0)
            {
                ReviewCargoItem(batch, item, "来源货位已不属于原巷道");
                return;
            }
            var gates = entrances.Marks;
            if (sourceStations.Skip(sourceIndex + 1).Any(s => !gates.Contains(s.Mark) && !s.IsEmpty()))
            {
                SetCargoItemWaiting(item, "等待外侧货物搬离");
                return;
            }
            if (PocketLocks.ContainsKey(item.SourcePocketKey))
            {
                SetCargoItemWaiting(item, "等待来源巷道释放");
                return;
            }
            if (TaskInfos.Count(t => t.Status != TaskStatus.Finished) >= MaxParallelMoveTask)
            {
                SetCargoItemWaiting(item, "等待执行任务名额");
                return;
            }
            var info = new TaskInfo
            {
                CargoBatchId = batch.BatchId,
                CargoItemId = item.ItemId,
                CargoName = item.CargoName,
                CargoTag = item.CargoTag,
                FromMark = item.SourceMark,

                DynamicDrop = true,
                RequestedAgv = "00",
                LastWaitReason = item.WaitReason,
                TaskType = TaskTypeEnum.移库任务
            };
            // 按目标列表顺序填充；在途预留计入容量，通行锁忙不代表没有容量。
            foreach (var key in batch.TargetPocketKeys)
            {
                info.DropPocketKey = key;
                if (PrepareEntrances(info) && ReserveDropCapacity(info)) break;
            }
            if (!info.DropCapacityReserved)
            {
                SetCargoItemWaiting(item, "所有目标巷道均无可用容量或配置异常，等待重新检查");
                return;
            }
            info.LastWaitReason = null;
            var agv = FindCargoAgv(info, preferredAgv);
            if (agv == null)
            {
                // 尚未加入 TaskInfos，本次临时容量检查不构成持久预留。
                info.DropCapacityReserved = false;
                SetCargoItemWaiting(item, "等待可用车辆");
                return;
            }
            info.WorkAgv = agv;
            info.LockPickPocketKey = item.SourcePocketKey;
            PocketLocks[item.SourcePocketKey] = info;
            TaskInfos.Add(info);
            item.ExecutionSteps.Add(info);
            item.ExecutionTaskId = info.TaskId;
            item.AssignedDropPocketKey = info.DropPocketKey;
            item.AssignedAgv = agv.Name;
            item.Status = CargoTaskStatus.Executing;
            item.WaitReason = null;
            batch.Status = CargoBatchStatus.Running;
            try
            {
                StartFromParking(info, info.PickEntranceMark, TaskStatus.GoPickEntrance);
            }
            catch (Exception ex)
            {
                info.Status = TaskStatus.NeedsReview;
                info.LastWaitReason = "派车结果不确定，需核对：" + ex.Message;
                ReviewCargoItem(batch, item, info.LastWaitReason);
            }
            item.ExecutionStatus = info.Status;
            Tools.trace($"[任务进度] 来源 {item.SourcePocketKey} 第{item.PickSequence}项｜车辆 {agv.Name}｜取货位 {item.SourceMark} → 目标巷道 {batch.TargetPocketNames[batch.TargetPocketKeys.IndexOf(info.DropPocketKey)]}｜卸货位待分配｜{CargoStageText(info.Status)}");
        }

        void SyncCargoBatchProgress()
        {
            foreach (var batch in cargoMoveBatches.Where(b => b.Status != CargoBatchStatus.Completed))
            {
                foreach (var item in batch.Items.Where(i => !string.IsNullOrEmpty(i.ExecutionTaskId) && i.Status != CargoTaskStatus.Completed))
                {
                    var finalStep = item.ExecutionSteps.LastOrDefault();
                    var info = item.ExecutionSteps.FirstOrDefault(t => t.Status != TaskStatus.Finished) ?? finalStep;
                    if (info == null)
                    {
                        ReviewCargoItem(batch, item, "关联执行任务丢失，需核对");
                        continue;
                    }
                    var changed = item.ExecutionStatus != info.Status || item.AssignedDropMark != finalStep?.ToMark || item.AssignedAgv != info.WorkAgv?.Name || item.WaitReason != info.LastWaitReason;
                    item.ExecutionStatus = info.Status;
                    item.AssignedDropMark = finalStep?.ToMark;
                    item.AssignedAgv = info.WorkAgv?.Name;
                    item.WaitReason = info.LastWaitReason;
                    if (info.Status == TaskStatus.NeedsReview)
                        ReviewCargoItem(batch, item, info.LastWaitReason);
                    else if (item.ExecutionSteps.Count > 0 && item.ExecutionSteps.All(s => s.Status == TaskStatus.Finished))
                    {
                        item.Status = CargoTaskStatus.Completed;
                        item.CompletedTime = DateTime.Now;
                        item.WaitReason = null;
                    }
                    if (changed)
                        Tools.trace($"[任务进度] 货物 {item.CargoName}/{item.CargoTag}｜当前步骤 {info.StepType}，搬运 {info.CargoName}/{info.CargoTag}｜车辆 {item.AssignedAgv}｜{info.FromMark} → {info.ToMark ?? "待分配"}｜{CargoStageText(info.Status)}{(string.IsNullOrEmpty(item.WaitReason) ? "" : "｜等待原因：" + item.WaitReason)}");
                    if (info.Status == TaskStatus.Finished)
                        Tools.trace($"[整批进度] 已完成 {batch.CompletedCount}/{batch.TotalCount} 项（车辆已续接任务或回到停车位）");
                }
                if (batch.TotalCount > 0 && batch.CompletedCount == batch.TotalCount)
                {
                    batch.Status = CargoBatchStatus.Completed;
                    batch.CompletedTime = DateTime.Now;
                    Tools.trace($"[批次完成] {batch.MoveType}：{batch.SourceScopeId} → {batch.TargetScopeId}，全部 {batch.TotalCount} 项已完成，车辆已续接任务或回到停车位。批次编号：{batch.BatchId}");
                    foreach (var finished in batch.Items.OrderBy(i => i.PickSequence))
                        Tools.trace($"[搬运结果] 第{finished.PickSequence}项，货物 {finished.CargoName}/{finished.CargoTag}：取货位 {finished.SourceMark} → 卸货位 {finished.AssignedDropMark}，执行车辆 {finished.AssignedAgv}");
                }
            }
        }

        // 仅调用公开查询函数，验证调用方实际能取得的数据，不读取内部任务池。
        void TestCargoBatchQueries()
        {
            Tools.trace("[查询测试] 开始读取批次列表和详情");
            foreach (var batch in GetBatches())
            {
                Tools.trace($"[列表查询] 批次 {batch.BatchId}，{batch.MoveType}：{batch.SourceScopeId} → {batch.TargetScopeId}｜{batch.StatusText}｜总数 {batch.TotalCount}，等待 {batch.WaitingCount}，执行中 {batch.ExecutingCount}，完成 {batch.CompletedCount}，待核对 {batch.NeedsReviewCount}");
                var detail = GetBatchDetail(batch.BatchId);
                if (detail == null) continue;
                foreach (var item in detail.Items)
                {
                    Tools.trace($"[详情查询] 货物={item.CargoName}/{item.CargoTag}｜起点={item.SourceMark}｜目标货位={item.TargetMark ?? "未指定"}｜目标巷道={string.Join(",", item.TargetPocketNames)}｜实际卸货位={item.AssignedDropMark ?? "待分配"}｜{item.StatusText}｜步骤={item.CompletedStepCount}/{item.TotalStepCount}｜原因={item.WaitReason ?? "无"}");
                    foreach (var step in item.Steps)
                        Tools.trace($"[步骤查询] 第{step.Sequence}步 {step.StepType}｜货物={step.CargoName}/{step.CargoTag}｜{step.FromMark} -> {step.ToMark ?? "待分配"}｜AGV={step.AgvName ?? "待分配"}｜{step.StatusText}｜原因={step.WaitReason ?? "无"}");
                }
            }
        }

        // 初始化测试直接传巷道子分类名称。
        void TestPocketMoveBatchCreation(IEnumerable<string> sourcePocketNames, IEnumerable<string> targetPocketNames)
        {
            var result = CreatePocketMoveBatch(sourcePocketNames, targetPocketNames);
            if (!result.Success) return;
            var batch = result.Batch;
            Tools.trace($"[批次创建] 共 {batch.TotalCount} 项，来源巷道 {batch.SourceScopeId} → 目标巷道 {batch.TargetScopeId}（均为子分类名称）。批次编号：{batch.BatchId}");
            foreach (var item in batch.Items)
                Tools.trace($"[待搬清单] 来源 {item.SourcePocketKey} 第{item.PickSequence}项：取货位 {item.SourceMark} → 目标巷道（标识 {batch.TargetScopeId}），卸货位待分配；{item.WaitReason}");
            Tools.trace("[调度准备] 清单已生成，接下来自动按取货条件派车；具体卸货位稍后逐条分配。");
        }
    }
}
