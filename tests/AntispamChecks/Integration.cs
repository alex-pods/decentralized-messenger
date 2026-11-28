using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MessengerDesktop;
using MessengerDesktop.Antispam;

static class Integration
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static async Task Run(string server)
    {
        var suffix=Guid.NewGuid().ToString("N")[..7];
        using var alice=new MessengerClient(server);using var bob=new MessengerClient(server);
        await alice.Register("aim"+suffix,"testPass!42");await alice.Login("aim"+suffix,"testPass!42");
        await bob.Register("bob"+suffix,"testPass!42");await bob.Login("bob"+suffix,"testPass!42");
        await alice.DiscoverAntispam();
        Check(alice.AntispamSupported&&!alice.AntispamBlocked,"Capability negotiation failed");
        var chat=await alice.CreateDirect((await bob.GetMe()).Id);
        var first="";
        string MessageId() => chat + "." + Guid.NewGuid().ToString("N");
        var relayId = MessageId();
        for(var i=0;i<5;i++)first=await alice.SendMessage(chat,"a",clientRef:"integration"+i);
        try{await alice.SendMessage(chat,"a");throw new Exception("Burst was accepted");}catch(ApiException ex)when(ex.Code=="antispam_required"){}
        Check((await alice.GetMessages(chat)).Count==5,"Blocked message was stored");
        using var raw=new HttpClient{BaseAddress=new Uri(server.TrimEnd('/')+"/")};
        raw.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",alice.Token);
        var media=await alice.GetConcentrationClips(CancellationToken.None);
        using(var anonymous=new HttpClient())
        {
            using var denied=await anonymous.GetAsync(server.TrimEnd('/')+"/antispam/media");
            Check(denied.StatusCode==HttpStatusCode.Unauthorized,"Unauthenticated media catalog exposed");
            if(media.Length>0)
            {
                using var deniedClip=await anonymous.GetAsync(server.TrimEnd('/')+"/antispam/media/"+media[0].Id);
                Check(deniedClip.StatusCode==HttpStatusCode.Unauthorized,"Unauthenticated media file exposed");
            }
        }
        using(var missing=await raw.GetAsync("antispam/media/not-allowlisted"))Check(missing.StatusCode==HttpStatusCode.NotFound,"Unknown media not rejected");
        if(media.Length>0)
        {
            var file=await alice.DownloadConcentrationClip(media[0],null,CancellationToken.None);
            Check(new FileInfo(file).Length==media[0].Bytes,"Real media download failed");
            using var range=new HttpRequestMessage(HttpMethod.Get,"antispam/media/"+media[0].Id);
            range.Headers.Range=new RangeHeaderValue(0,99);
            using var response=await raw.SendAsync(range);
            Check(response.StatusCode==HttpStatusCode.PartialContent&&(await response.Content.ReadAsByteArrayAsync()).Length==100,"Media range request failed");
        }
        Console.WriteLine("PASS authenticated media catalog/files, fixed allowlist, download checksum and byte ranges");
        foreach(var (method,path,body) in new[]{(HttpMethod.Post,$"chats/{chat}/messages","{\"text\":\"blocked\",\"message_id\":\"" + MessageId() + "\"}"),(HttpMethod.Post,$"chats/{chat}/messages/relay",$"{{\"text\":\"blocked\",\"message_id\":\"{relayId}\"}}"),(HttpMethod.Post,$"messages/{first}/forward",$"{{\"chat_id\":\"{chat}\",\"message_id\":\"{MessageId()}\"}}"),(HttpMethod.Patch,$"messages/{first}","{\"text\":\"blocked edit\"}")})
        {
            using var request=new HttpRequestMessage(method,path){Content=new StringContent(body,Encoding.UTF8,"application/json")};
            using var response=await raw.SendAsync(request);Check(response.StatusCode==HttpStatusCode.TooManyRequests,"Raw request bypassed server gate");
        }
        var challenge=await alice.CreateAntispamChallenge();
        using(var foreign=new ClientWebSocket())
        {
            foreign.Options.SetRequestHeader("Authorization","Bearer "+bob.Token);
            try{await foreign.ConnectAsync(alice.AntispamWebSocketUri(challenge.Id),CancellationToken.None);throw new Exception("Foreign challenge accepted");}catch(WebSocketException){}
        }
        using(var early=await Connect(alice,challenge.Id))
        {
            await Read(early);
            var state=await Exchange(early,new("finish",1));
            Check(state.Phase=="failed","Fake completion was accepted");
        }
        challenge=await alice.CreateAntispamChallenge();
        using(var interrupted=await Connect(alice,challenge.Id)) {await Read(interrupted);interrupted.Abort();}
        await Task.Delay(150);
        challenge=await alice.CreateAntispamChallenge();
        using(var socket=await Connect(alice,challenge.Id))
        {
            var state=await Read(socket);var sequence=0;
            Check(state.Phase=="countdown"&&state.Targets.GetArrayLength()==0,"Countdown leaked targets");
            while(state.Phase=="countdown") {await Task.Delay(100);state=await Exchange(socket,new("heartbeat",++sequence));}
            for(var i=0;i<60;i++)
            {
                var target=state.Targets[0];await Task.Delay(80);
                state=await Exchange(socket,new("shot",++sequence,target.GetProperty("x").GetDouble(),target.GetProperty("y").GetDouble()));
                Check(state.Phase is "running" or "qualified","Shot verification failed");
            }
            Check(state.Score==6000&&state.Phase=="running","6000 ended the round early");
            while(state.Phase=="running") {await Task.Delay(200);state=await Exchange(socket,new("heartbeat",++sequence));}
            Check(state.Phase=="qualified"&&state.Hits==60&&state.Misses==0&&state.Accuracy==100,"Incorrect final result");
            state=await Exchange(socket,new("finish",++sequence));Check(state.Phase=="passed","Server did not pass valid run");
        }
        await alice.RefreshAntispam();Check(!alice.AntispamBlocked,"Client remained blocked after pass");
        try { await alice.RelayMessage(chat, relayId, "retry after verification"); throw new Exception("Offline relay was accepted"); }
        catch (ApiException ex) when (ex.Code == "relay_no_recipient") { }
        await alice.SendMessage(chat,"after verification");
        Check((await alice.GetMessages(chat)).Count==6,"Sending did not resume");
        Console.WriteLine("PASS HTTP/WS anti-spam: advertised flag, burst, blocked storage/edit/forward, user binding, fake finish, disconnect/retry, real 6000 run and resumed sending");
    }
    static async Task<ClientWebSocket> Connect(MessengerClient api,string id)
    {
        var ws = new ClientWebSocket();
        try
        {
            await api.ConnectAuthenticatedSocket(ws, api.AntispamWebSocketUri(id), CancellationToken.None);
            return ws;
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }
    static async Task<AimState> Exchange(ClientWebSocket ws,AimInput input)
    {
        await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(input,Json),WebSocketMessageType.Text,true,CancellationToken.None);return await Read(ws);
    }
    static async Task<AimState> Read(ClientWebSocket ws)
    {
        var buffer=new byte[8192];using var timeout=new CancellationTokenSource(3000);
        var result=await ws.ReceiveAsync(buffer.AsMemory(),timeout.Token);
        Check(result.MessageType==WebSocketMessageType.Text&&result.EndOfMessage,"Challenge connection closed unexpectedly");
        return JsonSerializer.Deserialize<AimState>(buffer.AsSpan(0,result.Count),Json)!;
    }
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
}
