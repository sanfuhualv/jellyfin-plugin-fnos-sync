using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("1.0.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETCoreApp,Version=v10.0")]

namespace Jellyfin.Plugin.FnosSync;

public sealed class Mapping
{
    public string JellyfinId { get; set; } = "";
    public string FnosId { get; set; } = "";
    public string Title { get; set; } = "";
}

public sealed class Configuration : BasePluginConfiguration
{
    public bool Enabled { get; set; }
    public string FnosUrl { get; set; } = "";
    public string FnosUsername { get; set; } = "";
    public string JellyfinUserId { get; set; } = "";
    // Legacy migration defaults only. Native task trigger files are authoritative.
    public int PollSeconds { get; set; } = 900;
    public int DetailCacheSeconds { get; set; } = 5;
    public int ProgressPushSeconds { get; set; } = 30;
    public int FullSyncHour { get; set; } = 3;
    public string TimeZone { get; set; } = "Asia/Shanghai";
    public string InitialPolicy { get; set; } = "baseline";
    public string ConflictPolicy { get; set; } = "preserve";
    public bool DiscoverNewItemsDaily { get; set; } = true;
    public List<Mapping> Mappings { get; set; } = new();
}

public sealed class Plugin : BasePlugin<Configuration>, IHasWebPages
{
    public const string ReleaseVersion = "1.0.0";
    public static Plugin Instance { get; private set; } = null!;
    public override string Name => "飞牛实时同步";
    public override Guid Id => Guid.Parse("918b455a-b767-45b2-8f9b-a1e44de57e91");
    public override string Description => "飞牛影音收藏与观看状态：事件推送、页面按需刷新、每日核对。";
    public string StateDirectory { get; }
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer)
    {
        Instance = this;
        StateDirectory = Path.Combine(paths.DataPath, "fnos-sync");
        Directory.CreateDirectory(StateDirectory);
        Credentials = new SessionCredentials(StateDirectory);
    }
    public IEnumerable<PluginPageInfo> GetPages() => new[] {
        new PluginPageInfo { Name = "FnosSync", EmbeddedResourcePath = "Jellyfin.Plugin.FnosSync.configuration.html" }
    };
    public SessionCredentials Credentials { get; }
    public static string AccountScope(Configuration c)=>c.FnosUrl.TrimEnd('/')+"|"+c.FnosUsername+"|"+c.JellyfinUserId;
    public bool HasPassword(Configuration? configuration=null)=>Credentials.Has(AccountScope(configuration??Configuration));
    public string Password()=>Credentials.Get(AccountScope(Configuration));

}

public sealed class Registrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<SyncService>();
        services.AddSingleton<IHostedService>(s=>s.GetRequiredService<SyncService>());
        services.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask,LightCheckTask>();
        services.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask,FullCheckTask>();
        services.AddTransient<ReadRefreshFilter>();
        services.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter,WebRefreshStartup>();
        services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(o=>o.Filters.AddService<ReadRefreshFilter>());
    }
}

public sealed record MediaState(bool Favorite, bool Played, double Seconds)
{
    // fnOS deliberately clears watched_ts when marking an item watched. Once
    // both sides are complete, their different stored resume positions are not
    // a conflict. Unwatched rewind/reset still compare the exact position.
    public bool SamePlayback(MediaState other) => Played==other.Played && (Played || Math.Abs(Seconds-other.Seconds)<=2);
}
public sealed class PairState
{
    public string FnosId { get; set; } = "";
    public MediaState Jellyfin { get; set; } = new(false,false,0);
    public MediaState Fnos { get; set; } = new(false,false,0);
}
public sealed class PendingWork
{
    public bool FavoriteIntent { get; set; }
    public bool PlaybackIntent { get; set; }
    public long Revision { get; set; }
    public string Reason { get; set; } = "event";
    public DateTimeOffset Due { get; set; }
}
public sealed class StoredState
{
    public long ClientRevision { get; set; }
    public string Scope { get; set; } = "";
    public Dictionary<string,PairState> Baselines { get; set; } = new();
    public Dictionary<string,PendingWork> Pending { get; set; } = new();
    public DateTimeOffset? LastFullSync { get; set; }
}
public sealed record SyncResult(string ItemId, string Reason, int Changes, int Conflicts, double Milliseconds, string Status);

public static class Reconcile
{
    // Favorite and playback are independent. A zero position and false are valid values.
    public static string Decide(bool equal, bool localChanged, bool remoteChanged, string initial, bool hasBaseline)
    {
        if(equal) return "equal";
        if(!hasBaseline) return initial=="fnos"?"pull":initial=="jellyfin"?"push":"baseline";
        if(localChanged&&!remoteChanged) return "push";
        if(remoteChanged&&!localChanged) return "pull";
        return localChanged&&remoteChanged?"conflict":"baseline";
    }
}
