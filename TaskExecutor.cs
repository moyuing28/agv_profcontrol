namespace ProfControl
{
    public partial class SampleProject
    {

        void ExecuteMoveTasks()
        {

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
                        if (Arrived(info, info.PickEntranceMark))
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
                        if (StateTimeout(info))
                        {
                                     API.GetStation(info.FromMark)
                                         .MoveToOtherArea(GarageArea, info.WorkAgv, "车");
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
                            MoveStage(info, info.PickEntranceMark, "move", TaskStatus.GoPickExit);
                        break;

                    case TaskStatus.GoPickExit:
                        if (Arrived(info, info.PickEntranceMark))
                        {
                            ReleaseLock(info, true);
                            MoveStage(info, info.DropEntranceMark, "move", TaskStatus.GoDropEntrance);
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
                            info.WorkAgv.MoveToOtherArea("车",
                                API.GetStation(info.ToMark), GarageArea);
                            info.Status = TaskStatus.Dropped;
                            info.DropCapacityReserved = false;


                        }
                        break;

                    case TaskStatus.Dropped:
                        MoveStage(info, info.DropEntranceMark, "move", TaskStatus.GoDropExit);
                        break;

                    case TaskStatus.GoDropExit:
                        if (Arrived(info, info.DropEntranceMark))
                        {
                            ReleaseLock(info, true);
                            ReleaseLock(info, false);
                            info.Status = TaskStatus.ExitedDrop;
                        }
                        break;

                    case TaskStatus.ExitedDrop:
                        // 每单必须先退出巷道并回家，到家后才结束任务。
                        var homeStaId = info.WorkAgv.GetValue<string>("homestaID");
                        if (string.IsNullOrWhiteSpace(homeStaId) || API.GetStation(homeStaId) == null)
                        {
                            Tools.trace($"AGV {info.WorkAgv.Name} 未配置有效 homestaID，保留任务，等待修正。");
                            break;
                        }
                        info.WorkAgv.GoTo(homeStaId);
                        info.Status = TaskStatus.GoHome;
                        break;

                    case TaskStatus.GoHome:
                        if (Arrived(info, info.WorkAgv.GetValue<string>("homestaID")))
                        {
                            info.Status = TaskStatus.Finished;
                        }
                        break;

                }

                UpdateAgvView(info);
            }

            foreach (var batchId in PendingCargoBatches.Keys.ToList())
            {
                // 取货锁释放即可激活下一单；暂时不可用时保留队列重试。
                ActivateNextCargoTask(batchId);
            }

            // ✅ 每次调度后，如果已进入回家模式，确保所有空闲 AGV 都被送回家
            if (is_GoHome)
            {
                SendFreeAgvsHome();
            }
        }



        void AssignMoveAgv(TaskInfo info)
        {
            var toStation = API.GetStation(info.ToMark);
            if (toStation == null) return;

            var availableAgvs = API.按条件找一些Agv(a =>
                    a.IsFree() &&
                    !TaskInfos.Any(t => t.WorkAgv == a && t.Status != TaskStatus.Finished));

            var agv = string.IsNullOrWhiteSpace(info.RequestedAgv) || info.RequestedAgv == "00"
                ? availableAgvs
                    .OrderBy(a => API.Dist(a.View.KeyPoint, toStation.View.KeyPoint))
                    .ThenBy(a => a.Name)
                    .FirstOrDefault()
                : availableAgvs.FirstOrDefault(a => a.Name == info.RequestedAgv);
            if (agv == null) return;

            info.WorkAgv = agv;
            agv.SetValue("staID", info.ToMark);
            agv.SetValue("staAction", "move");
            agv.GoTo(info.ToMark);
            info.Status = TaskStatus.GoMove;
            info.StateTime = DateTime.Now;
            Tools.trace($"AssignMoveAgv: {agv.Name} -> {info.ToMark}");
        }

        void AssignAgv(TaskInfo info)
        {
            var fromStation = API.GetStation(info.FromMark);
            if (fromStation == null) return;
            if (!PrepareEntrances(info) || !info.DropCapacityReserved || !OwnsLock(info, info.LockPickPocketKey)) return;

            var availableAgvs = API.按条件找一些Agv(a =>
                    a.IsFree() &&
                    !TaskInfos.Any(t => t.WorkAgv == a && t.Status != TaskStatus.Finished));

            var agv = string.IsNullOrWhiteSpace(info.RequestedAgv) || info.RequestedAgv == "00"
                ? availableAgvs
                    .OrderBy(a => API.Dist(a.View.KeyPoint, fromStation.View.KeyPoint))
                    .ThenBy(a => a.Name)
                    .FirstOrDefault()
                : availableAgvs.FirstOrDefault(a => a.Name == info.RequestedAgv);
            if (agv == null) return;

            info.WorkAgv = agv;
            MoveStage(info, info.PickEntranceMark, "move", TaskStatus.GoPickEntrance);
            Tools.trace($"AssignAgv: {agv.Name} -> {info.FromMark}");
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

        void WaitReason(TaskInfo info, string reason)
        {
            if (info.LastWaitReason != reason)
            {
                Tools.trace($"[搬运等待] {info.FromMark}->{info.ToMark}: {reason}");
                info.LastWaitReason = reason;
            }
        }

        bool PrepareEntrances(TaskInfo info)
        {
            var pick = API.GetStation(info.FromMark)?.BelongPocketLines.FirstOrDefault();
            var drop = API.GetStation(info.ToMark)?.BelongPocketLines.FirstOrDefault();
            var sub = SubCategoryCollection.Instance["入口"] as ISubCategory;
            if (pick == null || drop == null || sub == null)
            {
                WaitReason(info, "找不到取卸货巷道或入口分类，不派车");
                return false;
            }
            var marks = new HashSet<string>(sub.Children.OfType<IStation>().Select(s => s.Mark));
            var picks = pick.OrderStations.Where(s => marks.Contains(s.Mark)).Select(s => s.Mark).Distinct().ToList();
            var drops = drop.OrderStations.Where(s => marks.Contains(s.Mark)).Select(s => s.Mark).Distinct().ToList();
            if (picks.Count != 1 || drops.Count != 1)
            {
                WaitReason(info, $"入口不唯一：取货入口={string.Join(",", picks)}，卸货入口={string.Join(",", drops)}，不派车");
                return false;
            }
            info.PickEntranceMark = picks[0];
            info.DropEntranceMark = drops[0];
            info.SamePocket = pick.OrderStations.First().Mark == drop.OrderStations.First().Mark;
            return true;
        }

        bool OwnsLock(TaskInfo info, string key)
        {
            return !string.IsNullOrEmpty(key) && PocketLocks.TryGetValue(key, out var owner) &&
                object.ReferenceEquals(owner, info);
        }

        List<IStation> DropSlots(TaskInfo info)
        {
            var pocket = API.GetStation(info.DropPocketKey)?.BelongPocketLines
                .FirstOrDefault(p => p.OrderStations.FirstOrDefault()?.Mark == info.DropPocketKey);
            var garage = SubCategoryCollection.Instance[GarageArea] as ISubCategory;
            var entrance = SubCategoryCollection.Instance["入口"] as ISubCategory;
            if (pocket == null || garage == null) return new List<IStation>();
            var marks = new HashSet<string>(garage.Children.OfType<IStation>().Select(s => s.Mark));
            var gates = new HashSet<string>(entrance == null ? new List<string>() : entrance.Children.OfType<IStation>().Select(s => s.Mark).ToList());
            return pocket.OrderStations.Where(s => marks.Contains(s.Mark) && !gates.Contains(s.Mark))
                .Reverse().TakeWhile(s => s.IsEmpty()).Reverse().ToList();
        }

        bool ReserveDropCapacity(TaskInfo info)
        {
            if (string.IsNullOrEmpty(info.DropPocketKey))
                info.DropPocketKey = API.GetStation(info.ToMark)?.BelongPocketLines.FirstOrDefault()?.OrderStations.FirstOrDefault()?.Mark;
            var slots = DropSlots(info);
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
                WaitReason(info, $"AGV={info.WorkAgv?.Name}在取货位={info.FromMark}等待卸货巷道={key}，占用AGV={owner.WorkAgv?.Name}");
                return false;
            }
            var slots = DropSlots(info);
            var target = API.GetStation(info.ToMark);
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
            info.WorkAgv.GoTo(mark);
            info.Status = status;
            info.StateTime = DateTime.Now;
            Tools.trace($"[搬运阶段] AGV={info.WorkAgv.Name}, {info.FromMark}->{info.ToMark}, 状态={status}, 前往={mark}");
        }

        void ReleaseLock(TaskInfo info, bool pick)
        {
            var key = pick ? info.LockPickPocketKey : info.LockDropPocketKey;
            if (OwnsLock(info, key))
            {
                PocketLocks.Remove(key);
                Tools.trace($"[巷道释放] AGV={info.WorkAgv.Name}, 到达出口={info.WorkAgv.API.On}, 巷道Key={key}");
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

        /// <summary>
        /// 将所有空闲且无任务的 AGV 送回家
        /// </summary>
        void SendFreeAgvsHome()
        {
            foreach (var agv in API.Agvs)
            {
                if (!agv.IsFree()) continue;

                // 跳过有未完成任务（状态不是 Finished）的 AGV
                if (TaskInfos.Any(t => t.WorkAgv == agv && t.Status != TaskStatus.Finished))
                    continue;

                // 如果 AGV 不在 home 站，则送它回家
                var homeStaId = agv.GetValue<string>("homestaID");
                if (!string.IsNullOrEmpty(homeStaId) && agv.API.On != homeStaId)
                {
                    agv.GoTo(homeStaId);
                    Tools.trace($"SendFreeAgvsHome: {agv.Name} -> {homeStaId}");
                }
            }
        }

    }
}
