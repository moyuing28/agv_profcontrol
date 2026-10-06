namespace ProfControl
{
    public partial class SampleProject
    {

        void ExecuteMoveTasks()
        {
            // 与创建入口共用锁，防止重复调度和资源检查、占用之间被其他创建请求插入。
            lock (cargoBatchSync) ExecuteMoveTasksCore();
        }

        void ExecuteMoveTasksCore()
        {
            UpdateDelayedLockReleases();
            UpdateParkingTrips();

            foreach (var info in TaskInfos
                .Where(t => t.Status != TaskStatus.Finished))
            {
                switch (info.Status)
                {
                    case TaskStatus.None:
                        if (info.TaskType == TaskTypeEnum.移动任务)
                        {
                            AssignMoveAgv(info);
                        }
                        else
                        {
                            AssignAgv(info);
                        }
                        break;

                    case TaskStatus.GoMove:
                        if (Arrived(info, info.ToMark))
                        {
                            info.Status = TaskStatus.Finished;
                        }
                        break;

                    case TaskStatus.GoPickEntrance:
                        if (Arrived(info, info.PickEntranceMark) && ValidateExpectedCargo(info) && ValidateBatchPickStation(info))
                            MoveStage(info, info.FromMark, "pick", TaskStatus.GoPick);
                        break;

                    case TaskStatus.GoPick:
                        if (Arrived(info, info.FromMark))
                        {
                            info.Status = TaskStatus.Picking;
                            info.StateTime = DateTime.Now;
                        }
                        break;

                    case TaskStatus.Picking:
                        if (StateTimeout(info) && ValidateExpectedCargo(info) && ValidateBatchPickStation(info))
                        {
                            var fromStation = API.GetStation(info.FromMark);
                            var pickArea = GetStationCargoArea(fromStation, out var pickAreaError);
                            if (pickArea == null)
                            {
                                WaitReason(info, $"取货位 {info.FromMark}：{pickAreaError}");
                                break;
                            }
                            fromStation.MoveToOtherArea(pickArea, info.WorkAgv, "车");
                            if (!ValidateCargoOwner(info, info.WorkAgv)) break;
                            info.Status = TaskStatus.Picked;
                            Tools.trace($"[取货完成] AGV={info.WorkAgv.Name}, 取货位={info.FromMark}, 等待申请卸货巷道={info.DropPocketKey}");
                        }
                        break;

                    case TaskStatus.Picked:
                        // 下游未落实时保留取货锁，停在取货位。
                        if (!Arrived(info, info.FromMark) || !DropReady(info)) break;
                        if (info.SamePocket)
                            MoveStage(info, info.ToMark, "drop", TaskStatus.GoDrop);
                        else
                            MoveStage(info, info.PickEntranceMark, TaskStatus.GoPickExit);
                        break;

                    case TaskStatus.GoPickExit:
                        if (Arrived(info, info.PickEntranceMark))
                        {
                            ReleaseLock(info, true);
                            MoveStage(info, info.DropEntranceMark, TaskStatus.GoDropEntrance);
                        }
                        break;

                    case TaskStatus.GoDropEntrance:
                        if (Arrived(info, info.DropEntranceMark) && DropReady(info))
                            MoveStage(info, info.ToMark, "drop", TaskStatus.GoDrop);
                        break;

                    case TaskStatus.GoDrop:
                        if (Arrived(info, info.ToMark))
                        {
                            info.Status = TaskStatus.Dropping;
                            info.StateTime = DateTime.Now;
                        }
                        break;

                    case TaskStatus.Dropping:
                        if (StateTimeout(info))
                        {
                            if (!ValidateCargoOwner(info, info.WorkAgv)) break;
                            var toStation = API.GetStation(info.ToMark);
                            var dropArea = GetStationCargoArea(toStation, out var dropAreaError);
                            if (dropArea == null)
                            {
                                WaitReason(info, $"卸货位 {info.ToMark}：{dropAreaError}");
                                break;
                            }
                            info.WorkAgv.MoveToOtherArea("车", toStation, dropArea);
                            if (!ValidateCargoOwner(info, toStation)) break;
                            info.Status = TaskStatus.Dropped;
                            info.DropCapacityReserved = false;


                        }
                        break;

                    case TaskStatus.Dropped:
                        MoveStage(info, info.DropEntranceMark, TaskStatus.GoDropExit);
                        break;

                    case TaskStatus.GoDropExit:
                        if (Arrived(info, info.DropEntranceMark))
                        {
                            ReleaseLock(info, true);
                            ReleaseLock(info, false);
                            info.Status = TaskStatus.ExitedDrop;
                            // 到出口立即申请回家，不等待巷道锁延迟释放。
                            if (TryPark(info.WorkAgv)) info.Status = TaskStatus.GoHome;
                            else WaitReason(info, "停车巷道暂不可进入，等待动态停车位");
                        }
                        break;

                    case TaskStatus.ExitedDrop:
                        if (TryPark(info.WorkAgv)) info.Status = TaskStatus.GoHome;
                        else WaitReason(info, "停车巷道暂不可进入，保持当前位置等待动态停车位");
                        break;

                    case TaskStatus.GoHome:
                        if (!parkingTrips.ContainsKey(info.WorkAgv) && info.WorkAgv.IsFree() &&
                            parkingLanes.Any(l => l.Slots.Contains(ParkingPosition(info.WorkAgv))))
                            info.Status = TaskStatus.Finished;
                        break;

                    case TaskStatus.GoParkingExit:
                        if (Arrived(info, info.ParkingExitMark))
                            FinishParkingExit(info);
                        break;
                }
                UpdateAgvView(info);
            }

            foreach (var clearanceBatchId in PendingClearanceBatches.Keys.ToList())
            {
                // 清障组按完成依赖激活下一步；暂时不可用时保留队列重试。
                ActivateNextClearanceTask(clearanceBatchId);
            }

            SyncCargoBatchProgress();
            DispatchCargoBatch();

        }



        // 朝向子分类名称与货物区域名称一致，每个库位只允许匹配一个朝向。
        string GetStationCargoArea(IStation station, out string error)
        {
            error = null;
            if (station == null)
            {
                error = "找不到站点，无法确定货物区域";
                return null;
            }

            if (stationCargoAreas.TryGetValue(station.Mark, out var area)) return area;
            if (!stationCargoAreaErrors.TryGetValue(station.Mark, out error))
                error = "站点不在有效车库货位配置中";
            return null;
        }

        void AssignMoveAgv(TaskInfo info)
        {
            var toStation = API.GetStation(info.ToMark);
            if (toStation == null) return;

            var availableAgvs = API.按条件找一些Agv(a =>
                    a.IsFree() && CanLeaveParking(a) &&
                    !TaskInfos.Any(t => t.WorkAgv == a && t.Status != TaskStatus.Finished));

            var agv = string.IsNullOrWhiteSpace(info.RequestedAgv) || info.RequestedAgv == "00"
                ? availableAgvs
                    .OrderBy(a => API.Dist(a.View.KeyPoint, toStation.View.KeyPoint))
                    .ThenBy(a => a.Name)
                    .FirstOrDefault()
                : availableAgvs.FirstOrDefault(a => a.Name == info.RequestedAgv);
            if (agv == null) return;

            info.WorkAgv = agv;
            StartFromParking(info, info.ToMark, TaskStatus.GoMove);
        }

        void AssignAgv(TaskInfo info)
        {
            var fromStation = API.GetStation(info.FromMark);
            if (fromStation == null) return;
            if (!PrepareEntrances(info) || !info.DropCapacityReserved || !OwnsLock(info, info.LockPickPocketKey)) return;

            var agv = FindCargoAgv(info);
            if (agv == null)
            {
                WaitReason(info, "等待可用车辆");
                return;
            }

            info.WorkAgv = agv;
            StartFromParking(info, info.PickEntranceMark, TaskStatus.GoPickEntrance);
        }

        IAgv FindCargoAgv(TaskInfo info)
        {
            var fromStation = API.GetStation(info.FromMark);
            if (fromStation == null) return null;
            var availableAgvs = API.按条件找一些Agv(a =>
                    a.IsFree() && CanLeaveParking(a) &&
                    !TaskInfos.Any(t => t.WorkAgv == a && t.Status != TaskStatus.Finished));

            return string.IsNullOrWhiteSpace(info.RequestedAgv) || info.RequestedAgv == "00"
                ? availableAgvs
                    .OrderBy(a => API.Dist(a.View.KeyPoint, fromStation.View.KeyPoint))
                    .ThenBy(a => a.Name)
                    .FirstOrDefault()
                : availableAgvs.FirstOrDefault(a => a.Name == info.RequestedAgv);
        }

        bool Arrived(TaskInfo info, string mark)
        {
            return info.WorkAgv != null &&
                   info.WorkAgv.IsFree() &&
                   info.WorkAgv.API.On == mark;
        }

        bool StateTimeout(TaskInfo info)
        {
            return (DateTime.Now - info.StateTime).TotalSeconds >= PickDropDelay;
        }

        const double LaneLockReleaseDelaySeconds = 1;

        readonly List<(DateTime Due, Action Release)> delayedLockReleases = new List<(DateTime, Action)>();

        void DelayLockRelease(Action release)
        {
            delayedLockReleases.Add((DateTime.UtcNow.AddSeconds(LaneLockReleaseDelaySeconds), release));
        }

        void UpdateDelayedLockReleases()
        {
            var now = DateTime.UtcNow;
            foreach (var pending in delayedLockReleases.Where(p => p.Due <= now).ToList())
            {
                delayedLockReleases.Remove(pending);
                pending.Release();
            }
        }

        void WaitReason(TaskInfo info, string reason)
        {
            if (info.LastWaitReason != reason)
            {
                Tools.trace($"[搬运等待] 取货位 {info.FromMark} → 卸货位 {info.ToMark ?? "待分配"}：{reason}");
                info.LastWaitReason = reason;
            }
        }

        bool PrepareEntrances(TaskInfo info)
        {
            var pick = GetCargoPocket(GetStationPocketKey(info.FromMark));
            var drop = GetCargoPocket(!string.IsNullOrEmpty(info.DropPocketKey)
                ? info.DropPocketKey : GetStationPocketKey(info.ToMark));
            if (pick == null || drop == null)
            {
                WaitReason(info, "找不到取卸货巷道或入口分类，不派车");
                return false;
            }
            if (pick.Error != null || drop.Error != null)
            {
                WaitReason(info, pick.Error ?? drop.Error);
                return false;
            }
            info.PickEntranceMark = pick.Entrance;
            info.DropEntranceMark = drop.Entrance;
            info.SamePocket = pick.Key == drop.Key;
            return true;
        }

        bool OwnsLock(TaskInfo info, string key)
        {
            return !string.IsNullOrEmpty(key) && PocketLocks.TryGetValue(key, out var owner) &&
                object.ReferenceEquals(owner, info);
        }

        List<IStation> DropSlots(TaskInfo info, bool afterPick = false)
        {
            var pocket = GetCargoPocket(info.DropPocketKey);
            if (pocket == null || pocket.Error != null) return new List<IStation>();
            return pocket.Slots.AsEnumerable().Reverse().TakeWhile(s => s.IsEmpty() ||
                (afterPick && s.Mark == info.FromMark && GetStationPocketKey(info.FromMark) == info.DropPocketKey))
                .Reverse().ToList();
        }

        bool ReserveDropCapacity(TaskInfo info)
        {
            if (string.IsNullOrEmpty(info.DropPocketKey))
                info.DropPocketKey = GetStationPocketKey(info.ToMark);
            // 点到点同巷道预留按取货后的状态计算，DropReady 仍按真实占用检查。
            var slots = DropSlots(info, afterPick: string.IsNullOrEmpty(info.CargoItemId));
            var reserved = TaskInfos.Where(t => !object.ReferenceEquals(t, info) && t.DropCapacityReserved && t.DropPocketKey == info.DropPocketKey).ToList();
            if (slots.Count <= reserved.Count || (!info.DynamicDrop &&
                (!slots.Any(s => s.Mark == info.ToMark) || reserved.Any(t => !t.DynamicDrop && t.ToMark == info.ToMark))))
            {
                WaitReason(info, "目标巷道容量不足或指定货位不可用，暂不派车");
                return false;
            }
            info.DropCapacityReserved = true;
            return true;
        }

        bool DropReady(TaskInfo info)
        {
            var key = info.DropPocketKey;
            if (string.IsNullOrEmpty(key) || !info.DropCapacityReserved) return false;
            if (PocketLocks.TryGetValue(key, out var owner) && !object.ReferenceEquals(owner, info))
            {
                WaitReason(info, $"车辆 {info.WorkAgv?.Name} 已取货，等待车辆 {owner.WorkAgv?.Name} 释放目标巷道（标识 {key}）；当前停在取货位 {info.FromMark}");
                return false;
            }
            var slots = DropSlots(info);
            var target = string.IsNullOrEmpty(info.ToMark) ? null : API.GetStation(info.ToMark);
            if (info.DynamicDrop && !OwnsLock(info, key))
            {
                var fixedMarks = new HashSet<string>(TaskInfos.Where(t => !object.ReferenceEquals(t, info) && t.DropCapacityReserved && !t.DynamicDrop).Select(t => t.ToMark));
                // 不可先放到固定预留货位外侧，把固定任务堵在深处。
                target = slots.TakeWhile(s => !fixedMarks.Contains(s.Mark)).FirstOrDefault();
            }
            if (target == null || !slots.Any(s => s.Mark == target.Mark))
            {
                WaitReason(info, "没有可进入的卸货位，保留取货锁并原地等待");
                return false;
            }
            if (!OwnsLock(info, key))
            {
                PocketLocks[key] = info;
                info.LockDropPocketKey = key;
                info.ToMark = target.Mark;
                Tools.trace($"[卸货准入] AGV={info.WorkAgv?.Name}, 取货={info.FromMark}, 巷道={key}, 确定卸货位={info.ToMark}");
            }
            info.LastWaitReason = null;
            return true;
        }

        void MoveStage(TaskInfo info, string mark, string action, TaskStatus status)
        {
            info.WorkAgv.SetValue("staID", mark);
            info.WorkAgv.SetValue("staAction", action);
            MoveStage(info, mark, status);
        }

        // 单纯移动只下发目的地，不设置站点动作。
        void MoveStage(TaskInfo info, string mark, TaskStatus status)
        {
            info.WorkAgv.GoTo(mark);
            info.Status = status;
            info.LastWaitReason = null;
            info.StateTime = DateTime.Now;
            // 批量任务由任务进度日志统一展示，避免同一阶段重复打印。
            if (string.IsNullOrEmpty(info.CargoItemId))
                Tools.trace($"[搬运阶段] 车辆 {info.WorkAgv.Name}｜{CargoStageText(status)}｜前往站点 {mark}");
        }

        void ReleaseLock(TaskInfo info, bool pick)
        {
            var key = pick ? info.LockPickPocketKey : info.LockDropPocketKey;
            if (OwnsLock(info, key))
            {
                var exitMark = info.WorkAgv.API.On;
                DelayLockRelease(() =>
                {
                    // 延迟期间仍由原任务持锁；只释放原任务的锁。
                    if (!OwnsLock(info, key)) return;
                    PocketLocks.Remove(key);
                    Tools.trace($"[巷道延迟释放] AGV={info.WorkAgv.Name}, 确认出口={exitMark}, 巷道Key={key}");
                });
            }
            if (pick) info.LockPickPocketKey = null;
            else info.LockDropPocketKey = null;
        }

        void UpdateAgvView(TaskInfo info)
        {
            if (info.WorkAgv == null) return;

            info.WorkAgv.SetValue("状态", info.Status == TaskStatus.Finished ? "" : info.Status.ToString());
            info.WorkAgv.SetValue("任务类型", info.Status == TaskStatus.Finished ? "" : info.TaskType.ToString());
        }


    }
}
