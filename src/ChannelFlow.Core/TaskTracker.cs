namespace ChannelFlow.Core;

public class RunningTask
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Label { get; set; }
    public long Current { get; set; }
    public long Total { get; set; }
    public string Status { get; set; } = "running"; // running, done, failed
    public string? Detail { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
}

public class TaskTracker : ChannelFlow.PluginSdk.IPluginTaskHost
{
    private readonly List<RunningTask> _tasks = new();
    private readonly object _lock = new();

    public void Report(string key, string title, string? label, long? current, long? total, string status = "running", string? detail = null)
    {
        lock (_lock)
        {
            var existing = _tasks.FirstOrDefault(t => t.Key == key && t.Status == "running");
            if (existing is not null)
            {
                existing.Title = title;
                existing.Label = label;
                if (current.HasValue) existing.Current = current.Value;
                if (total.HasValue) existing.Total = total.Value;
                existing.Status = status;
                existing.Detail = detail ?? existing.Detail;
                if (status != "running") existing.FinishedAt = DateTime.UtcNow;
            }
            else
            {
                _tasks.Add(new RunningTask { Key = key, Title = title, Label = label, Current = current ?? 0, Total = total ?? 0, Status = status, Detail = detail });
            }
        }
    }

    public List<RunningTask> GetRunning() => _tasks.Where(t => t.Status == "running").ToList();
    public List<RunningTask> GetAll() => _tasks.ToList();
    public void ClearFinished()
    {
        lock (_lock) { _tasks.RemoveAll(t => t.Status != "running" && t.FinishedAt.HasValue && (DateTime.UtcNow - t.FinishedAt.Value).TotalSeconds > 5); }
    }
}
