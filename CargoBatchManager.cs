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
                        CargoId = i.CargoId,
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
                case TaskStatus.ExitedDrop: return "已退出卸货巷道，等待安排停车";
                case TaskStatus.GoHome: return "搬运已结束，车辆正在返回停车位";
                case TaskStatus.Finished: return "任务完成，车辆已就位";
                case TaskStatus.GoMove: return "车辆正在前往指定站点";
                case TaskStatus.NeedsReview: return "已暂停，需要现场核对";
                default: return "未知执行阶段";
            }
        }

        // 创建只建立内存清单；后续由调度循环激活，不在创建函数中派车。
        // 对外参数为巷道子分类名称，例如 "01"、"02"，不是站点 Mark。
        public CargoBatchCreateResult CreatePocketMoveBatch(string sourcePocketName, string targetPocketName)
        {
            lock (cargoBatchSync)
            {
                if (!TryResolveCargoPocket(sourcePocketName, out var sourceKey, out var sourceError))
                    return RejectCargoBatch(sourceError);
                if (!TryResolveCargoPocket(targetPocketName, out var targetKey, out var targetError))
                    return RejectCargoBatch(targetError);
                return CreatePocketMoveBatchCore(sourceKey, targetKey, sourcePocketName, targetPocketName);
            }
        }

        bool TryResolveCargoPocket(string name, out string key, out string error)
        {
            key = null;
            error = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "巷道子分类名称不能为空";
                return false;
            }
            var category = SubCategoryCollection.Instance[name] as ISubCategory;
            var garage = SubCategoryCollection.Instance[GarageArea] as ISubCategory;
            var entrances = SubCategoryCollection.Instance["入口"] as ISubCategory;
            if (category == null || garage == null || entrances == null)
            {
                error = $"找不到巷道子分类 {name}，或缺少车库位、入口分类";
                return false;
            }
            var marks = new HashSet<string>(category.Children.OfType<IStation>().Select(s => s.Mark));
            var garageMarks = new HashSet<string>(garage.Children.OfType<IStation>().Select(s => s.Mark));
            var gates = new HashSet<string>(entrances.Children.OfType<IStation>().Select(s => s.Mark));
            var stations = category.Children.OfType<IStation>()
                .Where(s => garageMarks.Contains(s.Mark) && !gates.Contains(s.Mark)).ToList();
            var pockets = stations.SelectMany(s => s.BelongPocketLines).Distinct().ToList();
            if (stations.Count == 0 || pockets.Count != 1)
            {
                error = $"子分类 {name} 必须对应一条明确的货物巷道；当前不能将多条物理巷道当作一条处理";
                return false;
            }
            // 不允许只选了半条巷道，却把分类之外的货物也搬走或放入分类之外。
            if (pockets[0].OrderStations.Any(s => garageMarks.Contains(s.Mark) && !gates.Contains(s.Mark) && !marks.Contains(s.Mark)))
            {
                error = $"子分类 {name} 未包含该巷道的全部车库货位，请检查分类配置";
                return false;
            }
            key = pockets[0].OrderStations.FirstOrDefault()?.Mark;
            if (string.IsNullOrEmpty(key))
            {
                error = $"子分类 {name} 对应的巷道没有站点";
                return false;
            }
            return true;
        }

        // 站点 Key 只在内部沿用，保持巷道锁、容量预留的现有实现。
        CargoBatchCreateResult CreatePocketMoveBatchCore(string sourcePocketKey, string targetPocketKey,
            string sourcePocketName, string targetPocketName)
        {
            lock (cargoBatchSync)
            {
                if (string.IsNullOrWhiteSpace(sourcePocketKey) || string.IsNullOrWhiteSpace(targetPocketKey))
                    return RejectCargoBatch("来源和目标巷道 Key 不能为空");
                if (sourcePocketKey == targetPocketKey)
                    return RejectCargoBatch("来源和目标不能是同一巷道");
                if (HasOpenCargoBatch())
                    return RejectCargoBatch("当前搬运批次尚未结束，不能重复创建或创建其他批次");
                if (TaskInfos.Any(t => t.Status != TaskStatus.Finished) || PendingClearanceBatches.Count > 0)
                    return RejectCargoBatch("当前已有执行任务或清障队列，请完成后再创建批次");

                var sourcePockets = API.GetStation(sourcePocketKey)?.BelongPocketLines
                    .Where(p => p.OrderStations.FirstOrDefault()?.Mark == sourcePocketKey).ToList();
                var targetPockets = API.GetStation(targetPocketKey)?.BelongPocketLines
                    .Where(p => p.OrderStations.FirstOrDefault()?.Mark == targetPocketKey).ToList();
                if (sourcePockets == null || sourcePockets.Count != 1 ||
                    targetPockets == null || targetPockets.Count != 1)
                    return RejectCargoBatch("找不到唯一的来源或目标巷道，请使用巷道首站 Mark");

                var garage = SubCategoryCollection.Instance[GarageArea] as ISubCategory;
                var entrances = SubCategoryCollection.Instance["入口"] as ISubCategory;
                if (garage == null || entrances == null)
                    return RejectCargoBatch("缺少车库位或入口分类");
                var garageMarks = new HashSet<string>(garage.Children.OfType<IStation>().Select(s => s.Mark));
                var entranceMarks = new HashSet<string>(entrances.Children.OfType<IStation>().Select(s => s.Mark));
                var sourceStations = sourcePockets[0].OrderStations.ToList();
                var targetStations = targetPockets[0].OrderStations.ToList();
                if (sourceStations.Where(s => entranceMarks.Contains(s.Mark)).Select(s => s.Mark).Distinct().Count() != 1 ||
                    targetStations.Where(s => entranceMarks.Contains(s.Mark)).Select(s => s.Mark).Distinct().Count() != 1)
                    return RejectCargoBatch("来源和目标巷道必须各有一个入口");
                if (!targetStations.Any(s => garageMarks.Contains(s.Mark) && !entranceMarks.Contains(s.Mark)))
                    return RejectCargoBatch("目标巷道没有车库货位");

                var cargoStations = sourceStations
                    .Where(s => garageMarks.Contains(s.Mark) && !entranceMarks.Contains(s.Mark) && !s.IsEmpty())
                    .Reverse().ToList();
                if (cargoStations.Count == 0)
                    return RejectCargoBatch("来源巷道没有待搬运货物");
                if (cargoStations.Select(s => s.Mark).Distinct().Count() != cargoStations.Count)
                    return RejectCargoBatch("来源巷道包含重复货位，请检查地图配置");

                var batch = new CargoMoveBatch
                {
                    MoveType = CargoMoveType.PocketToPocket,
                    SourceScopeId = sourcePocketName,
                    TargetScopeId = targetPocketName,
                    TargetPocketKey = targetPocketKey
                };
                for (var i = 0; i < cargoStations.Count; i++)
                {
                    batch.Items.Add(new CargoMoveItem
                    {
                        BatchId = batch.BatchId,
                        SourcePocketKey = sourcePocketKey,
                        SourceMark = cargoStations[i].Mark,
                        PickSequence = i + 1,
                        // 示例货物尚无已确认的唯一编号，不用站点号冒充 CargoId。
                        WaitReason = i == 0 ? "等待调度" : "等待外侧货物搬离"
                    });
                }
                cargoMoveBatches.Add(batch);
                return new CargoBatchCreateResult { Success = true, Batch = batch };
            }
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
        void DispatchCargoBatch()
        {
            var batch = cargoMoveBatches.FirstOrDefault(b => b.Status != CargoBatchStatus.Completed);
            if (batch == null || batch.Status == CargoBatchStatus.NeedsReview) return;
            var item = batch.Items.Where(i => i.Status == CargoTaskStatus.Waiting)
                .OrderBy(i => i.PickSequence).FirstOrDefault();
            if (item == null) return;
            if (!string.IsNullOrEmpty(item.ExecutionTaskId))
            {
                ReviewCargoItem(batch, item, "等待明细已有执行关联，需核对，禁止重复下发");
                return;
            }
            var sourceStation = API.GetStation(item.SourceMark);
            if (sourceStation == null || sourceStation.IsEmpty())
            {
                ReviewCargoItem(batch, item, "来源站点不存在或已无货，暂停批次");
                return;
            }
            var sourcePocket = API.GetStation(item.SourcePocketKey)?.BelongPocketLines
                .FirstOrDefault(p => p.OrderStations.FirstOrDefault()?.Mark == item.SourcePocketKey);
            var entrances = SubCategoryCollection.Instance["入口"] as ISubCategory;
            if (sourcePocket == null || entrances == null)
            {
                ReviewCargoItem(batch, item, "来源巷道或入口配置丢失");
                return;
            }
            var sourceStations = sourcePocket.OrderStations.ToList();
            var sourceIndex = sourceStations.FindIndex(s => s.Mark == item.SourceMark);
            if (sourceIndex < 0)
            {
                ReviewCargoItem(batch, item, "来源货位已不属于原巷道");
                return;
            }
            var gates = new HashSet<string>(entrances.Children.OfType<IStation>().Select(s => s.Mark));
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
                FromMark = item.SourceMark,
                DropPocketKey = batch.TargetPocketKey,
                DynamicDrop = true,
                RequestedAgv = "00",
                LastWaitReason = item.WaitReason,
                TaskType = TaskTypeEnum.移库任务
            };
            if (!PrepareEntrances(info) || !ReserveDropCapacity(info))
            {
                SetCargoItemWaiting(item, info.LastWaitReason);
                return;
            }
            var agv = FindCargoAgv(info);
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
            Tools.trace($"[任务进度] 第{item.PickSequence}/{batch.TotalCount}项｜车辆 {agv.Name}｜取货位 {item.SourceMark} → 目标巷道 {batch.TargetScopeId}｜卸货位待分配｜{CargoStageText(info.Status)}");
        }

        void SyncCargoBatchProgress()
        {
            foreach (var batch in cargoMoveBatches.Where(b => b.Status != CargoBatchStatus.Completed))
            {
                foreach (var item in batch.Items.Where(i => !string.IsNullOrEmpty(i.ExecutionTaskId) && i.Status != CargoTaskStatus.Completed))
                {
                    var info = TaskInfos.FirstOrDefault(t => t.TaskId == item.ExecutionTaskId);
                    if (info == null)
                    {
                        ReviewCargoItem(batch, item, "关联执行任务丢失，需核对");
                        continue;
                    }
                    var changed = item.ExecutionStatus != info.Status || item.AssignedDropMark != info.ToMark || item.WaitReason != info.LastWaitReason;
                    item.ExecutionStatus = info.Status;
                    item.AssignedDropMark = info.ToMark;
                    item.AssignedAgv = info.WorkAgv?.Name;
                    item.WaitReason = info.LastWaitReason;
                    if (info.Status == TaskStatus.NeedsReview)
                        ReviewCargoItem(batch, item, info.LastWaitReason);
                    else if (info.Status == TaskStatus.Finished)
                    {
                        item.Status = CargoTaskStatus.Completed;
                        item.CompletedTime = DateTime.Now;
                        item.WaitReason = null;
                    }
                    if (changed)
                        Tools.trace($"[任务进度] 第{item.PickSequence}/{batch.TotalCount}项｜车辆 {item.AssignedAgv}｜取货位 {item.SourceMark} → 卸货位 {item.AssignedDropMark ?? "待分配"}｜{CargoStageText(info.Status)}{(string.IsNullOrEmpty(item.WaitReason) ? "" : "｜等待原因：" + item.WaitReason)}");
                    if (info.Status == TaskStatus.Finished)
                        Tools.trace($"[整批进度] 已完成 {batch.CompletedCount}/{batch.TotalCount} 项（完成包含车辆回到停车位）");
                }
                if (batch.TotalCount > 0 && batch.CompletedCount == batch.TotalCount)
                {
                    batch.Status = CargoBatchStatus.Completed;
                    batch.CompletedTime = DateTime.Now;
                    Tools.trace($"[批次完成] 来源巷道（标识 {batch.SourceScopeId}）→ 目标巷道（标识 {batch.TargetScopeId}），全部 {batch.TotalCount} 项已完成，车辆已回到停车位。批次编号：{batch.BatchId}");
                    foreach (var finished in batch.Items.OrderBy(i => i.PickSequence))
                        Tools.trace($"[搬运结果] 第{finished.PickSequence}项：取货位 {finished.SourceMark} → 卸货位 {finished.AssignedDropMark}，执行车辆 {finished.AssignedAgv}");
                }
            }
        }

        // 仅调用公开查询函数，验证调用方实际能取得的数据，不读取内部任务池。
        void TestCargoBatchQueries()
        {
            Tools.trace("[查询测试] 开始读取批次列表和详情");
            foreach (var batch in GetBatches())
            {
                Tools.trace($"[列表查询] 巷道 {batch.SourceScopeId} → {batch.TargetScopeId}｜{batch.StatusText}｜总数 {batch.TotalCount}，等待 {batch.WaitingCount}，执行中 {batch.ExecutingCount}，完成 {batch.CompletedCount}，待核对 {batch.NeedsReviewCount}");
                var detail = GetBatchDetail(batch.BatchId);
                if (detail == null) continue;
                foreach (var item in detail.Items)
                    Tools.trace($"[详情查询] 第{item.PickSequence}/{detail.TotalCount}项｜取货位 {item.SourceMark} → 卸货位 {item.AssignedDropMark ?? "待分配"}｜车辆 {item.AssignedAgv ?? "待分配"}｜{item.StatusText}｜执行阶段：{item.ExecutionStatusText ?? "尚未开始"}｜等待原因：{item.WaitReason ?? "无"}");
            }
        }

        // 初始化测试直接传巷道子分类名称。
        void TestPocketMoveBatchCreation(string sourcePocketName, string targetPocketName)
        {
            var result = CreatePocketMoveBatch(sourcePocketName, targetPocketName);
            if (!result.Success) return;
            var batch = result.Batch;
            Tools.trace($"[批次创建] 共 {batch.TotalCount} 项，来源巷道 {batch.SourceScopeId} → 目标巷道 {batch.TargetScopeId}（均为子分类名称）。批次编号：{batch.BatchId}");
            foreach (var item in batch.Items)
                Tools.trace($"[待搬清单] 第{item.PickSequence}/{batch.TotalCount}项：取货位 {item.SourceMark} → 目标巷道（标识 {batch.TargetScopeId}），卸货位待分配；{item.WaitReason}");
            Tools.trace("[调度准备] 清单已生成，接下来自动按取货条件派车；具体卸货位稍后逐条分配。");
        }
    }
}
