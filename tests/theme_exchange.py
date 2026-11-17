"""Black-box theme API checks against a disposable local SQLite database."""
from pathlib import Path
import base64, json, os, secrets, sqlite3, subprocess, tempfile, time, urllib.request, urllib.error
root=Path(__file__).resolve().parents[1]
base="http://127.0.0.1:18559/messenger"
def request(path, method="GET", body=None, token=None, expected=200, chunked=False):
    headers={"Content-Type":"application/json"}
    if token: headers["Authorization"]="Bearer "+token
    data=json.dumps(body).encode() if body is not None else None
    if chunked: data=iter([data])
    try:
        with urllib.request.urlopen(urllib.request.Request(base+path,data=data,headers=headers,method=method),timeout=15) as response:
            status=response.status; payload=response.read()
    except urllib.error.HTTPError as e: status=e.code;payload=e.read()
    assert status==expected,(path,status,expected,payload[:300])
    return json.loads(payload) if payload else None
with tempfile.TemporaryDirectory() as tmp:
    env={**os.environ,"ASPNETCORE_URLS":"http://127.0.0.1:18559","Db":tmp+"/db.sqlite","MESSENGER_FILES":tmp+"/files","MESSENGER_DEV_SEED":"false","MESSENGER_KEY":base64.b64encode(secrets.token_bytes(32)).decode()}
    log=open(tmp+"/server.log","w")
    def start():
        process=subprocess.Popen(["dotnet",str(root/"backend/bin/Release/net8.0/Messenger.Api.dll")],env=env,cwd=tmp,stdout=log,stderr=log,creationflags=getattr(subprocess,"CREATE_NO_WINDOW",0))
        for _ in range(60):
            try:request("/health");return process
            except (OSError,AssertionError):time.sleep(.2)
        process.terminate();process.wait()
        raise RuntimeError("Backend failed to start")
    server=start()
    try:
        tokens=[]
        for user in ["themeowner","themeother"]:
            credentials={"login":user,"password":secrets.token_urlsafe(24)}
            request("/auth/register","POST",credentials,expected=201)
            tokens.append(request("/auth/login","POST",credentials)["user_session_token"])
        owner_before=request("/users/me",token=tokens[0])
        server.terminate();server.wait()
        # Recreate the pre-feature schema while preserving existing accounts/sessions.
        with sqlite3.connect(tmp+"/db.sqlite") as db:
            db.execute("DROP TABLE shared_themes")
            db.execute("DELETE FROM schema_migrations WHERE id='005_shared_themes.sql'")
        db.close()
        server=start()
        assert request("/users/me",token=tokens[0])==owner_before
        theme={"name":"My theme","dark":False,"background":"#EEEEEE","surface":"#FFFFFF","text":"#222222","accent":"#335577"}
        body={"schemaVersion":1,"theme":theme}
        request("/themes","POST",body,expected=401)
        created=request("/themes","POST",body,tokens[0],201);path="/themes/"+created["id"]
        assert request(path)["theme"]==theme
        request(path,"PATCH",{"version":1,"theme":{"accent":"#123456"}},tokens[1],403)
        request(path,"PATCH",{"version":1,"theme":{"accent":"red"}},tokens[0],400)
        request(path,"PATCH",{"version":1,"theme":{"accent":"#123456\n"}},tokens[0],400)
        updated=request(path,"PATCH",{"version":1,"theme":{"accent":"#123456"}},tokens[0])
        assert updated["version"]==2 and updated["theme"]["name"]==theme["name"]
        request(path,"PATCH",{"version":1,"theme":{"name":"Stale"}},tokens[0],409)
        request("/themes","POST",{"schemaVersion":2,"theme":theme},tokens[0],400)
        request("/themes","POST",{"schemaVersion":1,"theme":{**theme,"name":"x"*17000}},tokens[0],413)
        request("/themes","POST",{"schemaVersion":1,"theme":{**theme,"name":"x"*17000}},tokens[0],413,chunked=True)
        request(path,"DELETE",token=tokens[1],expected=403)
        server.terminate();server.wait();server=start()
        assert request(path)["version"]==2
        request(path,"DELETE",token=tokens[0],expected=204)
        request(path,expected=404)
        print("PASS themes: public read, authentication, ownership, partial PATCH, version conflict, schema/size validation, restart/migration persistence, deletion")
    finally:server.terminate();server.wait();log.close()
