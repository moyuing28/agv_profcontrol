namespace ProfControl
{
    public partial class SampleProject
    {
        private DateTime _lastTaskGenTime = DateTime.MinValue;

        void GenerateMoveTasks()
        {

            Tools.trace($"GenerateMoveTasks");

            if ((DateTime.Now - _lastTaskGenTime).TotalSeconds < TaskGenerationInterval)
            {
                return;
            }
            _lastTaskGenTime = DateTime.Now;

            var runningTasks = TaskInfos
                .Where(t => t.TaskType == TaskTypeEnum.移库任务 &&
                            t.Status != TaskStatus.Finished)
                .ToList();

            if (runningTasks.Count >= MaxParallelMoveTask)
                return;

            var garageSub = SubCategoryCollection.Instance[GarageArea] as ISubCategory;
            if (garageSub == null)
            {
                Tools.trace($"garageSub == null");
                return;
            }

            // 车库位中的有车站点和空站点都可以互相搬运。
            var garageStations = garageSub.Children.Cast<IStation>().ToList();
            var pickStations = garageStations
                .Where(s => !s.IsEmpty())
                .ToList();

            var dropStations = garageStations
                .Where(s => s.IsEmpty())
                .ToList();

            if (pickStations.Count == 0)
            {
                is_GoHome = true;
                SendFreeAgvsHome();
                Tools.trace($"pickStations.Count == 0");
                return;
            }
            if (dropStations.Count == 0)
            {
                is_GoHome = true;
                SendFreeAgvsHome();
                Tools.trace($"dropStations.Count == 0");
                return;
            }
            // ✅ 取货和卸货站点都可用时，重置回家模式，让 AGV 继续工作
            if (is_GoHome)
            {
                is_GoHome = false;
                Tools.trace("is_GoHome reset to false — both pick and drop stations available");
            }

            // 找到 pick 站点所对应的所有巷道（去重）
            var pickPockets = pickStations
                .SelectMany(x => x.BelongPocketLines)
                .Distinct()
                .ToList();

            // 找到 drop 站点所对应的所有巷道（去重）
            var dropPockets = dropStations
                .SelectMany(x => x.BelongPocketLines)
                .Distinct()
                .ToList();

            // 为每个可用的 pick 巷道生成任务
            foreach (var pickPocket in pickPockets)
            {
                if (runningTasks.Count >= MaxParallelMoveTask)
                    break;

                // 找到该巷道中末尾的有货站点作为 FromMark
                var fromSta = pickPocket.OrderStations
                    .LastOrDefault(s => pickStations.Contains(s) &&
                                       !TaskInfos.Any(t => t.Status != TaskStatus.Finished &&
                                                          t.FromMark == s.Mark));
                if (fromSta == null)
                    continue;

                // 使用巷道首站Mark作为 pickKey（巷道唯一标识）
                var pickKey = pickPocket.OrderStations.First().Mark;

                // 如果该 pick 巷道已被锁定，跳过
                if (PocketLocks.ContainsKey(pickKey))
                    continue;

                // 找一个可用的 drop 巷道
                IStation toSta = null;
                string dropKey = null;

                foreach (var dropPocket in dropPockets)
                {
                    // 找到该巷道中首个空站点作为 ToMark
                    toSta = dropPocket.OrderStations
                        .FirstOrDefault(s => dropStations.Contains(s) &&
                                            !TaskInfos.Any(t => t.Status != TaskStatus.Finished &&
                                                               t.ToMark == s.Mark));
                    if (toSta == null)
                        continue;

                    // 使用巷道首站Mark作为 dropKey（巷道唯一标识）
                    dropKey = dropPocket.OrderStations.First().Mark;

                    // 如果该 drop 巷道未被锁定，则使用它
                    if (dropKey != pickKey)
                        break;

                    // 否则继续找下一个 drop 巷道
                    toSta = null;
                    dropKey = null;
                }

                if (toSta == null || dropKey == null)
                    continue;

                // 生成任务
                var task = new TaskInfo
                {
                    FromMark = fromSta.Mark,
                    ToMark = toSta.Mark,
                    TaskType = TaskTypeEnum.移库任务,
                    LockPickPocketKey = pickKey,
                    DropPocketKey = dropKey,
                    DynamicDrop = true
                };

                // 加锁
                if (!ReserveDropCapacity(task) || !PrepareEntrances(task)) continue;
                PocketLocks[pickKey] = task;

                TaskInfos.Add(task);
                runningTasks.Add(task);
            }
        }

    }
}
