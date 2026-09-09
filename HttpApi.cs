namespace ProfControl
{
    public partial class SampleProject
    {
        // 暴露 API 接口
        void StartHttpApi()
        {
            API.Http("http://localhost:8088", app =>
            {
                app.SupportCORS();

                app.MapGet("/api/agvs", async (HttpContext ctx) =>
                {
                    var arr = new JArray();
                    foreach (var agv in API.Agvs)
                    {
                        arr.Add(new JObject
                        {
                            ["Name"] = agv.Name,
                            ["状态"] = agv.GetValue<string>("状态"),
                            ["任务类型"] = agv.GetValue<string>("任务类型"),
                            ["当前站点"] = agv.API.On,
                            ["空闲"] = agv.IsFree(),
                        });
                    }
                    return Results.Text(arr.ToString());
                });

                app.MapGet("/api/tasks", async (HttpContext ctx) =>
                {
                    var arr = new JArray();
                    foreach (var task in TaskInfos)
                    {
                        arr.Add(new JObject
                        {
                            ["FromMark"] = task.FromMark,
                            ["ToMark"] = task.ToMark,
                            ["Status"] = task.Status.ToString(),
                            ["Agv"] = task.WorkAgv?.Name,
                            ["任务类型"] = task.TaskType.ToString(),
                            ["取货锁"] = task.LockPickPocketKey,
                            ["卸货锁"] = task.LockDropPocketKey,
                            ["目标巷道"] = task.DropPocketKey,
                            ["动态卸货位"] = task.DynamicDrop,
                            ["容量已预留"] = task.DropCapacityReserved,
                        });
                    }
                    return Results.Text(arr.ToString());
                });
            });
        }
    }
}
