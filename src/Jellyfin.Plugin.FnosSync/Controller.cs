using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.FnosSync;

public sealed class SaveRequest
{
    public Configuration Configuration {get;set;}=new();
    public string Password {get;set;}="";
}
public sealed class RefreshRequest {public string[] ItemIds {get;set;}=Array.Empty<string>();}
public sealed class LocalBackup
{
    public string ItemId{get;set;}="";
    public bool IsFavorite{get;set;}
    public bool Played{get;set;}
    public long PlaybackPositionTicks{get;set;}
    public int PlayCount{get;set;}
    public DateTime? LastPlayedDate{get;set;}
}

[ApiController]
[Route("FnosSync")]
[Authorize(Policy="RequiresElevation")]
public sealed class SyncController : ControllerBase
{
    readonly SyncService service;
    public SyncController(SyncService service)=>this.service=service;
    [HttpGet("Status")] public object Status()=>service.Status();
    [HttpGet("Configuration")]
    public object Configuration()
    {
        var config=JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(Plugin.Instance.Configuration))!;
        bool configured=Plugin.Instance.HasPassword();
        return new {configuration=config,passwordConfigured=configured,credentialError=Plugin.Instance.Credentials.StorageError};
    }
    [HttpPost("Configuration")]
    public async Task<IActionResult> Save(SaveRequest request,CancellationToken ct)
    {
        var c=request.Configuration;
        if(!Uri.TryCreate(c.FnosUrl.TrimEnd('/')+"/",UriKind.Absolute,out var url)||url.Scheme is not ("http" or "https")||url.UserInfo.Length>0)return BadRequest("飞牛地址须为不含账户信息的 HTTP/HTTPS 地址");
        if(string.IsNullOrWhiteSpace(c.FnosUsername)||!Guid.TryParse(c.JellyfinUserId,out var user))return BadRequest("请填写飞牛用户名并选择 Jellyfin 用户");
        if(c.DetailCacheSeconds<0||c.DetailCacheSeconds>3600||c.ProgressPushSeconds<1||c.ProgressPushSeconds>3600)return BadRequest("缓存 0–3600 秒，进度合并 1–3600 秒；定时触发请在 Jellyfin 计划任务中设置");
        if(c.InitialPolicy is not ("baseline" or "fnos" or "jellyfin")||c.ConflictPolicy is not ("preserve" or "most-progress"))return BadRequest("同步策略无效");
        try{TimeZoneInfo.FindSystemTimeZoneById(c.TimeZone);}catch{return BadRequest("时区无效");}
        c.JellyfinUserId=user.ToString("N");
        c.Mappings??=new();
        if(c.Enabled&&c.Mappings.Count==0)return BadRequest("尚未保存任何媒体映射。请先发现映射并保存，再启用同步。");
        foreach(var m in c.Mappings)
        {
            if(!Guid.TryParse(m.JellyfinId,out var id)||!Guid.TryParse(m.FnosId,out var remote))return BadRequest("映射 ID 无效");
            m.JellyfinId=id.ToString("N");m.FnosId=remote.ToString("N");
        }
        if(c.Mappings.GroupBy(m=>m.JellyfinId).Any(g=>g.Count()>1)||c.Mappings.GroupBy(m=>m.FnosId).Any(g=>g.Count()>1))return BadRequest("映射必须一对一，不能重复");
        if(c.Enabled&&request.Password.Length==0&&!Plugin.Instance.HasPassword(c))return BadRequest("启用前请填写飞牛密码并保存；保存后会加密持久化，重启自动恢复。");
        await service.Configure(c,ct,request.Password);return NoContent();
    }
    [HttpPost("Test")]public async Task<object> Test(CancellationToken ct)=>await service.Test(ct);
    [HttpPost("Discover")]public async Task<object> Discover(CancellationToken ct)=>await service.Discover(ct);
    [HttpPost("Poll")]public async Task<object> Poll(CancellationToken ct)=>await service.Poll(ct);
    [HttpPost("Full")]public async Task<object> Full(CancellationToken ct)=>await service.Full(ct);
    [HttpPost("Restore")]
    public async Task<IActionResult> Restore(LocalBackup backup,CancellationToken ct)
    {
        if(!Guid.TryParse(backup.ItemId,out _)||backup.PlayCount<0||backup.PlaybackPositionTicks<0)return BadRequest("备份状态无效");
        if(Plugin.Instance.Configuration.Enabled)return BadRequest("恢复备份前请先停用插件同步");
        return Ok(await service.RestoreLocal(backup,ct));
    }
    [HttpPost("Refresh")]
    public async Task<object> Refresh(RefreshRequest request,CancellationToken ct)
    {
        if(request.ItemIds.Length>100)return BadRequest("单次最多刷新 100 个条目");
        var results=new List<SyncResult>();
        foreach(var id in request.ItemIds.Distinct())results.Add(await service.SyncOne(id,"explicit-refresh",ct));
        return new{results};
    }
}

// Runs on API result objects, not media file I/O. No external HTTP request blocks rendering.
public sealed class ReadRefreshFilter : IAsyncResultFilter, IAsyncActionFilter
{
    readonly SyncService service;
    public ReadRefreshFilter(SyncService service)=>this.service=service;
    public async Task OnActionExecutionAsync(ActionExecutingContext context,ActionExecutionDelegate next)
    {
        // Preserve the explicitly supplied fields. A first user action must
        // not be mistaken for an old difference that should only be baselined.
        var patch=context.ActionArguments.Values.OfType<MediaBrowser.Model.Dto.UpdateUserItemDataDto>().FirstOrDefault();
        if(patch!=null)
        {
            context.HttpContext.Items["FnosSync.FavoriteIntent"]=patch.IsFavorite.HasValue;
            context.HttpContext.Items["FnosSync.PlaybackIntent"]=patch.Played.HasValue||patch.PlaybackPositionTicks.HasValue;
        }
        await next();
    }
    public async Task OnResultExecutionAsync(ResultExecutingContext context,ResultExecutionDelegate next)
    {
        var config=Plugin.Instance.Configuration;
        bool ownUser=Guid.TryParse(config.JellyfinUserId,out var expected)&&context.HttpContext.User.Claims.Any(c=>Guid.TryParse(c.Value,out var claim)&&claim==expected);
        if(config.Enabled&&ownUser&&context.HttpContext.Request.Method=="GET"&&context.Result is ObjectResult result)
        {
            IEnumerable<BaseItemDto>? items=result.Value switch {
                BaseItemDto item=>new[]{item},
                QueryResult<BaseItemDto> list=>list.Items,
                IEnumerable<BaseItemDto> list=>list,
                _=>null
            };
            if(items!=null){service.Touch(items.Select(i=>i.Id.ToString("N")));service.WakeLightPoll();}
        }
        await next();
    }
}
