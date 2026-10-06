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

        public CargoMoveCreateResult CreateCargoMoveTask(string cargoName, string cargoTag,
            string toMark, string agvName = "00")
        {
            lock (cargoBatchSync)
            {
                var result = new CargoMoveCreateResult
                {
                    CargoName = cargoName, CargoTag = cargoTag, ToMark = toMark
                };
                if (string.IsNullOrWhiteSpace(cargoName) || string.IsNullOrWhiteSpace(cargoTag) ||
                    string.IsNullOrWhiteSpace(toMark))
                    result.Error = "货物类型、Tag 和目标站点不能为空";
                else if (HasOpenCargoBatch() || TaskInfos.Any(t => t.Status != TaskStatus.Finished) ||
                    PendingClearanceBatches.Count > 0)
                    result.Error = "当前已有未完成任务或搬运批次";
                else
                {
                    var matches = API.Cargos.Where(c => c.Info?.Name == cargoName &&
                        Convert.ToString(c.Tag) == cargoTag).ToList();
                    if (matches.Count != 1)
                        result.Error = matches.Count == 0 ? "未找到指定货物" : "货物类型与 Tag 匹配到多件货物";
                    else if (!(API.GetOwner(matches[0]) is IStation station))
                        result.Error = "货物当前不在站点，可能已在 AGV 上";
                    else
                    {
                        result.FromMark = station.Mark;
                        if (!IsExpectedCargoAtStation(station.Mark, cargoName, cargoTag))
                            result.Error = "源站点必须仅有一件数量为 1 的指定货物";
                        else if (CreateStationMoveTaskCore(station.Mark, toMark, agvName, cargoName, cargoTag))
                            result.Success = true;
                        else
                            result.Error = "搬运创建失败，请检查目标货位、巷道配置、指定车辆和清障空间；详情见终端日志";
                    }
                }
                Tools.trace($"[按货物搬运] 类型={cargoName}, Tag={cargoTag}, {result.FromMark} -> {toMark}, 成功={result.Success}, 原因={result.Error}");
                return result;
            }
        }

        bool IsExpectedCargoAtStation(string mark, string cargoName, string cargoTag)
        {
            var cargos = API.Cargos.Where(c => API.GetOwner(c) is IStation station &&
                station.Mark == mark).ToList();
            return cargos.Count == 1 && cargos[0].Info?.Name == cargoName &&
                Convert.ToString(cargos[0].Tag) == cargoTag && cargos[0].Quanlity == 1;
        }

        bool ValidateExpectedCargo(TaskInfo info)
        {
            if (string.IsNullOrEmpty(info.ExpectedCargoName)) return true;
            if (IsExpectedCargoAtStation(info.FromMark, info.ExpectedCargoName, info.ExpectedCargoTag))
                return true;
            info.Status = TaskStatus.NeedsReview;
            WaitReason(info, $"指定货物 {info.ExpectedCargoName}/{info.ExpectedCargoTag} 已不在取货位或取货位存在其他货物，需核对");
            return false;
        }

        bool CreateSimpleStationMoveTask(string fromMark, string toMark, string agvName = "00",
            string cargoName = null, string cargoTag = null)
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
                ExpectedCargoName = cargoName,
                ExpectedCargoTag = cargoTag,
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

        bool CreateStationMoveTaskCore(string targetMark, string toMark, string agvName,
            string cargoName = null, string cargoTag = null)
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

            var pickConfig = GetCargoPocket(GetStationPocketKey(targetMark));
            var dropConfig = GetCargoPocket(GetStationPocketKey(toMark));
            if (pickConfig == null || dropConfig == null || pickConfig.Error != null || dropConfig.Error != null ||
                !pickConfig.Slots.Any(s => s.Mark == targetMark) || !dropConfig.Slots.Any(s => s.Mark == toMark) ||
                !stationCargoAreas.ContainsKey(targetMark) || !stationCargoAreas.ContainsKey(toMark))
            {
                Tools.trace("[点到点搬运] 取卸货位或巷道配置无效");
                return false;
            }

            var garageSub = GetMapCategory(GarageArea);
            if (garageSub == null) return false;

            var entranceSub = GetMapCategory("入口");
            var destinationPocketKey = destinationPocket.OrderStations.FirstOrDefault()?.Mark;
            var targetPocketKey = targetPocket.OrderStations.FirstOrDefault()?.Mark;
            var reservedMarks = new HashSet<string>();
            var steps = new List<TaskInfo>();

            Tools.trace($"[点到点搬运] 巷道站点={string.Join(",", targetStations.Select(s => $"{s.Mark}:{(s.IsEmpty() ? "空" : "有货")}"))}");

            var pickBlockingStations = targetStations
                .Skip(targetIndex + 1)
                .Where(s => entranceSub == null ||
                            !entranceSub.Marks.Contains(s.Mark))
                .Where(s => !s.IsEmpty())
                .Reverse()
                .ToList();

            var destinationStations = destinationPocket.OrderStations.ToList();
            var destinationIndex = destinationStations.FindIndex(s => s.Mark == toMark);
            if (destinationIndex < 0) return false;
            var dropBlockingStations = destinationStations.Skip(destinationIndex + 1)
                .Where(s => entranceSub == null || !entranceSub.Marks.Contains(s.Mark))
                .Where(s => s.Mark != targetMark && !s.IsEmpty()).Reverse().ToList();
            var blockingMarks = new HashSet<string>(pickBlockingStations.Concat(dropBlockingStations).Select(s => s.Mark));
            var blockingStations = targetPocketKey == destinationPocketKey
                ? targetStations.AsEnumerable().Reverse().Where(s => blockingMarks.Contains(s.Mark)).ToList()
                : dropBlockingStations.Concat(pickBlockingStations).GroupBy(s => s.Mark).Select(g => g.First()).ToList();
            // 同巷道向深处搬运时，目标货物可能挡住更深处阻碍的取货路径。
            // 将目标货物按外到内顺序暂存到其他巷道，最后从暂存位搬到指定卸货位。
            var stageTarget = targetPocketKey == destinationPocketKey &&
                blockingStations.Any(s => targetStations.FindIndex(x => x.Mark == s.Mark) < targetIndex);
            if (stageTarget)
                blockingStations = targetStations.AsEnumerable().Reverse()
                    .Where(s => s.Mark == targetMark || blockingMarks.Contains(s.Mark)).ToList();
            var finalFromMark = targetMark;
            string stagedTargetPocketKey = null;
            Tools.trace($"[两端清障] 取货端={pickBlockingStations.Count}, 卸货端={dropBlockingStations.Count}, 去重后={blockingStations.Count}");

            Tools.trace($"[点到点搬运] 目标站点={targetMark}, 巷道位置={targetIndex}, 阻挡货物数={blockingStations.Count}");

            if (blockingStations.Count == 0)
            {
                Tools.trace("[点到点搬运] 未检测到阻挡货物，直接创建普通移库任务");
                return CreateSimpleStationMoveTask(targetMark, toMark, agvName, cargoName, cargoTag);
            }

            foreach (var blockingStation in blockingStations)
            {
                if (!stationCargoAreas.ContainsKey(blockingStation.Mark))
                {
                    Tools.trace($"[清障规划失败] 阻碍货位 {blockingStation.Mark} 朝向配置无效");
                    return false;
                }
                // 模拟前面步骤的存放占用，只使用入口方向连续可达的空位，从深到浅填充。
                var clearanceStation = cargoPockets.Values
                    .Where(p => p.Error == null && p.Key != targetPocketKey &&
                        p.Key != destinationPocketKey && p.Key != stagedTargetPocketKey && !PocketLocks.ContainsKey(p.Key))
                    .OrderBy(p => p.Key)
                    .Select(p => p.Slots.AsEnumerable().Reverse()
                        .TakeWhile(s => s.IsEmpty() && !reservedMarks.Contains(s.Mark))
                        .Reverse().FirstOrDefault(s => stationCargoAreas.ContainsKey(s.Mark)))
                    .FirstOrDefault(s => s != null);

                if (clearanceStation == null)
                {
                    Tools.trace("[清障规划失败] 其他巷道的可达空位不足，未创建任何搬运任务");
                    return false;
                }

                reservedMarks.Add(clearanceStation.Mark);
                if (blockingStation.Mark == targetMark)
                {
                    finalFromMark = clearanceStation.Mark;
                    // 暂存目标所在巷道不再放其他清障货物，保证最终可以取出。
                    stagedTargetPocketKey = GetStationPocketKey(clearanceStation.Mark);
                }
                steps.Add(new TaskInfo
                {
                    ExpectedCargoName = blockingStation.Mark == targetMark ? cargoName : null,
                    ExpectedCargoTag = blockingStation.Mark == targetMark ? cargoTag : null,
                    FromMark = blockingStation.Mark,
                    ToMark = clearanceStation.Mark,
                    DynamicDrop = false,
                    RequestedAgv = agvName,
                    TaskType = TaskTypeEnum.移库任务
                });
            }

            steps.Add(new TaskInfo
            {
                ExpectedCargoName = cargoName,
                ExpectedCargoTag = cargoTag,
                FromMark = finalFromMark,
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

            Tools.trace($"[清障计划] 目标货物 {targetMark} -> {toMark}，共 {steps.Count} 个步骤（串行清理两端阻碍，再搬目标货物；不包含搬回原位）");
            return true;
        }

        bool ActivateNextClearanceTask(string clearanceBatchId)
        {
            if (!PendingClearanceBatches.TryGetValue(clearanceBatchId, out var queue) || queue.Count == 0)
            {
                PendingClearanceBatches.Remove(clearanceBatchId);
                return true;
            }

            // 前一步完整退出巷道并结束后，才允许激活下一步。
            if (TaskInfos.Any(t => t.ClearanceBatchId == clearanceBatchId && t.Status != TaskStatus.Finished))
                return false;
            var task = queue.Peek();
            if (TaskInfos.Count(t => t.Status != TaskStatus.Finished) >= MaxParallelMoveTask) return false;
            var fromStation = API.GetStation(task.FromMark);
            var toStation = API.GetStation(task.ToMark);
            if (fromStation == null || toStation == null || fromStation.IsEmpty() || !toStation.IsEmpty())
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
