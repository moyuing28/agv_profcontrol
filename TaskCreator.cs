namespace ProfControl
{
    public partial class SampleProject
    {

        public bool CreateMoveTask(string toMark, string agvName = "00")
        {
            lock (cargoBatchSync)
            {
                if (HasOpenCargoBatch())
                {
                    Tools.trace("CreateMoveTask: 当前搬运批次尚未结束");
                    return false;
                }
                return CreateMoveTaskCore(toMark, agvName);
            }
        }

        bool CreateMoveTaskCore(string toMark, string agvName)
        {
            agvName = string.IsNullOrWhiteSpace(agvName) ? "00" : agvName;

            if (string.IsNullOrWhiteSpace(toMark))
            {
                Tools.trace("CreateMoveTask: 终点无效");
                return false;
            }

            if (API.GetStation(toMark) == null)
            {
                Tools.trace("CreateMoveTask: 终点不存在");
                return false;
            }

            if (TaskInfos.Count(t => t.Status != TaskStatus.Finished) >= MaxParallelMoveTask)
            {
                Tools.trace("CreateMoveTask: 当前任务数已达到上限");
                return false;
            }

            if (agvName != "00" && !API.Agvs.Any(a => a.Name == agvName))
            {
                Tools.trace($"CreateMoveTask: 找不到指定 AGV {agvName}");
                return false;
            }

            TaskInfos.Add(new TaskInfo
            {
                ToMark = toMark,
                RequestedAgv = agvName,
                TaskType = TaskTypeEnum.移动任务
            });

            Tools.trace($"CreateMoveTask: -> {toMark}, AGV={agvName}");
            return true;
        }

        bool CreateSimpleStationMoveTask(string fromMark, string toMark, string agvName = "00")
        {
            agvName = string.IsNullOrWhiteSpace(agvName) ? "00" : agvName;

            if (string.IsNullOrWhiteSpace(fromMark) || string.IsNullOrWhiteSpace(toMark) || fromMark == toMark)
            {
                Tools.trace("[点到点搬运] 起点和终点无效");
                return false;
            }

            var fromStation = API.GetStation(fromMark);
            var toStation = API.GetStation(toMark);
            if (fromStation == null || toStation == null || fromStation.IsEmpty() || !toStation.IsEmpty())
            {
                Tools.trace("[点到点搬运] 站点不存在或站点状态不符合要求");
                return false;
            }

            var runningTasks = TaskInfos.Count(t => t.Status != TaskStatus.Finished);
            if (runningTasks >= MaxParallelMoveTask)
            {
                Tools.trace("[点到点搬运] 当前任务数已达到上限");
                return false;
            }

            var pickPocket = fromStation.BelongPocketLines.FirstOrDefault();
            var dropPocket = toStation.BelongPocketLines.FirstOrDefault();
            var pickKey = pickPocket?.OrderStations.FirstOrDefault()?.Mark;
            var dropKey = dropPocket?.OrderStations.FirstOrDefault()?.Mark;
            if (string.IsNullOrEmpty(pickKey) || string.IsNullOrEmpty(dropKey) ||
                PocketLocks.ContainsKey(pickKey))
            {
                Tools.trace("[点到点搬运] 起点或终点巷道已被锁定");
                return false;
            }

            if (agvName != "00" && !API.Agvs.Any(a => a.Name == agvName))
            {
                Tools.trace($"[点到点搬运] 找不到指定 AGV {agvName}");
                return false;
            }

            var task = new TaskInfo
            {
                FromMark = fromMark,
                ToMark = toMark,
                RequestedAgv = string.IsNullOrWhiteSpace(agvName) ? "00" : agvName,
                TaskType = TaskTypeEnum.移库任务,
                LockPickPocketKey = pickKey,
                DropPocketKey = dropKey
            };

            if (!ReserveDropCapacity(task) || !PrepareEntrances(task)) return false;
            PocketLocks[pickKey] = task;

            TaskInfos.Add(task);
            Tools.trace($"[点到点搬运] {fromMark} -> {toMark}, AGV={task.RequestedAgv}");
            return true;
        }

        public bool CreateStationMoveTask(string targetMark, string toMark, string agvName = "00")
        {
            lock (cargoBatchSync)
            {
                if (HasOpenCargoBatch())
                {
                    Tools.trace("[点到点搬运] 当前搬运批次尚未结束");
                    return false;
                }
                return CreateStationMoveTaskCore(targetMark, toMark, agvName);
            }
        }

        bool CreateStationMoveTaskCore(string targetMark, string toMark, string agvName)
        {
            agvName = string.IsNullOrWhiteSpace(agvName) ? "00" : agvName;

            var targetStation = API.GetStation(targetMark);
            var destinationStation = API.GetStation(toMark);
            if (targetStation == null || destinationStation == null || targetStation.IsEmpty() ||
                !destinationStation.IsEmpty() || targetMark == toMark)
            {
                Tools.trace("[点到点搬运] 取货站点或目标货位无效");
                return false;
            }

            if (TaskInfos.Any(t => t.Status != TaskStatus.Finished) ||
                PendingClearanceBatches.Count > 0)
            {
                Tools.trace("[点到点搬运] 当前已有任务");
                return false;
            }

            if (agvName != "00" && !API.Agvs.Any(a => a.Name == agvName))
            {
                Tools.trace($"[点到点搬运] 找不到指定 AGV {agvName}");
                return false;
            }

            var targetPocket = targetStation.BelongPocketLines
                .FirstOrDefault(p => p.OrderStations.Any(s => s.Mark == targetMark));
            var destinationPocket = destinationStation.BelongPocketLines.FirstOrDefault();
            if (targetPocket == null || destinationPocket == null)
            {
                Tools.trace("[点到点搬运] 目标站点没有所属巷道");
                return false;
            }

            var targetStations = targetPocket.OrderStations.ToList();
            var targetIndex = targetStations.FindIndex(s => s.Mark == targetMark);
            if (targetIndex < 0)
            {
                Tools.trace("[点到点搬运] 找不到待搬货物在巷道中的位置");
                return false;
            }

            var garageSub = GetMapCategory(GarageArea);
            if (garageSub == null) return false;

            var garageStations = garageSub.Children.Cast<IStation>().ToList();
            var entranceSub = GetMapCategory("入口");
            var destinationPocketKey = destinationPocket.OrderStations.FirstOrDefault()?.Mark;
            var targetPocketKey = targetPocket.OrderStations.FirstOrDefault()?.Mark;
            var reservedMarks = new HashSet<string>();
            var steps = new List<TaskInfo>();

            Tools.trace($"[点到点搬运] 巷道站点={string.Join(",", targetStations.Select(s => $"{s.Mark}:{(s.IsEmpty() ? "空" : "有货")}"))}");

            var blockingStations = targetStations
                .Skip(targetIndex + 1)
                .Where(s => entranceSub == null ||
                            !entranceSub.Marks.Contains(s.Mark))
                .Where(s => !s.IsEmpty())
                .Reverse()
                .ToList();

            Tools.trace($"[点到点搬运] 目标站点={targetMark}, 巷道位置={targetIndex}, 阻挡货物数={blockingStations.Count}");

            if (blockingStations.Count == 0)
            {
                Tools.trace("[点到点搬运] 未检测到阻挡货物，直接创建普通移库任务");
                return CreateSimpleStationMoveTask(targetMark, toMark, agvName);
            }

            foreach (var blockingStation in blockingStations)
            {
                var clearanceStation = garageStations
                    .Where(s => s.BelongPocketLines.Any(p =>
                        p.OrderStations.FirstOrDefault()?.Mark != targetPocketKey &&
                        p.OrderStations.FirstOrDefault()?.Mark != destinationPocketKey &&
                        !PocketLocks.ContainsKey(p.OrderStations.FirstOrDefault()?.Mark)))
                    .Where(s => entranceSub == null ||
                                !entranceSub.Marks.Contains(s.Mark))
                    .Where(s => s.IsEmpty() && !reservedMarks.Contains(s.Mark))
                    .OrderBy(s => s.BelongPocketLines
                        .SelectMany(p => p.OrderStations)
                        .ToList()
                        .FindIndex(x => x.Mark == s.Mark))
                    .FirstOrDefault();

                if (clearanceStation == null)
                {
                    Tools.trace("[清障规划失败] 其他巷道没有可用于搬开阻挡货物的空位");
                    return false;
                }

                reservedMarks.Add(clearanceStation.Mark);
                steps.Add(new TaskInfo
                {
                    FromMark = blockingStation.Mark,
                    ToMark = clearanceStation.Mark,
                    DynamicDrop = true,
                    RequestedAgv = agvName,
                    TaskType = TaskTypeEnum.移库任务
                });
            }

            steps.Add(new TaskInfo
            {
                FromMark = targetMark,
                ToMark = toMark,
                RequestedAgv = agvName,
                TaskType = TaskTypeEnum.移库任务
            });

            var clearanceBatchId = Guid.NewGuid().ToString();
            for (var i = 0; i < steps.Count; i++)
            {
                steps[i].ClearanceBatchId = clearanceBatchId;
                steps[i].ClearanceSequence = i + 1;
            }

            PendingClearanceBatches[clearanceBatchId] = new Queue<TaskInfo>(steps);
            if (!ActivateNextClearanceTask(clearanceBatchId))
            {
                PendingClearanceBatches.Remove(clearanceBatchId);
                return false;
            }

            Tools.trace($"[清障计划] 目标货物 {targetMark} -> {toMark}，共 {steps.Count} 个步骤（先搬开阻挡货物，再搬目标货物；不包含搬回原位）");
            return true;
        }

        bool ActivateNextClearanceTask(string clearanceBatchId)
        {
            if (!PendingClearanceBatches.TryGetValue(clearanceBatchId, out var queue) || queue.Count == 0)
            {
                PendingClearanceBatches.Remove(clearanceBatchId);
                return true;
            }

            var task = queue.Peek();
            if (TaskInfos.Count(t => t.Status != TaskStatus.Finished) >= MaxParallelMoveTask) return false;
            var fromStation = API.GetStation(task.FromMark);
            var toStation = API.GetStation(task.ToMark);
            if (fromStation == null || toStation == null || fromStation.IsEmpty())
            {
                WaitReason(task, "清障步骤站点状态无效，等待重试");
                return false;
            }

            var pickKey = fromStation.BelongPocketLines.FirstOrDefault()?.OrderStations.FirstOrDefault()?.Mark;
            var dropKey = toStation.BelongPocketLines.FirstOrDefault()?.OrderStations.FirstOrDefault()?.Mark;
            if (string.IsNullOrEmpty(pickKey) || string.IsNullOrEmpty(dropKey) ||
                PocketLocks.ContainsKey(pickKey))
            {
                return false;
            }

            task.DropPocketKey = dropKey;
            if (!ReserveDropCapacity(task) || !PrepareEntrances(task)) return false;
            task.LockPickPocketKey = pickKey;
            PocketLocks[pickKey] = task;
            TaskInfos.Add(task);
            queue.Dequeue();
            Tools.trace($"[清障步骤开始] 步骤组={task.ClearanceBatchId}, 序号={task.ClearanceSequence}, 取货={task.FromMark}, 卸货巷道={task.DropPocketKey}, 动态货位={task.DynamicDrop}");
            return true;
        }
    }
}
