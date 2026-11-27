using Dapper;
using Messenger.Api.Antispam;
using Messenger.Api.Common.Errors;
using Messenger.Api.Database;
using Microsoft.Extensions.Configuration;
using MessengerDesktop;
using System.Net;
using System.Text;

if (args.Length == 2 && args[0] == "--server") { await Integration.Run(args[1]); return; }
if (args.Length == 2 && args[0] == "--media") { await ConcentrationChecks.CheckSoundtracks(args[1]); return; }
var directory = Path.Combine(Path.GetTempPath(), "messenger-antispam-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var db = new DbConnectionFactory(Path.Combine(directory, "test.db"));
    MigrationRunner.Run(db); MigrationRunner.Run(db);
    using (var connection = db.Open())
        connection.Execute("INSERT INTO users(id,tag,name,password_hash) VALUES(1,'alice','Alice','test'),(2,'bobby','Bob','test')");
    var repository = new AntispamRepository(db);
    var clock = new FakeClock();
    var options = new AntispamOptions(true);
    var service = new AntispamService(options, repository, clock);
    Expect(!AntispamOptions.Read(new ConfigurationBuilder().Build()).Enabled, "Default must be off");
    var off = new AntispamService(new(false), repository, clock);
    for (var i = 0; i < 100; i++) off.CheckSend(1, 1, null);
    Expect(!off.Required(1), "Disabled antispam blocked sending");
    for (var i = 0; i < 5; i++) service.CheckSend(1, i + 1, "ref" + i);
    for (var i = 0; i < 10; i++) service.CheckSend(1, 1, "ref0");
    Blocked(() => service.CheckSend(1, 6, "sixth"));
    Expect(service.Required(1) && !service.Required(2), "Account isolation or cross-chat burst failed");
    clock.Advance(60000);
    var restarted = new AntispamService(options, new AntispamRepository(db), clock);
    Blocked(() => restarted.CheckSend(1, 1, null));
    Expect(!off.Required(1), "Off switch ignored persisted block");
    off.CheckSend(1, 1, null);
    Console.WriteLine("PASS rate gate: disabled default, sixth send, idempotent retry, cross-chat/account isolation and durable block");

    var id = service.Create(1);
    Expect(service.Start(1,id).Targets.Count == 0, "Targets leaked during countdown");
    Expect(service.Input(1,id,new("shot",1,480,270)).Phase == "failed", "Early shot accepted");
    Expect(service.Required(1), "Failed round removed block");
    id = service.Create(1); service.Start(1,id); clock.Advance(4000);
    var state = service.Input(1,id,new("heartbeat",1));
    Expect(state.Phase == "running" && state.Targets.Count == 4, "Countdown did not finish");
    Expect(state.Targets.Distinct().Count() == 4, "Overlapping targets");
    var seq = 1;
    for (var i = 0; i < 60; i++)
    {
        var target = state.Targets[0]; clock.Advance(100);
        state = service.Input(1,id,new("shot",++seq,target.X,target.Y));
    }
    Expect(state.Score == 6000 && state.Phase == "running" && service.Required(1), "6000 ended round before 30 seconds");
    clock.Advance(24000);
    Expect(service.Input(1,id,new("heartbeat",++seq)).Phase=="qualified", "Full 30-second round did not qualify");
    service.Abort(1,id);
    Expect(service.Input(1,id,new("finish",++seq)).Phase == "failed" && service.Required(1), "Focus loss before finish was bypassed");
    id = service.Create(1); service.Start(1,id); clock.Advance(4000);
    state = service.Input(1,id,new("heartbeat",1)); seq=1;
    for(var i=0;i<60;i++) {clock.Advance(100);var target=state.Targets[0];state=service.Input(1,id,new("shot",++seq,target.X,target.Y));}
    clock.Advance(23999);
    Expect(service.Input(1,id,new("heartbeat",++seq)).Phase=="running" && service.Required(1), "Early release at 29.999 seconds");
    clock.Advance(1);
    state=service.Input(1,id,new("heartbeat",++seq));
    Expect(state.Phase=="qualified"&&state.Hits==60&&state.Misses==0&&state.Accuracy==100,"Full result incorrect");
    Expect(service.Input(1,id,new("finish",++seq)).Phase=="passed" && !service.Required(1), "Verified round did not unblock");
    for(var i=0;i<5;i++) service.CheckSend(1,1,null);
    Blocked(()=>service.CheckSend(1,1,null));
    service.Abort(1,id);
    Expect(service.Required(1), "Old attempt cleared a later block");
    id=service.Create(1);service.Start(1,id);clock.Advance(34000);
    Expect(service.Input(1,id,new("heartbeat",1)).Phase=="failed", "30 second limit ignored");
    id=service.Create(1);service.Start(1,id);clock.Advance(4000);
    Expect(service.Input(1,id,new("finish",1)).Phase=="failed", "Arbitrary finish accepted");
    id=service.Create(1);service.Start(1,id);
    Expect(service.Input(1,id,new("heartbeat",9)).Phase=="failed", "Out-of-order event accepted");
    var score = new PrecisionRound(); var target0 = score.Targets[0];
    score.Click(target0.X,target0.Y);score.Click(100000,100000);
    Expect(score.Score==50, "Misses do not reduce accuracy score");
    id=service.Create(1);service.Start(1,id);clock.Advance(4000);seq=1;
    state=service.Input(1,id,new("heartbeat",seq));
    for(var i=0;i<60;i++){clock.Advance(100);var target=state.Targets[0];state=service.Input(1,id,new("shot",++seq,target.X,target.Y));}
    clock.Advance(100);state=service.Input(1,id,new("shot",++seq,100000,100000));
    Expect(state.Score<6000 && state.Phase=="running","Miss after reaching 6000 did not change final eligibility");
    clock.Advance(23900);state=service.Input(1,id,new("heartbeat",++seq));
    Expect(state.Phase=="failed"&&state.Hits==60&&state.Misses==1&&service.Required(1),"Earlier peak score incorrectly passed final result");
    Console.WriteLine("PASS challenge: authoritative geometry/score, 4s countdown, 30s limit, 6000 threshold, interruption, replay, retry and retrigger");

    var concurrent = new AntispamService(options,repository,clock);
    var allowed=0;var blocked=0;
    Parallel.For(0,20,i=> {try{concurrent.CheckSend(2,i+1,null);Interlocked.Increment(ref allowed);}catch(AppException ex) when(ex.Code=="antispam_required"){Interlocked.Increment(ref blocked);}});
    Expect(allowed==5&&blocked==15,"Concurrent requests bypassed burst threshold");
    Console.WriteLine("PASS concurrent message reservations");

    var handler = new StubHandler();
    using var client = new MessengerClient("http://legacy.test/messenger",handler:handler);
    await client.DiscoverAntispam();
    Expect(!client.AntispamSupported&&!client.AntispamBlocked,"Missing capability changed legacy behavior");
    await client.SendMessage(1,"hello");
    handler.Block=true;
    try {await client.SendMessage(1,"blocked");}catch(ApiException){}
    var requests=handler.Count;
    for(var i=0;i<10;i++)try{await client.SendMessage(1,"no request");}catch(ApiException){}
    Expect(client.AntispamBlocked&&handler.Count==requests,"Client kept sending after antispam");
    handler.Block=false;
    await client.RefreshAntispam();
    await client.SendMessage(1,"resumed");
    Expect(!client.AntispamBlocked,"Verified server status did not unblock client");
    Console.WriteLine("PASS client compatibility and zero message requests while blocked");
    var camera=new MessengerDesktop.Antispam.AimCamera();
    Expect(MessengerDesktop.Antispam.AimCamera.TargetDistance==PrecisionRound.TargetDistance,"Client/server target distance mismatch");
    Expect(camera.AimPoint()==(480d,270d),"Camera reference origin");
    camera.Move(100,-50);
    Expect(Math.Abs(camera.Yaw+100*MessengerDesktop.Antispam.AimCamera.Gain)<1e-12,"Raw sensitivity calibration");
    foreach(var (ms,text) in new[]{(4000d,""),(3001d,""),(3000d,"3"),(2000d,"2"),(1000d,"1"),(0d,"")})
        Expect(MessengerDesktop.Antispam.AimCamera.Countdown(ms)==text,"Blank second / 3-2-1 countdown mismatch");
    Console.WriteLine("PASS native camera and blank first second / 3-2-1 countdown");
    await ConcentrationChecks.Run(directory);
}
finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory,true); }

static void Expect(bool value,string message){if(!value)throw new Exception(message);}
static void Blocked(Action action){try{action();throw new Exception("Expected block");}catch(AppException ex)when(ex.Code=="antispam_required"){} }
sealed class FakeClock : TimeProvider
{
    long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp()=>_ticks;
    public override DateTimeOffset GetUtcNow()=>new DateTimeOffset(2026,11,25,0,0,0,TimeSpan.Zero).AddTicks(_ticks);
    public void Advance(int ms)=>_ticks+=TimeSpan.FromMilliseconds(ms).Ticks;
}
sealed class StubHandler : HttpMessageHandler
{
    public int Count;public bool Block;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
    {
        Count++;
        var path=request.RequestUri!.AbsolutePath;
        var code=path.EndsWith("capabilities")?HttpStatusCode.NotFound:Block&&path.EndsWith("messages")?HttpStatusCode.TooManyRequests:HttpStatusCode.OK;
        var json=code==HttpStatusCode.TooManyRequests?"{\"code\":\"antispam_required\",\"error\":\"Check required\"}":path.EndsWith("status")?"{\"enabled\":true,\"required\":false}":"{\"message_id\":1}";
        return Task.FromResult(new HttpResponseMessage(code){Content=new StringContent(json,Encoding.UTF8,"application/json")});
    }
}
