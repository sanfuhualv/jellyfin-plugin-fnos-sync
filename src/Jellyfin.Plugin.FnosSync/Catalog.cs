using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FnosSync;

// The catalog contains only distributable plugin code, never configuration or
// credentials. Register its URL once in Jellyfin's plugin repository settings.
[ApiController]
[Route("FnosSync")]
[AllowAnonymous]
public sealed class CatalogController : ControllerBase
{
    static readonly Lazy<byte[]> archive=new(()=>
    {
        using var buffer=new MemoryStream();
        using(var zip=new ZipArchive(buffer,ZipArchiveMode.Create,true))
        {
            var entry=zip.CreateEntry("Jellyfin.Plugin.FnosSync.dll",CompressionLevel.Optimal);
            entry.LastWriteTime=new DateTimeOffset(2000,1,1,0,0,0,TimeSpan.Zero);
            using(var target=entry.Open())
            using(var source=System.IO.File.OpenRead(typeof(Plugin).Assembly.Location))source.CopyTo(target);
            foreach(var name in new[]{"LICENSE","THIRD_PARTY_NOTICES.md","README.md","icon.png"})
            {
                var document=zip.CreateEntry(name,CompressionLevel.Optimal);
                document.LastWriteTime=new DateTimeOffset(2000,1,1,0,0,0,TimeSpan.Zero);
                using var content=document.Open();
                using var resource=typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.FnosSync."+name)
                    ??throw new InvalidOperationException("Missing release document: "+name);
                resource.CopyTo(content);
            }
        }
        return buffer.ToArray();
    });
    [HttpGet("package.zip")]
    public IActionResult Download()=>File(archive.Value,"application/zip","FnosSync-"+Plugin.ReleaseVersion+".zip");
    [HttpGet("icon.png")]
    public IActionResult Icon()
    {
        var stream=typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.FnosSync.icon.png")
            ??throw new InvalidOperationException("Missing plugin icon");
        return File(stream,"image/png");
    }
    [HttpGet("manifest.json")]
    public IActionResult Manifest()
    {
        var root=Request.Scheme+"://"+Request.Host+Request.PathBase+"/FnosSync";
        var version=new{version=Plugin.ReleaseVersion+".0",targetAbi="12.4.0.0",sourceUrl=root+"/package.zip",checksum=Convert.ToHexString(MD5.HashData(archive.Value)).ToLowerInvariant(),timestamp="2026-11-01T00:00:00Z"};
        var package=new{guid=Plugin.Instance.Id,name=Plugin.Instance.Name,description=Plugin.Instance.Description,overview="飞牛影音与 Jellyfin 双向状态同步",owner="Media Library Sync",category="General",imageUrl=root+"/icon.png",versions=new[]{version}};
        return Content(JsonSerializer.Serialize(new[]{package}),"application/json");
    }
}
