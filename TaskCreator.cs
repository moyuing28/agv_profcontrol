namespace ProfControl
{
    public partial class SampleProject
    {

        public bool CreateMoveTask(string toMark, string agvName = "00")
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
                Tools.trace("CreateStationMoveTask: 起点和终点无效");
                return false;
            }

            var fromStation = API.GetStation(fromMark);
            var toStation = API.GetStation(toMark);
            if (fromStation == null || toStation == null || fromStation.IsEmpty() || !toStation.IsEmpty())
            {
                Tools.trace("CreateStationMoveTask: 站点不存在或站点状态不符合要求");
                return false;
            }

            var runningTasks = TaskInfos.Count(t => t.Status != TaskStatus.Finished);
            if (runningTasks >= MaxParallelMoveTask)
            {
                Tools.trace("CreateStationMoveTask: 当前任务数已达到上限");
                return false;
            }

            var pickPocket = fromStation.BelongPocketLines.FirstOrDefault();
            var dropPocket = toStation.BelongPocketLines.FirstOrDefault();
            var pickKey = pickPocket?.OrderStations.FirstOrDefault()?.Mark;
            var dropKey = dropPocket?.OrderStations.FirstOrDefault()?.Mark;
            if (string.IsNullOrEmpty(pickKey) || string.IsNullOrEmpty(dropKey) ||
                PocketLocks.ContainsKey(pickKey) || PocketLocks.ContainsKey(dropKey))
            {
                Tools.trace("CreateStationMoveTask: 起点或终点巷道已被锁定");
                return false;
            }

            if (agvName != "00" && !API.Agvs.Any(a => a.Name == agvName))
            {
                Tools.trace($"CreateStationMoveTask: 找不到指定 AGV {agvName}");
                return false;
            }

            var task = new TaskInfo
            {
                FromMark = fromMark,
                ToMark = toMark,
                RequestedAgv = string.IsNullOrWhiteSpace(agvName) ? "00" : agvName,
                TaskType = TaskTypeEnum.移库任务,
                LockPickPocketKey = pickKey,
                LockDropPocketKey = pickKey == dropKey ? null : dropKey
            };

            PocketLocks[pickKey] = task;
            if (task.LockDropPocketKey != null)
            {
                PocketLocks[task.LockDropPocketKey] = task;
            }

            TaskInfos.Add(task);
            Tools.trace($"CreateStationMoveTask: {fromMark} -> {toMark}, AGV={task.RequestedAgv}");
            return true;
        }

        public bool CreateStationMoveTask(string targetMark, string toMark, string agvName = "00")
        {
            agvName = string.IsNullOrWhiteSpace(agvName) ? "00" : agvName;

            var targetStation = API.GetStation(targetMark);
            var destinationStation = API.GetStation(toMark);
            if (targetStation == null || destinationStation == null || targetStation.IsEmpty() ||
                !destinationStation.IsEmpty() || targetMark == toMark)
            {
                Tools.trace("CreateClearanceMoveTask: 目标车辆或最终目标位置无效");
                return false;
            }

            if (TaskInfos.Any(t => t.Status != TaskStatus.Finished) ||
                PendingCargoBatches.Count > 0)
            {
                Tools.trace("CreateClearanceMoveTask: 当前已有任务");
                return false;
            }

            if (agvName != "00" && !API.Agvs.Any(a => a.Name == agvName))
            {
                Tools.trace($"CreateClearanceMoveTask: 找不到指定 AGV {agvName}");
                return false;
            }

            var targetPocket = targetStation.BelongPocketLines
                .FirstOrDefault(p => p.OrderStations.Any(s => s.Mark == targetMark));
            var destinationPocket = destinationStation.BelongPocketLines.FirstOrDefault();
            if (targetPocket == null || destinationPocket == null)
            {
                Tools.trace("CreateClearanceMoveTask: 目标站点没有所属巷道");
                return false;
            }

            var targetStations = targetPocket.OrderStations.ToList();
            var targetIndex = targetStations.FindIndex(s => s.Mark == targetMark);
            if (targetIndex < 0)
            {
                Tools.trace("CreateClearanceMoveTask: 找不到目标车在巷道中的位置");
                return false;
            }

            var garageSub = SubCategoryCollection.Instance[GarageArea] as ISubCategory;
            if (garageSub == null) return false;

            var garageStations = garageSub.Children.Cast<IStation>().ToList();
            var entranceSub = SubCategoryCollection.Instance["入口"] as ISubCategory;
            var destinationPocketKey = destinationPocket.OrderStations.FirstOrDefault()?.Mark;
            var targetPocketKey = targetPocket.OrderStations.FirstOrDefault()?.Mark;
            var reservedMarks = new HashSet<string>();
            var steps = new List<TaskInfo>();

            Tools.trace($"CreateStationMoveTask: 巷道站点={string.Join(",", targetStations.Select(s => $"{s.Mark}:{(s.IsEmpty() ? "空" : "有车")}"))}");

            var blockingStations = targetStations
                .Skip(targetIndex + 1)
                .Where(s => entranceSub == null ||
                            !entranceSub.Children.Cast<IStation>().Any(e => e.Mark == s.Mark))
                .Where(s => !s.IsEmpty())
                .Reverse()
                .ToList();

            Tools.trace($"CreateStationMoveTask: 目标站点={targetMark}, 巷道位置={targetIndex}, 阻挡车辆数={blockingStations.Count}");

            if (blockingStations.Count == 0)
            {
                Tools.trace("CreateClearanceMoveTask: 未检测到阻挡车辆，直接创建普通移库任务");
                return CreateSimpleStationMoveTask(targetMark, toMark, agvName);
            }

            foreach (var blockingStation in blockingStations)
            {
                var temporaryStation = garageStations
                    .Where(s => s.BelongPocketLines.Any(p =>
                        p.OrderStations.FirstOrDefault()?.Mark != targetPocketKey &&
                        p.OrderStations.FirstOrDefault()?.Mark != destinationPocketKey &&
                        !PocketLocks.ContainsKey(p.OrderStations.FirstOrDefault()?.Mark)))
                    .Where(s => entranceSub == null ||
                                !entranceSub.Children.Cast<IStation>().Any(e => e.Mark == s.Mark))
                    .Where(s => s.IsEmpty() && !reservedMarks.Contains(s.Mark))
                    .OrderBy(s => s.BelongPocketLines
                        .SelectMany(p => p.OrderStations)
                        .ToList()
                        .FindIndex(x => x.Mark == s.Mark))
                    .FirstOrDefault();

                if (temporaryStation == null)
                {
                    Tools.trace("CreateClearanceMoveTask: 没有找到其他巷道的临时空位");
                    return false;
                }

                reservedMarks.Add(temporaryStation.Mark);
                steps.Add(new TaskInfo
                {
                    FromMark = blockingStation.Mark,
                    ToMark = temporaryStation.Mark,
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

            var batchId = Guid.NewGuid().ToString();
            for (var i = 0; i < steps.Count; i++)
            {
                steps[i].BatchId = batchId;
                steps[i].BatchSequence = i + 1;
            }

            PendingCargoBatches[batchId] = new Queue<TaskInfo>(steps);
            if (!ActivateNextCargoTask(batchId))
            {
                PendingCargoBatches.Remove(batchId);
                return false;
            }

            Tools.trace($"CreateClearanceMoveTask: {targetMark} -> {toMark}, 子任务数={steps.Count}");
            return true;
        }

        bool ActivateNextCargoTask(string batchId)
        {
            if (!PendingCargoBatches.TryGetValue(batchId, out var queue) || queue.Count == 0)
            {
                PendingCargoBatches.Remove(batchId);
                return true;
            }

            var task = queue.Peek();
            var fromStation = API.GetStation(task.FromMark);
            var toStation = API.GetStation(task.ToMark);
            if (fromStation == null || toStation == null || fromStation.IsEmpty() || !toStation.IsEmpty())
            {
                Tools.trace($"ActivateNextCargoTask: 子任务站点状态无效 {task.FromMark} -> {task.ToMark}");
                return false;
            }

            var pickKey = fromStation.BelongPocketLines.FirstOrDefault()?.OrderStations.FirstOrDefault()?.Mark;
            var dropKey = toStation.BelongPocketLines.FirstOrDefault()?.OrderStations.FirstOrDefault()?.Mark;
            if (string.IsNullOrEmpty(pickKey) || string.IsNullOrEmpty(dropKey) ||
                PocketLocks.ContainsKey(pickKey) || PocketLocks.ContainsKey(dropKey))
            {
                Tools.trace("ActivateNextCargoTask: 子任务巷道已被锁定");
                return false;
            }

            task.LockPickPocketKey = pickKey;
            task.LockDropPocketKey = pickKey == dropKey ? null : dropKey;
            PocketLocks[pickKey] = task;
            if (task.LockDropPocketKey != null) PocketLocks[task.LockDropPocketKey] = task;
            TaskInfos.Add(task);
            queue.Dequeue();
            return true;
        }
    }
}
