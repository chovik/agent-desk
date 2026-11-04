using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace AgentDesk.Portal;

// Only the bound guest process can use this bridge; it has no browser/public HTTP endpoint.
// Personal credentials stay here, never in the guest config/stdin/stdout or task snapshots.
public sealed class ModelBridge(PortalOptions options,CodexConnections.CredentialLease account,Action<string,string> output) : IDisposable
{
    private readonly HttpClient _http=new(new HttpClientHandler{AllowAutoRedirect=false,UseProxy=true,Proxy=new WebProxy(options.CodexProxy)}){Timeout=Timeout.InfiniteTimeSpan};
    private int _calls;
    public void Dispose()=>_http.Dispose();
    public static JsonObject Validate(string body,string path,string model)
    {
        if(path is not ("/v1/responses" or "/v1/responses/compact")||body.Length>2*1024*1024)throw new IOException("Model route/body rejected.");
        var json=JsonNode.Parse(body,documentOptions:new(){MaxDepth=64}) as JsonObject??throw new IOException("Invalid model request.");
        if(json["model"]?.GetValue<string>()!=model)throw new IOException("Model is not approved for this run.");
        foreach(var key in new[]{"previous_response_id","conversation","prompt","container","mcp_servers","prompt_cache_options"})
            if(json[key] is not null)throw new IOException("Provider-side resources are not available.");
        static bool FileReference(JsonNode? n)=>n switch{JsonObject o=>o["type"]?.ToString()=="item_reference"||o.Any(v=>v.Key=="file_id"||FileReference(v.Value)),JsonArray a=>a.Any(FileReference),_=>false};
        if(FileReference(json))throw new IOException("Provider file references are not available.");
        if(json["tools"] is not null)
        {
            if(json["tools"] is not JsonArray tools)throw new IOException("Invalid tools.");
            foreach(var tool in tools)if(tool?["type"]?.ToString() is not ("function" or "custom"))throw new IOException("Provider hosted tools are not allowed.");
        }
        json.Remove("background");json["store"]=false;
        if(path=="/v1/responses")json["stream"]=true;
        return json;
    }
    public async Task Forward(JsonElement frame,Func<object,Task> send,CancellationToken ct)
    {
        var id=frame.GetProperty("id").GetString();if(!Guid.TryParse(id,out _))throw new IOException("Invalid bridge request ID.");
        bool header=false;
        try
        {
            if(Interlocked.Increment(ref _calls)>8)throw new IOException("Model request limit reached.");
            var body=Convert.FromBase64String(frame.GetProperty("body").GetString()!);if(body.Length>2*1024*1024)throw new IOException("Model request too large.");
            var path=frame.GetProperty("path").GetString()!;var json=Validate(Encoding.UTF8.GetString(body),path,options.CodexModel);
            using var request=new HttpRequestMessage(HttpMethod.Post,"https://chatgpt.com/backend-api/codex/"+path[4..]);
            request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",account.AccessToken);request.Headers.Add("ChatGPT-Account-Id",account.AccountId);
            request.Headers.TryAddWithoutValidation("User-Agent","agent_desk_private_lab/0.2.0");
            request.Content=new StringContent(json.ToJsonString(),Encoding.UTF8,"application/json");
            using var response=await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
            if(!response.IsSuccessStatusCode)
            {
                output("system",$"Model provider rejected the request (HTTP {(int)response.StatusCode}). No other account or API-key fallback was used.");
                throw new IOException("Provider rejected request.");
            }
            await send(new{type="model-header",id,status=200,contentType=path.EndsWith("compact")?"application/json":"text/event-stream"});header=true;
            using var stream=await response.Content.ReadAsStreamAsync(ct);var buffer=new byte[16384];int read,total=0;
            while((read=await stream.ReadAsync(buffer,ct))>0){total+=read;if(total>8*1024*1024)throw new IOException("Model response limit reached.");await send(new{type="model-chunk",id,data=Convert.ToBase64String(buffer,0,read)});}
        }
        catch(Exception)when(!ct.IsCancellationRequested)
        {
            output("system","The bounded model bridge could not complete a request. Reconnect if authorization expired; no success was inferred.");
            if(!header){await send(new{type="model-header",id,status=502,contentType="application/json"});await send(new{type="model-chunk",id,data=Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"Portal model request failed or reached its limit.\"}}"))});}
        }
        finally{if(!ct.IsCancellationRequested)await send(new{type="model-end",id});}
    }
}
