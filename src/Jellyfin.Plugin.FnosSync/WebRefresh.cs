using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FnosSync;

// Adds a small same-origin helper to Jellyfin Web at response time. It never
// alters Jellyfin's web files on disk, so uninstall + restart removes the hook.
public sealed class WebRefreshStartup : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)=>app=>
    {
        app.Use(async (context,continuation)=>
        {
            var path=context.Request.Path.Value?.TrimEnd('/')??"";
            if(context.Request.Method!="GET"||path is not ("/web" or "/web/index.html"))
            {await continuation();return;}
            var original=context.Response.Body;
            var accept=context.Request.Headers["Accept-Encoding"];
            var conditional=context.Request.Headers["If-None-Match"];
            context.Request.Headers.Remove("Accept-Encoding");context.Request.Headers.Remove("If-None-Match");
            context.Response.OnStarting(()=>
            {
                context.Response.Headers.Remove("ETag");context.Response.ContentLength=null;
                context.Response.Headers.CacheControl="no-store";
                return Task.CompletedTask;
            });
            using var buffer=new MemoryStream();context.Response.Body=buffer;
            try
            {
                await continuation();
                var bytes=buffer.ToArray();
                if(context.Response.StatusCode==200&&(context.Response.ContentType??"").Contains("text/html"))
                {
                    var html=Encoding.UTF8.GetString(bytes);
                    var src=context.Request.PathBase+"/FnosSync/Client.js?v="+typeof(Plugin).Module.ModuleVersionId.ToString("N");
                    html=html.Replace("</head>","<script defer src=\""+src+"\"></script></head>",StringComparison.OrdinalIgnoreCase);
                    bytes=Encoding.UTF8.GetBytes(html);
                }
                context.Response.Body=original;await original.WriteAsync(bytes,context.RequestAborted);
            }
            finally
            {
                context.Response.Body=original;
                context.Request.Headers["Accept-Encoding"]=accept;
                context.Request.Headers["If-None-Match"]=conditional;
            }
        });
        next(app);
    };
}

[ApiController]
[Route("FnosSync")]
[Authorize]
public sealed class WebClientController : ControllerBase
{
    readonly SyncService service;
    public WebClientController(SyncService service)=>this.service=service;
    [HttpGet("Client.js")]
    [AllowAnonymous]
    public IActionResult Script()
    {
        Response.Headers.CacheControl="no-store";
        using var stream=typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.FnosSync.Client.js")!;
        using var reader=new StreamReader(stream);return Content(reader.ReadToEnd(),"application/javascript",Encoding.UTF8);
    }
    [HttpGet("ClientState")]
    public object State([FromQuery]bool wake=false,[FromQuery]string? ids=null)
    {
        var c=Plugin.Instance.Configuration;
        bool own=Guid.TryParse(c.JellyfinUserId,out var expected)&&User.Claims.Any(x=>Guid.TryParse(x.Value,out var value)&&value==expected);
        if(!own)return new{enabled=false,revision=0L};
        return service.ClientInfo(wake,(ids??"").Split(',',StringSplitOptions.RemoveEmptyEntries).Take(100));
    }
}
