using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.FnosSync;

public sealed class LightCheckTask : IScheduledTask
{
    readonly SyncService service;
    public LightCheckTask(SyncService service)=>this.service=service;
    public string Name=>"飞牛同步：轻量检查";
    public string Key=>"FnosSyncLightCheck";
    public string Description=>"读取飞牛收藏与继续观看小集合，将变化条目交给同步队列。收藏与已观看事件推送不依赖此任务。停用插件同步时不执行。";
    public string Category=>"飞牛媒体库同步";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()=>new[]{new TaskTriggerInfo{Type=TaskTriggerInfoType.IntervalTrigger,IntervalTicks=TimeSpan.FromSeconds(Math.Clamp(Plugin.Instance.Configuration.PollSeconds,5,86400)).Ticks}};
    public async Task ExecuteAsync(IProgress<double> progress,CancellationToken cancellationToken)
    {progress.Report(0);await service.Poll(cancellationToken);progress.Report(100);}
}

public sealed class FullCheckTask : IScheduledTask
{
    readonly SyncService service;
    public FullCheckTask(SyncService service)=>this.service=service;
    public string Name=>"飞牛同步：完整核对";
    public string Key=>"FnosSyncFullCheck";
    public string Description=>"按设置发现新媒体映射，核对全部已保存映射的收藏和观看状态。可查看进度、取消或设置触发器。停用插件同步时不执行。";
    public string Category=>"飞牛媒体库同步";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var c=Plugin.Instance.Configuration;
        // Native DailyTrigger uses server local time. Preserve the old configured
        // wall-clock schedule when first migrating, then Jellyfin owns triggers.
        var zone=TimeZoneInfo.FindSystemTimeZoneById(c.TimeZone);
        var date=TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,zone).Date.AddHours(Math.Clamp(c.FullSyncHour,0,23));
        var local=TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(date,DateTimeKind.Unspecified),zone,TimeZoneInfo.Local);
        return new[]{new TaskTriggerInfo{Type=TaskTriggerInfoType.DailyTrigger,TimeOfDayTicks=local.TimeOfDay.Ticks}};
    }
    public async Task ExecuteAsync(IProgress<double> progress,CancellationToken cancellationToken)
    {progress.Report(0);await service.Full(cancellationToken,progress);progress.Report(100);}
}
