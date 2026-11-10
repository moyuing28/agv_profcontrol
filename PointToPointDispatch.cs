namespace ProfControl
{
    public partial class SampleProject
    {
        // 货物已经落位的步骤不再阻止重新准备；其 AGV 和锁仍按原状态机收尾。
        bool PointMoveInProgress(TaskInfo info) => info.Status != TaskStatus.Finished &&
            (!info.PointAction.HasValue || !info.PointDropCompleted);

        void DispatchPointToPoint(IAgv preferredAgv = null)
        {
            // 所有调用均在 cargoBatchSync 内，候选每次成功操作后重建，不能重复使用旧清单。
            var batch = cargoMoveBatches.FirstOrDefault(b => b.Status != CargoBatchStatus.Completed);
            if (batch == null || batch.MoveType != CargoMoveType.PointToPoint) return;
            while (batch.Status != CargoBatchStatus.Completed && batch.Status != CargoBatchStatus.NeedsReview)
            {
                SyncPointBatchProgress(batch);
                if (batch.Status == CargoBatchStatus.Completed || batch.Status == CargoBatchStatus.NeedsReview) return;
                var preview = GetPointToPointCandidates(batch.BatchId);
                foreach (var task in preview.Tasks.Where(t => t.CandidateCount == 0))
                {
                    var item = batch.Items.First(i => i.ItemId == task.ItemId);
                    if (item.Status == CargoTaskStatus.Waiting) item.WaitReason = task.WaitReason;
                }
                var progressed = false;
                foreach (var candidate in preview.Candidates)
                {
                    var item = batch.Items.First(i => i.ItemId == candidate.ItemId);
                    if (candidate.ActionType == PointActionType.FINALIZE)
                    {
                        // 再做一次实时确认；零搬运不占并行名额，不需要车辆。
                        var confirmation = GetPointToPointCandidates(batch.BatchId).Candidates.FirstOrDefault(c =>
                            c.ItemId == candidate.ItemId && c.ActionType == PointActionType.FINALIZE);
                        if (confirmation == null) continue;
                        CompletePointCustomer(batch, item, PointActionType.FINALIZE);
                        progressed = true;
                        break;
                    }
                    if (TaskInfos.Count(t => t.Status != TaskStatus.Finished) >= MaxParallelMoveTask)
                    {
                        item.WaitReason = "等待执行任务名额";
                        continue; // 名额已满仍要扫描后面的 FINALIZE。
                    }
                    if (!TryDispatchPointMove(batch, item, candidate, preferredAgv, out var error))
                    {
                        item.WaitReason = error;
                        continue;
                    }
                    progressed = true;
                    if (preferredAgv != null) return;
                    break;
                }
                if (!progressed) return;
            }
        }

        bool TryDispatchPointMove(CargoMoveBatch batch, CargoMoveItem item, PointActionCandidate candidate,
            IAgv preferredAgv, out string error)
        {
            error = null;
            if (PocketLocks.ContainsKey(candidate.SourcePocketKey))
            {
                error = "等待来源巷道释放";
                return false;
            }
            if (!TryBuildPointMove(batch, item, candidate.ActionType, candidate.Phase, candidate.CargoName,
                candidate.CargoTag, candidate.ToMark, candidate.Reason, out var current, out error)) return false;
            if (current.FromMark != candidate.FromMark)
            {
                error = "货物位置已变化，等待重新生成候选";
                return false;
            }
            var info = new TaskInfo
            {
                CargoBatchId = batch.BatchId, CargoItemId = item.ItemId, PointAction = candidate.ActionType,
                CargoName = candidate.CargoName, CargoTag = candidate.CargoTag,
                FromMark = candidate.FromMark, ToMark = candidate.ToMark,
                DropPocketKey = candidate.TargetPocketKey, DynamicDrop = false,
                RequestedAgv = item.RequestedAgv, TaskType = TaskTypeEnum.移库任务,
                StepType = candidate.ActionType == PointActionType.CUSTOMER_MOVE ? CargoStepType.TargetMove :
                    candidate.ActionType == PointActionType.FILLING ? CargoStepType.Filling :
                    (candidate.CargoName == item.CargoName && candidate.CargoTag == item.CargoTag ? CargoStepType.Staging : CargoStepType.Clearance)
            };
            if (!PrepareEntrances(info))
            {
                error = info.LastWaitReason;
                return false;
            }
            var agv = FindPointAgv(info, preferredAgv);
            if (agv == null)
            {
                error = "等待可用车辆及可达路线（检查指定车辆及停车出库）";
                return false;
            }
            // 容量标记只在加入 TaskInfos 后生效；资源预约必须整组成功。
            if (!ReserveDropCapacity(info) || !TryReservePointMove(info, out error))
            {
                error = error ?? info.LastWaitReason;
                info.DropCapacityReserved = false;
                return false;
            }
            info.WorkAgv = agv;
            info.LockPickPocketKey = candidate.SourcePocketKey;
            PocketLocks[info.LockPickPocketKey] = info;
            TaskInfos.Add(info);
            item.ExecutionSteps.Add(info);
            item.ExecutionTaskId = info.TaskId;
            item.Status = CargoTaskStatus.Executing;
            item.ExecutionStatus = info.Status;
            item.AssignedAgv = agv.Name;
            item.AssignedDropPocketKey = info.DropPocketKey;
            item.AssignedDropMark = info.ToMark;
            item.WaitReason = null;
            batch.Status = CargoBatchStatus.Running;
            try
            {
                StartFromParking(info, info.PickEntranceMark, TaskStatus.GoPickEntrance);
                item.ExecutionStatus = info.Status;
                Tools.trace($"[点到点派发] 客户={item.TaskNo}, 动作={info.PointAction}, 搬运={info.TaskId}, " +
                    $"货物={info.CargoName}/{info.CargoTag}, {info.FromMark} -> {info.ToMark}, AGV={agv.Name}");
            }
            catch (Exception ex)
            {
                // 指令可能已发出，禁止回滚预约或重新派发。
                ReviewPointExecution(info, "派车结果不确定，需现场核对：" + ex.Message);
            }
            return true;
        }

        IAgv FindPointAgv(TaskInfo info, IAgv preferredAgv)
        {
            var bestDistance = double.PositiveInfinity;
            IAgv best = null;
            if (!TryPointRouteDistance(new[] { info.FromMark, info.PickEntranceMark, info.DropEntranceMark, info.ToMark },
                out _)) return null;
            foreach (var agv in API.按条件找一些Agv(a => (preferredAgv == null || a == preferredAgv) &&
                a.IsFree() && CanLeaveParking(a) && !TaskInfos.Any(t => t.WorkAgv == a && t.Status != TaskStatus.Finished)))
            {
                if ((!string.IsNullOrWhiteSpace(info.RequestedAgv) && info.RequestedAgv != "00" && info.RequestedAgv != agv.Name) ||
                    API.Cargos.Any(c => object.ReferenceEquals(API.GetOwner(c), agv))) continue;
                var start = ParkingPosition(agv);
                if (string.IsNullOrEmpty(start)) continue;
                var route = new List<string> { start };
                var parking = parkingLanes.FirstOrDefault(l => l.Slots.Contains(start));
                if (parking != null) route.Add(parking.Gate);
                route.Add(info.PickEntranceMark);
                route.Add(info.FromMark);
                if (!TryPointRouteDistance(route, out var empty)) continue;
                if (empty < bestDistance || (empty == bestDistance && (best == null || ComparePointStationMarks(agv.Name, best.Name) < 0)))
                {
                    bestDistance = empty;
                    best = agv;
                }
            }
            return best;
        }

        bool TryPointRouteDistance(IEnumerable<string> marks, out double distance)
        {
            distance = 0;
            string previous = null;
            foreach (var mark in marks)
            {
                if (string.IsNullOrEmpty(mark)) return false;
                if (previous != null && previous != mark)
                {
                    if (!TryGetPointLoadedDistance(previous, mark, out var segment, out _)) return false;
                    distance += segment;
                }
                previous = mark;
            }
            return !double.IsInfinity(distance);
        }

        bool CheckPointPickup(TaskInfo info)
        {
            if (!info.PointAction.HasValue) return true;
            string error = null;
            if (!TryGetOwnedPointReservation(info, out var reservation) || reservation.SourceReleased ||
                reservation.FromMark != info.FromMark || reservation.ToMark != info.ToMark ||
                reservation.CargoName != info.CargoName || reservation.CargoTag != info.CargoTag ||
                !OwnsLock(info, info.LockPickPocketKey) || info.WorkAgv == null ||
                API.Cargos.Any(c => object.ReferenceEquals(API.GetOwner(c), info.WorkAgv)))
                error = "取货前预约、源巷道锁、车辆或车载状态不符";
            else if (!CanPointCargoMove(info.CargoName, info.CargoTag, info.ToMark, info, out var source, out error) ||
                source != info.FromMark) error = error ?? "取货前货物实时位置已变化";
            else if (HasUnfinishedPointCargoBelow(info.ToMark, info.CargoName, info.CargoTag, out _))
                error = "取货前目标深处出现未完成客户货物";
            if (error == null) return true;
            ReviewPointExecution(info, error);
            return false;
        }

        bool CheckPointDropoff(TaskInfo info, bool requireLock)
        {
            if (!info.PointAction.HasValue) return true;
            if (!CanDropReservedPointMove(info, out var error))
            {
                ReviewPointExecution(info, error);
                return false;
            }
            if ((requireLock && !OwnsLock(info, info.LockDropPocketKey)) ||
                HasUnfinishedPointCargoBelow(info.ToMark, info.CargoName, info.CargoTag, out _))
            {
                ReviewPointExecution(info, "放货前目标巷道锁或未来客户货物保护检查不符");
                return false;
            }
            return true;
        }

        void PointPickupCompleted(TaskInfo info)
        {
            if (info.PointAction.HasValue && !ReleasePointSourceReservation(info))
                ReviewPointExecution(info, "取货完成但源预约归属不符，保留资源待核对");
        }

        void PointDropoffCompleted(TaskInfo info)
        {
            if (!info.PointAction.HasValue) return;
            var batch = cargoMoveBatches.First(b => b.BatchId == info.CargoBatchId);
            var item = batch.Items.First(i => i.ItemId == info.CargoItemId);
            if (info.PointAction == PointActionType.CUSTOMER_MOVE &&
                (item.CargoName != info.CargoName || item.CargoTag != info.CargoTag || item.TargetMark != info.ToMark))
            {
                ReviewPointExecution(info, "正式搬运与客户要求不符，禁止自动完成");
                return;
            }
            if (!IsExpectedCargoAtStation(info.ToMark, info.CargoName, info.CargoTag) || !ReleasePointMoveReservation(info))
            {
                ReviewPointExecution(info, "放货完成后的货物或预约归属不符，保留资源待核对");
                return;
            }
            info.PointDropCompleted = true;
            info.DropoffCompletedTime = DateTime.Now;
            item.ExecutionStatus = info.Status;
            item.WaitReason = null;
            if (info.PointAction == PointActionType.CUSTOMER_MOVE)
                CompletePointCustomer(batch, item, PointActionType.CUSTOMER_MOVE);
            else item.Status = CargoTaskStatus.Waiting;
        }

        void CompletePointCustomer(CargoMoveBatch batch, CargoMoveItem item, PointActionType action)
        {
            item.Status = CargoTaskStatus.Completed;
            item.CompletedTime = DateTime.Now;
            item.CompletionAction = action;
            item.WaitReason = null;
            item.AssignedDropMark = item.TargetMark;
            item.AssignedDropPocketKey = GetStationPocketKey(item.TargetMark);
            if (batch.Items.All(i => i.Status == CargoTaskStatus.Completed)) batch.CustomerCompletedTime = item.CompletedTime;
            Tools.trace($"[客户完成] {item.TaskNo}, 方式={action}, 最终货位={item.TargetMark}");
        }

        void ReviewPointExecution(TaskInfo info, string reason)
        {
            reason = reason ?? "执行状态不确定，需现场核对";
            info.Status = TaskStatus.NeedsReview;
            WaitReason(info, reason);
            var batch = cargoMoveBatches.FirstOrDefault(b => b.BatchId == info.CargoBatchId);
            if (batch == null) return;
            batch.Status = CargoBatchStatus.NeedsReview;
            var item = batch.Items.FirstOrDefault(i => i.ItemId == info.CargoItemId);
            if (item == null) return;
            if (item.Status != CargoTaskStatus.Completed) item.Status = CargoTaskStatus.NeedsReview;
            item.WaitReason = reason;
            item.ExecutionStatus = info.Status;
        }

        void SyncPointBatchProgress(CargoMoveBatch batch)
        {
            foreach (var failed in TaskInfos.Where(t => t.CargoBatchId == batch.BatchId && t.Status == TaskStatus.NeedsReview))
                ReviewPointExecution(failed, failed.LastWaitReason);
            foreach (var item in batch.Items)
            {
                var info = item.ExecutionSteps.LastOrDefault();
                if (info == null) continue;
                item.ExecutionStatus = info.Status;
                if (info.Status == TaskStatus.NeedsReview) ReviewPointExecution(info, info.LastWaitReason);
                else if (item.Status != CargoTaskStatus.Completed && item.Status != CargoTaskStatus.NeedsReview && PointMoveInProgress(info))
                {
                    item.Status = CargoTaskStatus.Executing;
                    item.WaitReason = info.LastWaitReason;
                }
            }
            if (batch.Status == CargoBatchStatus.NeedsReview) return;
            if (!batch.Items.All(i => i.Status == CargoTaskStatus.Completed)) return;
            if (!batch.CustomerCompletedTime.HasValue) batch.CustomerCompletedTime = DateTime.Now;
            if (TaskInfos.Any(t => t.CargoBatchId == batch.BatchId && t.Status != TaskStatus.Finished) ||
                pointMoveReservations.Values.Any(r => r.Owner.CargoBatchId == batch.BatchId) ||
                PocketLocks.Values.Any(t => t.CargoBatchId == batch.BatchId)) return;
            batch.Status = CargoBatchStatus.Completed;
            batch.CompletedTime = DateTime.Now;
        }
    }
}
