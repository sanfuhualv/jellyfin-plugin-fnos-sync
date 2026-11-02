using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.FnosSync;

public sealed class SyncService : BackgroundService
{
    readonly IUserDataManager data;
    readonly IUserManager users;
    readonly ILibraryManager library;
    readonly ILogger<SyncService> logger;
    readonly IHttpContextAccessor requests;
    readonly SemaphoreSlim engine=new(1,1);
    readonly SemaphoreSlim fullAudit=new(1,1);
    readonly object stateLock=new();
    readonly HashSet<string> suppressed=new();
    readonly Dictionary<string,DateTimeOffset> touched=new();
    readonly Dictionary<string,DateTimeOffset> active=new();
    readonly Dictionary<string,string> lightFingerprints=new();
    readonly Queue<object> history=new();
    StoredState state;
    readonly FnosClient fn=new();
    bool lightPollRequested;
    DateTimeOffset lastViewPoll=DateTimeOffset.MinValue;
    public void WakeLightPoll(){lock(stateLock){if((DateTimeOffset.UtcNow-lastViewPoll).TotalSeconds<5)return;lastViewPoll=DateTimeOffset.UtcNow;lightPollRequested=true;}}
    public SyncService(IUserDataManager data,IUserManager users,ILibraryManager library,ILogger<SyncService> logger,IHttpContextAccessor requests)
    {
        this.data=data;this.users=users;this.library=library;this.logger=logger;this.requests=requests;
        try {state=JsonSerializer.Deserialize<StoredState>(File.ReadAllText(StatePath),Plugin.Json)??new();}
        catch(FileNotFoundException){state=new();}
        catch(Exception e){throw new InvalidOperationException("飞牛同步基线无法读取，请恢复备份，不自动重置",e);}
    }
    string StatePath=>Path.Combine(Plugin.Instance.StateDirectory,"state.json");
    Configuration Config=>Plugin.Instance.Configuration;
    public object ClientInfo(bool wake,IEnumerable<string> ids)
    {
        if(!Config.Enabled||!Plugin.Instance.HasPassword())return new{enabled=false,revision=0L};
        if(wake){Touch(ids);WakeLightPoll();}
        lock(stateLock)return new{enabled=true,revision=state.ClientRevision};
    }
    string Scope=>Config.FnosUrl.TrimEnd('/')+"|"+Config.FnosUsername+"|"+Config.JellyfinUserId;
    public async Task Configure(Configuration config,CancellationToken ct,string? password=null)
    {
        await engine.WaitAsync(ct);
        try
        {
            Plugin.Instance.Credentials.Update(Plugin.AccountScope(config),password);
            Plugin.Instance.UpdateConfiguration(config);EnsureScope();
            lock(stateLock)
            {
                foreach(var id in state.Baselines.Keys.ToArray())
                    if(!config.Mappings.Any(m=>m.JellyfinId==id&&m.FnosId==state.Baselines[id].FnosId))state.Baselines.Remove(id);
                foreach(var id in state.Pending.Keys.ToArray())if(!Mapped(id))state.Pending.Remove(id);
                Save();
            }
        }
        finally{engine.Release();}
    }
    // Administrator recovery path: restore an explicit complete backup while
    // synchronization is disabled. Unlike nullable patch DTOs this can clear
    // LastPlayedDate back to null, which is necessary for exact rollback.
    public async Task<object> RestoreLocal(LocalBackup backup,CancellationToken ct)
    {
        await engine.WaitAsync(ct);
        try
        {
            if(Config.Enabled)throw new InvalidOperationException("恢复备份前请先停用插件同步");
            var id=Guid.Parse(backup.ItemId);if(!Mapped(id.ToString("N")))throw new InvalidOperationException("仅可恢复当前明确映射的影片");
            var user=users.GetUserById(Guid.Parse(Config.JellyfinUserId))??throw new InvalidOperationException("用户不存在");
            var item=library.GetItemById(id)??throw new InvalidOperationException("影片不存在");
            var value=data.GetUserData(user,item)??new UserItemData{Key=item.GetUserDataKeys().First()};
            value.IsFavorite=backup.IsFavorite;value.Played=backup.Played;value.PlaybackPositionTicks=backup.PlaybackPositionTicks;
            value.PlayCount=backup.PlayCount;value.LastPlayedDate=backup.LastPlayedDate;
            data.SaveUserData(user,item,value,UserDataSaveReason.Import,ct);
            lock(stateLock){state.Baselines.Remove(id.ToString("N"));state.Pending.Remove(id.ToString("N"));Save();}
            Log("restore",new{itemId=id});return new{restored=true,itemId=id};
        }
        finally{engine.Release();}
    }
    void EnsureScope()
    {
        lock(stateLock)
        {
            if(state.Scope==Scope)return;
            state=new StoredState{Scope=Scope}; touched.Clear();active.Clear();lightFingerprints.Clear();Save();
        }
    }
    void Save()
    {
        var tmp=StatePath+".tmp";
        File.WriteAllText(tmp,JsonSerializer.Serialize(state,Plugin.Json));File.Move(tmp,StatePath,true);
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(StatePath,UnixFileMode.UserRead|UnixFileMode.UserWrite);
    }
    void Log(string action,object details)
    {
        lock(stateLock)
        {
            var entry=new {at=DateTimeOffset.UtcNow,action,details};history.Enqueue(entry);while(history.Count>200)history.Dequeue();
            var path=Path.Combine(Plugin.Instance.StateDirectory,"events.jsonl");
            if(File.Exists(path)&&new FileInfo(path).Length>4*1024*1024)File.Move(path,path+".previous",true);
            File.AppendAllText(path,JsonSerializer.Serialize(entry)+Environment.NewLine);
        }
    }
    public object Status()
    {
        lock(stateLock)return new{version=Plugin.ReleaseVersion,enabled=Config.Enabled,mappings=Config.Mappings.Count,
            authenticationRequired=Config.Enabled&&!Plugin.Instance.HasPassword(),passwordConfigured=Plugin.Instance.HasPassword(),
            credentialError=Plugin.Instance.Credentials.StorageError,
            baselines=state.Baselines.Count,pending=state.Pending.Count,lastFullSync=state.LastFullSync,
            nativeRequests=fn.Requests,events=history.ToArray()};
    }
    static string Canonical(string id)=>Guid.TryParse(id,out var guid)?guid.ToString("N"):id;
    bool Mapped(string id)=>Config.Mappings.Any(m=>Canonical(m.JellyfinId)==Canonical(id));
    public void Touch(IEnumerable<string> ids,string reason="view")
    {
        if(!Config.Enabled)return;EnsureScope();
        lock(stateLock)
        {
            foreach(var rawId in ids.Distinct().Take(100))
            {
                var id=Canonical(rawId);if(!Mapped(id))continue;
                if(reason=="view"&&touched.TryGetValue(id,out var last)&&(DateTimeOffset.UtcNow-last).TotalSeconds<Config.DetailCacheSeconds)
                {
                    // A cached read still needs one refresh when the cache
                    // expires; dropping it could leave the current page stale.
                    if(!state.Pending.ContainsKey(id))state.Pending[id]=new PendingWork{Reason=reason,Revision=1,Due=last.AddSeconds(Config.DetailCacheSeconds)};
                    continue;
                }
                touched[id]=DateTimeOffset.UtcNow;
                if(state.Pending.TryGetValue(id,out var existing))
                {
                    // A confirmed small-list change invalidates a view cache
                    // and must survive acknowledgement of an older queued job.
                    if(reason!="view"){existing.Revision++;existing.Reason=reason;existing.Due=DateTimeOffset.UtcNow;}
                }
                else state.Pending[id]=new PendingWork{Reason=reason,Revision=1,Due=DateTimeOffset.UtcNow};
            }
            Save();
        }
    }
    void UserSaved(object? sender,UserDataSaveEventArgs e)
    {
        try
        {
            if(!Config.Enabled||e.Item==null||Canonical(e.UserId.ToString())!=Canonical(Config.JellyfinUserId))return;
            var id=e.Item.Id.ToString("N");
            if(!Mapped(id)){Log("unmapped-action",new{itemId=id,title=e.Item.Name,reason=e.SaveReason.ToString(),message="此影片尚无唯一媒体映射，本次操作未同步。请发现并保存映射。"});return;}
            EnsureScope();
            lock(stateLock)
            {
                if(suppressed.Contains(id))return;
                string reason=e.SaveReason.ToString();
                var request=requests.HttpContext;
                var path=request?.Request.Path.Value??"";
                bool favoriteIntent=path.Contains("/FavoriteItems/",StringComparison.OrdinalIgnoreCase)||path.Contains("/UserFavoriteItems/",StringComparison.OrdinalIgnoreCase)||request?.Items["FnosSync.FavoriteIntent"] is true;
                bool playbackIntent=reason is "PlaybackProgress" or "PlaybackFinished" or "TogglePlayed"||request?.Items["FnosSync.PlaybackIntent"] is true;
                if(reason is "PlaybackStart" or "PlaybackProgress")active[id]=DateTimeOffset.UtcNow.AddSeconds(90);
                if(reason=="PlaybackFinished")active.Remove(id);
                bool progress=reason=="PlaybackProgress";
                var due=DateTimeOffset.UtcNow.AddSeconds(progress?Config.ProgressPushSeconds:0);
                if(state.Pending.TryGetValue(id,out var pending))
                {pending.Revision++;pending.FavoriteIntent|=favoriteIntent;pending.PlaybackIntent|=playbackIntent;if(due<pending.Due)pending.Due=due;pending.Reason=progress&&pending.Reason!="PlaybackProgress"?pending.Reason:reason;}
                else state.Pending[id]=new PendingWork{Revision=1,Reason=reason,Due=due,FavoriteIntent=favoriteIntent,PlaybackIntent=playbackIntent};
                Save();
            }
        }
        catch(Exception ex){logger.LogError(ex,"Failed to persist fnOS sync event");}
    }
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        data.UserDataSaved+=UserSaved;
        try
        {
            while(!ct.IsCancellationRequested)
            {
                if(Config.Enabled&&Plugin.Instance.HasPassword())
                {
                    try
                    {
                        EnsureScope();
                        List<KeyValuePair<string,PendingWork>> jobs;
                        lock(stateLock)jobs=state.Pending.Where(p=>p.Value.Due<=DateTimeOffset.UtcNow).OrderBy(p=>p.Value.FavoriteIntent||p.Value.PlaybackIntent?0:p.Value.Reason=="view"?2:1).ThenBy(p=>p.Value.Due).Select(p=>new KeyValuePair<string,PendingWork>(p.Key,new PendingWork{Reason=p.Value.Reason,Revision=p.Value.Revision,Due=p.Value.Due,FavoriteIntent=p.Value.FavoriteIntent,PlaybackIntent=p.Value.PlaybackIntent})).Take(20).ToList();
                        foreach(var job in jobs)
                        {
                            try
                            {
                                var result=await SyncOne(job.Key,job.Value.Reason,ct,job.Value.FavoriteIntent,job.Value.PlaybackIntent);
                                lock(stateLock)
                                {
                                    if(state.Pending.TryGetValue(job.Key,out var current)&&current.Revision==job.Value.Revision)
                                    {
                                        if(result.Status is "ok" or "baseline" or "conflict")state.Pending.Remove(job.Key);
                                        else current.Due=DateTimeOffset.UtcNow.AddSeconds(10);
                                    }
                                    Save();
                                }
                            }
                            catch(Exception ex)when(!ct.IsCancellationRequested)
                            {
                                Log("retry",new{itemId=job.Key,error=ex.Message});
                                lock(stateLock){if(state.Pending.TryGetValue(job.Key,out var current))current.Due=DateTimeOffset.UtcNow.AddSeconds(10);Save();}
                            }
                        }
                        bool wake;
                        lock(stateLock){wake=lightPollRequested;lightPollRequested=false;}
                        if(wake)await Poll(ct);
                    }
                    catch(Exception ex)when(!ct.IsCancellationRequested){Log("background-error",new{error=ex.Message});}
                }
                await Task.Delay(150,ct);
            }
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){}
        finally{data.UserDataSaved-=UserSaved;}
    }
    public override void Dispose(){fn.Dispose();engine.Dispose();fullAudit.Dispose();base.Dispose();}
    MediaState Local(BaseItem item)
    {
        var user=users.GetUserById(Guid.Parse(Config.JellyfinUserId))??throw new InvalidOperationException("Jellyfin 用户不存在");
        var value=data.GetUserData(user,item);
        return value==null?new(false,false,0):new(value.IsFavorite,value.Played,value.PlaybackPositionTicks/10000000d);
    }
    bool PlaybackActive(string id){lock(stateLock)return active.TryGetValue(id,out var until)&&until>DateTimeOffset.UtcNow;}
    void WriteLocal(BaseItem item,MediaState target,bool favorite,bool playback)
    {
        var user=users.GetUserById(Guid.Parse(Config.JellyfinUserId))??throw new InvalidOperationException("Jellyfin 用户不存在");
        var patch=new UpdateUserItemDataDto();
        if(favorite)patch.IsFavorite=target.Favorite;
        if(playback){patch.Played=target.Played;patch.PlaybackPositionTicks=(long)(target.Seconds*10000000);if(target.Seconds>0||target.Played)patch.LastPlayedDate=DateTime.UtcNow;}
        var id=item.Id.ToString("N");lock(stateLock)suppressed.Add(id);
        try{data.SaveUserData(user,item,patch,UserDataSaveReason.UpdateUserData);}
        finally{lock(stateLock)suppressed.Remove(id);}
        lock(stateLock){state.ClientRevision++;Save();}
    }
    public async Task<SyncResult> SyncOne(string rawId,string reason,CancellationToken ct,bool favoriteIntent=false,bool playbackIntent=false)
    {
        var watch=Stopwatch.StartNew();var id=Canonical(rawId);
        if(!Config.Enabled)return new(id,reason,0,0,0,"disabled");
        await engine.WaitAsync(ct);
        try
        {
            EnsureScope();
            var mapping=Config.Mappings.SingleOrDefault(m=>Canonical(m.JellyfinId)==id)??throw new InvalidOperationException("该影片没有唯一映射");
            var item=library.GetItemById(Guid.Parse(id))??throw new InvalidOperationException("Jellyfin 影片不存在");
            var j=Local(item);var f=await fn.Read(mapping.FnosId,ct);
            if(Local(item)!=j)return new(id,reason,0,0,watch.Elapsed.TotalMilliseconds,"changed-during-read");
            PairState? prior;lock(stateLock)state.Baselines.TryGetValue(id,out prior);
            if(prior!=null&&prior.FnosId!=mapping.FnosId)prior=null;
            bool has=prior!=null;prior??=new PairState{Jellyfin=j,Fnos=f};
            var fav=Reconcile.Decide(j.Favorite==f.Favorite,j.Favorite!=prior.Jellyfin.Favorite,f.Favorite!=prior.Fnos.Favorite,Config.InitialPolicy,has);
            var play=Reconcile.Decide(j.SamePlayback(f),!j.SamePlayback(prior.Jellyfin),!f.SamePlayback(prior.Fnos),Config.InitialPolicy,has);
            if(!has&&favoriteIntent&&j.Favorite!=f.Favorite)fav="push";
            if(!has&&playbackIntent&&!j.SamePlayback(f))play="push";
            if(play=="conflict"&&Config.ConflictPolicy=="most-progress")
            {
                if(j.Played!=f.Played)play=j.Played?"push":"pull";
                else if(Math.Abs(j.Seconds-f.Seconds)>2)play=j.Seconds>f.Seconds?"push":"pull";
            }
            if(play=="pull"&&PlaybackActive(id))play="active";
            var desiredJ=j;var desiredF=f;int changes=0,conflicts=(fav=="conflict"?1:0)+(play=="conflict"?1:0);
            if(fav=="push")desiredF=desiredF with{Favorite=j.Favorite};
            if(fav=="pull")desiredJ=desiredJ with{Favorite=f.Favorite};
            if(play=="push")desiredF=desiredF with{Played=j.Played,Seconds=j.Seconds};
            if(play=="pull")desiredJ=desiredJ with{Played=f.Played,Seconds=f.Seconds};
            if(play=="push"&&item is not (Movie or Episode))play="unsupported";
            // Verify the remote again before a write to avoid using a stale observation.
            bool writeF=fav=="push"||play=="push",writeJ=fav=="pull"||play=="pull";
            if(writeF||writeJ)
            {
                var currentF=await fn.Read(mapping.FnosId,ct);
                if(currentF!=f||Local(item)!=j)return new(id,reason,0,conflicts,watch.Elapsed.TotalMilliseconds,"changed-before-write");
                if(play=="push"&&item.RunTimeTicks.HasValue&&j.Seconds>item.RunTimeTicks.Value/10000000d+2)throw new InvalidOperationException("进度超过 Jellyfin 当前版本时长");
                if(fav=="push"){await fn.Favorite(mapping.FnosId,desiredF.Favorite,ct);changes++;}
                if(play=="push"){await fn.Playback(mapping.FnosId,desiredF,ct);changes++;}
                var verifiedF=await fn.Read(mapping.FnosId,ct);
                if((fav=="push"&&verifiedF.Favorite!=desiredF.Favorite)||(play=="push"&&!verifiedF.SamePlayback(desiredF)))throw new InvalidOperationException("飞牛写后核验未通过");
                if(Local(item)!=j)return new(id,reason,changes,conflicts,watch.Elapsed.TotalMilliseconds,"local-changed-during-write");
                if(writeJ)
                {
                    if(play=="pull"&&item.RunTimeTicks.HasValue&&f.Seconds>item.RunTimeTicks.Value/10000000d+2)throw new InvalidOperationException("飞牛进度超过 Jellyfin 当前版本时长");
                    WriteLocal(item,desiredJ,fav=="pull",play=="pull");
                    if(fav=="pull")changes++;if(play=="pull")changes++;
                }
                var verifiedJ=Local(item);
                if((fav=="pull"&&verifiedJ.Favorite!=desiredJ.Favorite)||(play=="pull"&&!verifiedJ.SamePlayback(desiredJ)))throw new InvalidOperationException("Jellyfin 写后核验未通过");
                j=verifiedJ;f=verifiedF;
            }
            lock(stateLock)
            {
                // Only successful fields advance; unresolved conflicts retain their old observation.
                var next=new PairState{FnosId=mapping.FnosId,Jellyfin=prior.Jellyfin,Fnos=prior.Fnos};
                if(fav!="conflict"){next.Jellyfin=next.Jellyfin with{Favorite=j.Favorite};next.Fnos=next.Fnos with{Favorite=f.Favorite};}
                if(play is not ("conflict" or "active" or "unsupported")){next.Jellyfin=next.Jellyfin with{Played=j.Played,Seconds=j.Seconds};next.Fnos=next.Fnos with{Played=f.Played,Seconds=f.Seconds};}
                state.Baselines[id]=next;Save();
            }
            var result=new SyncResult(id,reason,changes,conflicts,watch.Elapsed.TotalMilliseconds,conflicts>0?"conflict":!has&&changes==0?"baseline":play=="active"?"active":"ok");
            Log("sync",result);return result;
        }
        finally{engine.Release();}
    }
    public async Task<object> Poll(CancellationToken ct)
    {
        if(!Config.Enabled)return new{status="disabled"};
        await engine.WaitAsync(ct);var watch=Stopwatch.StartNew();
        try
        {
            var favorites=await fn.Paged("/v/api/v1/favorite/list",ct);
            var playing=await fn.Api("GET","/v/api/v1/play/list",null,ct);
            if(playing.ValueKind!=JsonValueKind.Array)throw new InvalidOperationException("飞牛继续观看列表不完整");
            var current=new Dictionary<string,string>();
            foreach(var row in favorites)current["f:"+FnosClient.Text(row,"guid")]="1";
            foreach(var row in playing.EnumerateArray())current["p:"+FnosClient.Text(row,"guid")]=FnosClient.Text(row,"ts")+":"+FnosClient.Text(row,"watched");
            var changed=current.Keys.Union(lightFingerprints.Keys).Where(k=>!current.TryGetValue(k,out var a)||!lightFingerprints.TryGetValue(k,out var b)||a!=b).Select(k=>k[2..]).ToHashSet();
            // A complete audit may observe a newer state between light checks.
            // Compare with the persisted baseline too: reverting to a previous
            // small-list fingerprint is still a real change after that audit.
            var positions=playing.EnumerateArray().ToDictionary(r=>FnosClient.Text(r,"guid"),r=>new MediaState(false,FnosClient.Flag(r,"watched"),FnosClient.Number(r,"ts")));
            lock(stateLock)
            {
                foreach(var mapping in Config.Mappings)
                {
                    if(!state.Baselines.TryGetValue(mapping.JellyfinId,out var prior))continue;
                    if(prior.Fnos.Favorite!=current.ContainsKey("f:"+mapping.FnosId))changed.Add(mapping.FnosId);
                    bool listed=positions.TryGetValue(mapping.FnosId,out var position);
                    if(listed?!position!.SamePlayback(prior.Fnos):!prior.Fnos.Played&&prior.Fnos.Seconds>0)changed.Add(mapping.FnosId);
                }
            }
            lightFingerprints.Clear();foreach(var pair in current)lightFingerprints[pair.Key]=pair.Value;
            var ids=Config.Mappings.Where(m=>changed.Contains(m.FnosId)).Select(m=>m.JellyfinId).ToList();Touch(ids,"light-poll");
            var report=new{queued=ids.Count,elapsedMs=watch.Elapsed.TotalMilliseconds};Log("light-poll",report);return report;
        }
        finally{engine.Release();}
    }
    public async Task<object> Full(CancellationToken ct,IProgress<double>? progress=null)
    {
        await fullAudit.WaitAsync(ct);
        try
        {
        var watch=Stopwatch.StartNew();var results=new List<SyncResult>();
        if(!Config.Enabled)return new{status="disabled"};
        int added=0;
        if(Config.DiscoverNewItemsDaily)
        {
            var discovery=await Discover(ct);
            foreach(var candidate in discovery.mappings)
                if(!Config.Mappings.Any(m=>m.JellyfinId==candidate.JellyfinId||m.FnosId==candidate.FnosId)){Config.Mappings.Add(candidate);added++;}
            if(added>0)await Configure(Config,ct);
        }
        var mappings=Config.Mappings.ToArray();
        foreach(var mapping in mappings){ct.ThrowIfCancellationRequested();results.Add(await SyncOne(mapping.JellyfinId,"daily-full",ct));progress?.Report(100d*results.Count/Math.Max(1,mappings.Length));await Task.Delay(50,ct);}
        lock(stateLock){state.LastFullSync=DateTimeOffset.UtcNow;Save();}
        var report=new{items=results.Count,newMappings=added,changes=results.Sum(r=>r.Changes),conflicts=results.Sum(r=>r.Conflicts),elapsedMs=watch.Elapsed.TotalMilliseconds};Log("full",report);return report;
        }
        finally{fullAudit.Release();}
    }
    public async Task<object> Test(CancellationToken ct)
    {await engine.WaitAsync(ct);try{await fn.Login(ct);return new{ok=true,requests=fn.Requests,transport="direct",environmentProxyWouldBypass=System.Net.Http.HttpClient.DefaultProxy.IsBypassed(new Uri(Config.FnosUrl))};}finally{engine.Release();}}
    public sealed record DiscoveryResult(List<Mapping> mappings,int unmatched,double elapsedMs,string note);
    public async Task<DiscoveryResult> Discover(CancellationToken ct)
    {
        await engine.WaitAsync(ct);try
        {
            var watch=Stopwatch.StartNew();var top=await fn.Paged("/v/api/v1/item/list",ct);
            var remote=new List<(string Id,string Kind,string Name,int Year,string Imdb,string Series,int Season,int Episode)>();
            foreach(var row in top)
            {
                var kind=FnosClient.Text(row,"type").ToLowerInvariant();var imdb=FnosClient.Text(row,"imdb_id");
                if(kind=="movie")remote.Add((FnosClient.Text(row,"guid"),"Movie",FnosClient.Text(row,"title"),Year(row),imdb,"",0,0));
                if(kind is "tv" or "series")
                {
                    remote.Add((FnosClient.Text(row,"guid"),"Series",FnosClient.Text(row,"title"),Year(row),imdb,"",0,0));
                    var seasons=await fn.Api("GET","/v/api/v1/season/list/"+FnosClient.Text(row,"guid"),null,ct);
                    foreach(var season in seasons.EnumerateArray())
                    {
                        var eps=await fn.Api("GET","/v/api/v1/episode/list/"+FnosClient.Text(season,"guid"),null,ct);
                        foreach(var ep in eps.EnumerateArray())remote.Add((FnosClient.Text(ep,"guid"),"Episode",FnosClient.Text(ep,"title"),Year(ep),"",imdb,(int)FnosClient.Number(ep,"season_number"),(int)FnosClient.Number(ep,"episode_number")));
                    }
                }
            }
            var user=users.GetUserById(Guid.Parse(Config.JellyfinUserId))??throw new InvalidOperationException("请先选择 Jellyfin 用户");
            var items=library.GetItemList(new InternalItemsQuery(user){Recursive=true});var candidates=new List<Mapping>();int unmatched=0;
            foreach(var item in items.Where(i=>i is Movie or Episode or Series))
            {
                var kind=item is Movie?"Movie":item is Episode?"Episode":"Series";
                item.ProviderIds.TryGetValue("Imdb",out var imdb);
                string? seriesImdb="";int season=0,episode=0;
                if(item is Episode ep){ep.Series?.ProviderIds.TryGetValue("Imdb",out seriesImdb);season=ep.ParentIndexNumber??-1;episode=ep.IndexNumber??-1;}
                var matches=remote.Where(r=>r.Kind==kind&&(kind=="Episode"?!string.IsNullOrEmpty(seriesImdb)&&seriesImdb==r.Series&&r.Season==season&&r.Episode==episode:!string.IsNullOrEmpty(imdb)&&imdb==r.Imdb)).ToList();
                // Name fallback requires a known matching year, and never overrides an explicit ID disagreement.
                if(matches.Count==0&&kind!="Episode")matches=remote.Where(r=>r.Kind==kind&&r.Year>0&&item.ProductionYear==r.Year&&Normalize(r.Name)==Normalize(item.Name)&&(string.IsNullOrEmpty(imdb)||string.IsNullOrEmpty(r.Imdb))).ToList();
                if(matches.Count==1)candidates.Add(new Mapping{JellyfinId=item.Id.ToString("N"),FnosId=matches[0].Id,Title=item.Name});else unmatched++;
            }
            var unique=candidates.Where(m=>candidates.Count(x=>x.FnosId==m.FnosId)==1).ToList();
            return new DiscoveryResult(unique,unmatched+candidates.Count-unique.Count,watch.Elapsed.TotalMilliseconds,"仅预览，未保存映射或写入媒体状态；无唯一外部 ID 的分集不自动匹配。");
        }finally{engine.Release();}
    }
    static int Year(JsonElement row){var date=FnosClient.Text(row,"release_date");if(date.Length<4)date=FnosClient.Text(row,"first_air_date");return date.Length>=4&&int.TryParse(date[..4],out var year)?year:0;}
    static string Normalize(string title)=>string.Concat(title.ToLowerInvariant().Where(char.IsLetterOrDigit));
}
