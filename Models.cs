namespace ProfControl
{
    public partial class SampleProject
    {
        public enum TaskTypeEnum
        {
            移动任务,
            移库任务
        }

        public enum TaskStatus
        {
            None,
            GoPick,
            Picking,
            Picked,
            GoDrop,
            Dropping,
            Dropped,
            GoHome,
            Finished,
            GoMove,
            GoPickEntrance,
            GoPickExit,
            GoDropEntrance,
            GoDropExit,
            ExitedDrop,
            GoParkingExit,
        }

        public class TaskInfo
        {
            public string FromMark { get; set; }
            public string ToMark { get; set; }
            public string ParkingExitMark { get; set; }
            public string ParkingResumeMark { get; set; }
            public TaskStatus ParkingResumeStatus { get; set; }
            public string DropPocketKey { get; set; }
            public bool DynamicDrop { get; set; }
            public bool DropCapacityReserved { get; set; }
            public string PickEntranceMark { get; set; }
            public string DropEntranceMark { get; set; }
            public bool SamePocket { get; set; }
            public string LastWaitReason { get; set; }
            public IAgv WorkAgv { get; set; }
            public string RequestedAgv { get; set; }
            public TaskTypeEnum TaskType { get; set; }
            public TaskStatus Status { get; set; } = TaskStatus.None;
            public DateTime StateTime { get; set; } = DateTime.Now;

            // 巷道锁（取货 + 卸货）
            public string LockPickPocketKey { get; set; }
            public string LockDropPocketKey { get; set; }
            public string BatchId { get; set; }
            public int BatchSequence { get; set; }
        }

        public class SelectableStationInfo
        {
            public string StationMark { get; set; }
            public string PocketName { get; set; }
            public bool HasCargo { get; set; }
        }
    }
}
