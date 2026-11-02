using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.FnosSync;

public sealed class FnosClient : IDisposable
{
    // NAS media traffic must not inherit Jellyfin's metadata/internet proxy.
    // .NET does not interpret every NO_PROXY format accepted by curl (CIDR).
    static HttpClient DirectClient()=>new(new HttpClientHandler{UseProxy=false}){Timeout=TimeSpan.FromSeconds(12)};
    HttpClient http = DirectClient();
    static readonly JsonSerializerOptions Compact=new(){Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
    string scope="", token="";
    public long Requests;
    readonly Dictionary<string,string> mediaIds=new();
    public static string Sign(string path,string body,string nonce,string timestamp)
    {
        const string salt="NDzZTVxnRKP8Z0jXg1VAMonaG8akvh";
        int[] keyBytes={152,159,234,234,236,235,154,237,132,232,235,157,155,132,153,158,158,237,132,154,159,232,152,132,239,154,156,156,154,155,157,236,157,155,154,158};
        var key=Encoding.UTF8.GetString(keyBytes.Select(x=>(byte)(x^169)).ToArray());
        static string Md5(string s)=>Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
        var digest=Md5(string.Join("_",salt,path,nonce,timestamp,Md5(body),key));
        return $"nonce={nonce}&timestamp={timestamp}&sign={digest}";
    }
    public static bool Flag(JsonElement row,string name)=>row.TryGetProperty(name,out var v)&&(v.ValueKind==JsonValueKind.True||(v.ValueKind==JsonValueKind.Number&&v.GetDouble()!=0));
    public static string Text(JsonElement row,string name)=>row.TryGetProperty(name,out var v)?(v.ValueKind==JsonValueKind.String?v.GetString()??"":v.ToString()):"";
    public static double Number(JsonElement row,string name)=>row.TryGetProperty(name,out var v)&&double.TryParse(v.ToString(),NumberStyles.Float,CultureInfo.InvariantCulture,out var n)?n:0;
    public async Task Login(CancellationToken ct)
    {
        var c=Plugin.Instance.Configuration;
        var password=Plugin.Instance.Password();
        var newScope=Plugin.AccountScope(c)+"|"+Plugin.Instance.Credentials.Revision;
        if(scope!=newScope){token="";mediaIds.Clear();scope=newScope;http.Dispose();http=DirectClient();}
        if(token.Length>0) return;
        var baseUrl=c.FnosUrl.TrimEnd('/'); if(baseUrl.EndsWith("/v"))baseUrl=baseUrl[..^2];
        if(!Uri.TryCreate(baseUrl+"/",UriKind.Absolute,out var uri)||uri.Scheme is not ("http" or "https"))throw new InvalidOperationException("请设置飞牛原生服务地址");
        if(http.BaseAddress==null)http.BaseAddress=uri;
        using var identityResponse=await http.GetAsync("System/Info/Public",ct); Requests++;
        identityResponse.EnsureSuccessStatusCode();
        var identity=JsonSerializer.Deserialize<JsonElement>(await identityResponse.Content.ReadAsStringAsync(ct));
        if(!Text(identity,"OperatingSystem").Equals("fnOS",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("目标不是飞牛影音原生服务");
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password))).ToLowerInvariant();
        var login=await Raw("POST","/v/api/v2/user/loginByPassword",new {username=c.FnosUsername,password=hash,remember=false},ct,false);
        token=Text(login,"token"); if(token.Length==0)throw new InvalidOperationException("飞牛未返回登录令牌");
        var server=await Raw("GET","/v/api/v1/server/info",null,ct,false);
        if(Text(server,"guid").Length==0||(!string.IsNullOrEmpty(Text(identity,"Id"))&&Text(identity,"Id")!=Text(server,"guid")))throw new InvalidOperationException("飞牛服务身份不一致");
    }
    async Task<JsonElement> Raw(string method,string path,object? data,CancellationToken ct,bool retry=true)
    {
        var body=data==null?"":JsonSerializer.Serialize(data,Compact);
        using var req=new HttpRequestMessage(new HttpMethod(method),path.TrimStart('/'));
        req.Headers.TryAddWithoutValidation("X-Trim-Client","web");req.Headers.TryAddWithoutValidation("X-Trim-Client-Version","631");
        req.Headers.TryAddWithoutValidation("authx",Sign(path,method=="GET"?"":body,RandomNumberGenerator.GetInt32(100000,1000000).ToString(),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()));
        if(token.Length>0)req.Headers.TryAddWithoutValidation("Authorization",token);
        if(data!=null)req.Content=new StringContent(body,Encoding.UTF8,"application/json");
        using var res=await http.SendAsync(req,ct); Requests++;
        var raw=await res.Content.ReadAsStringAsync(ct);
        if(retry&&res.StatusCode==HttpStatusCode.Unauthorized)
        {token="";await Login(ct);return await Raw(method,path,data,ct,false);}
        if(!res.IsSuccessStatusCode)throw new InvalidOperationException($"飞牛请求失败：{method} {path} HTTP {(int)res.StatusCode}，类型 {res.Content.Headers.ContentType?.MediaType??"未提供"}，正文 {raw.Length} 字符");
        JsonElement obj;try{obj=JsonSerializer.Deserialize<JsonElement>(raw);}catch{throw new InvalidOperationException($"飞牛返回无效 JSON：{method} {path} HTTP {(int)res.StatusCode}，类型 {res.Content.Headers.ContentType?.MediaType??"未提供"}，正文 {raw.Length} 字符");}
        if(retry&&(res.StatusCode==HttpStatusCode.Unauthorized||Number(obj,"code")==-2))
        {token="";await Login(ct);return await Raw(method,path,data,ct,false);}
        if(!res.IsSuccessStatusCode||!obj.TryGetProperty("code",out var code)||code.GetInt32()!=0||!obj.TryGetProperty("data",out var result))throw new InvalidOperationException($"飞牛请求失败 HTTP {(int)res.StatusCode}，业务码 {Text(obj,"code")}");
        return result.Clone();
    }
    public async Task<JsonElement> Api(string method,string path,object? data,CancellationToken ct)
    {await Login(ct);return await Raw(method,path,data,ct);}
    public async Task<MediaState> Read(string id,CancellationToken ct)
    {
        var row=await Api("GET","/v/api/v1/item/"+Uri.EscapeDataString(id),null,ct);
        if(!row.TryGetProperty("is_favorite",out _)||(!row.TryGetProperty("is_watched",out _)&&!row.TryGetProperty("watched",out _))||(!row.TryGetProperty("watched_ts",out _)&&!row.TryGetProperty("ts",out _)))throw new InvalidOperationException("飞牛单条状态字段不完整");
        return new(Flag(row,"is_favorite"),Flag(row,"is_watched")||Flag(row,"watched"),Math.Max(0,row.TryGetProperty("watched_ts",out _)?Number(row,"watched_ts"):Number(row,"ts")));
    }
    public async Task Favorite(string id,bool value,CancellationToken ct)=>await Api(value?"PUT":"DELETE","/v/api/v1/item/favorite",new{item_guid=id},ct);
    public async Task Playback(string id,MediaState state,CancellationToken ct)
    {
        if(!mediaIds.TryGetValue(id,out var media))
        {
            var streams=await Api("GET","/v/api/v1/stream/list/"+Uri.EscapeDataString(id),null,ct);
            var files=streams.GetProperty("files").EnumerateArray().Where(x=>Text(x,"guid").Length>0&&(!x.TryGetProperty("can_play",out _)||Flag(x,"can_play"))).ToList();
            if(files.Count==1)media=Text(files[0],"guid");
            else if(files.Count>1){var selected=await Api("POST","/v/api/v1/play/info",new{item_guid=id},ct);media=Text(selected,"media_guid");if(!files.Any(x=>Text(x,"guid")==media))throw new InvalidOperationException("飞牛多版本没有明确选中项");}
            else throw new InvalidOperationException("飞牛没有可播放文件");
            mediaIds[id]=media;
        }
        await Api("POST","/v/api/v1/play/record",new{media_guid=media,item_guid=id,ts=(int)Math.Max(0,state.Seconds)},ct);
        await Api(state.Played?"POST":"DELETE","/v/api/v1/item/watched",new{item_guid=id},ct);
        if(state.Seconds==0)await Api("DELETE","/v/api/v1/play/record",new{item_guid=id},ct);
    }
    public async Task<List<JsonElement>> Paged(string path,CancellationToken ct)
    {
        var rows=new List<JsonElement>();var ids=new HashSet<string>();int? expected=null;
        for(int page=1;page<=5000;page++)
        {
            var data=await Api("POST",path,new{tags=new{},sort_type="DESC",sort_column="create_time",page,page_size=200},ct);
            var total=data.GetProperty("total").GetInt32();expected??=total;
            if(expected!=total)throw new InvalidOperationException("飞牛分页期间目录变化");
            var batch=data.GetProperty("list").EnumerateArray().ToList();
            foreach(var row in batch){if(!ids.Add(Text(row,"guid"))||Text(row,"guid").Length==0)throw new InvalidOperationException("飞牛分页重复条目");rows.Add(row.Clone());}
            if(rows.Count==total)return rows;
            if(batch.Count==0||rows.Count>total)throw new InvalidOperationException("飞牛分页不完整");
        }
        throw new InvalidOperationException("飞牛分页超过上限");
    }
    public void Dispose()=>http.Dispose();
}
