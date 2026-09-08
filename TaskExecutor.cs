namespace ProfControl
{
    public partial class SampleProject
    {

        void ExecuteMoveTasks()
        {
            Tools.trace($"ExecuteMoveTasks");
            var completedBatchIds = new List<string>();

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
                        }
                        break;

                    case TaskStatus.Picked:
                        info.WorkAgv.SetValue("staID", info.ToMark);
                        info.WorkAgv.SetValue("staAction", "drop");
                        info.WorkAgv.GoTo(info.ToMark);
                        info.Status = TaskStatus.GoDrop;
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

                            // 卸货完成后释放取货巷道锁。
                            ReleaseLock(info.LockPickPocketKey);

                        }
                        break;

                    case TaskStatus.Dropped:
                        // 卸货完成后释放卸货巷道锁。
                        ReleaseLock(info.LockDropPocketKey);

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
                            if (!string.IsNullOrEmpty(info.BatchId))
                            {
                                completedBatchIds.Add(info.BatchId);
                            }
                        }
                        break;

                }

                UpdateAgvView(info);
            }

            foreach (var batchId in completedBatchIds.Distinct())
            {
                if (!ActivateNextCargoTask(batchId))
                {
                    PendingCargoBatches.Remove(batchId);
                }
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
            agv.SetValue("staID", info.FromMark);
            agv.SetValue("staAction", "pick");
            agv.GoTo(info.FromMark);
            info.Status = TaskStatus.GoPick;
            info.StateTime = DateTime.Now;
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

        void ReleaseLock(string lockKey)
        {
            if (!string.IsNullOrEmpty(lockKey) && PocketLocks.ContainsKey(lockKey))
            {
                PocketLocks.Remove(lockKey);
            }
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
