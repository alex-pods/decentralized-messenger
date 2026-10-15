"""
Чёрный ящик v4: контракт MVP (без save_history, Bearer-only REST, идемпотентная отправка,
legacy not_stored). Пишется по заданию рефакторинга и swagger.json, код сервера — чёрный ящик.
"""
import json, socket, base64, os, sqlite3, struct, sys, threading, time, urllib.request, urllib.error, urllib.parse, uuid

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://176.53.160.4/messenger"
WSHOST = sys.argv[2] if len(sys.argv) > 2 else "176.53.160.4"
WSPORT = int(sys.argv[3]) if len(sys.argv) > 3 else 80
DB_FILE = sys.argv[4] if len(sys.argv) > 4 else None  # для legacy-проверки (локально)
SUF = "v4" + str(int(time.time()))[-7:]

PASSED, FAILED = [], []
def check(name, cond, ctx=""):
    (PASSED if cond else FAILED).append((name, str(ctx)[:300]))
    print(("PASS  " if cond else "FAIL  ") + name + ("" if cond else f"   [{str(ctx)[:200]}]"))

def api(method, path, body=None, token=None, raw_body=None, ctype="application/json", auth="bearer"):
    req = urllib.request.Request(BASE + path, method=method)
    req.add_header("Content-Type", ctype)
    if token and auth == "bearer": req.add_header("Authorization", "Bearer " + token)
    if token and auth == "xheader": req.add_header("X-Session-Token", token)
    if token and auth == "query": pass
    data = raw_body if raw_body is not None else (json.dumps(body).encode() if body is not None else None)
    try:
        with urllib.request.urlopen(req, data, timeout=20) as r:
            raw = r.read()
            try: parsed = json.loads(raw) if raw else None
            except Exception: parsed = raw[:80]
            return r.status, parsed, r.headers
    except urllib.error.HTTPError as e:
        raw = e.read()
        try: parsed = json.loads(raw) if raw else None
        except Exception: parsed = raw[:200]
        return e.code, parsed, e.headers

def is_err(b):
    return isinstance(b, dict) and isinstance(b.get("code"), str) and isinstance(b.get("error"), str) \
        and isinstance(b.get("trace_id"), str) and "details" in b

def multipart(files, fields=None):
    b = "----b" + uuid.uuid4().hex
    out = b""
    for k, v in (fields or {}).items():
        out += (f"--{b}\r\nContent-Disposition: form-data; name=\"{k}\"\r\n\r\n{v}\r\n").encode()
    for field, fname, data, ctype in files:
        out += (f"--{b}\r\nContent-Disposition: form-data; name=\"{field}\"; filename=\"{fname}\"\r\n"
                f"Content-Type: {ctype}\r\n\r\n").encode() + data + b"\r\n"
    out += f"--{b}--\r\n".encode()
    return out, f"multipart/form-data; boundary={b}"

SOCKBUF = {}
def ws_connect(path):
    s = socket.create_connection((WSHOST, WSPORT), timeout=10)
    key = base64.b64encode(os.urandom(16)).decode()
    s.sendall((f"GET {path} HTTP/1.1\r\nHost: {WSHOST}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
               f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n").encode())
    resp = b""
    while b"\r\n\r\n" not in resp:
        chunk = s.recv(4096)
        if not chunk: raise ConnectionError("closed during handshake")
        resp += chunk
    head, _, rest = resp.partition(b"\r\n\r\n")
    code = int(head.split(b" ")[1])
    if code != 101: s.close(); return code, None
    SOCKBUF[s] = rest
    return 101, s
def ws_send(s, obj):
    data = json.dumps(obj).encode(); mask = os.urandom(4)
    h = bytearray([0x81]); n = len(data)
    if n < 126: h.append(0x80 | n)
    elif n < 65536: h.append(0x80 | 126); h += struct.pack(">H", n)
    else: h.append(0x80 | 127); h += struct.pack(">Q", n)
    h += mask
    s.sendall(bytes(h) + bytes(b ^ mask[i % 4] for i, b in enumerate(data)))
def _read(s, n):
    out = b""
    b = SOCKBUF.pop(s, b"")
    if b: out += b[:n]; n -= len(out); SOCKBUF[s] = b[len(out):] if len(b) > len(out) else b""
    while n > 0:
        c = s.recv(n)
        if not c: raise ConnectionError("closed")
        out += c; n -= len(c)
    return out
def ws_recv(s, timeout=4):
    s.settimeout(timeout)
    while True:
        b1, b2 = _read(s, 2); op, ln = b1 & 0x0F, b2 & 0x7F
        if ln == 126: ln = struct.unpack(">H", _read(s, 2))[0]
        elif ln == 127: ln = struct.unpack(">Q", _read(s, 8))[0]
        p = _read(s, ln)
        if op == 0x8:
            cc = struct.unpack(">H", p[:2])[0] if len(p) >= 2 else None
            raise ConnectionError(f"closed_by_server:{cc}")
        if op in (0x9, 0xA): continue
        return json.loads(p.decode())
def ws_nothing(s, timeout=1.5):
    try: ws_recv(s, timeout); return False
    except ConnectionError: raise
    except Exception: return True

def is2xx(st): return 200 <= st < 300
def is4xx(st): return 400 <= st < 500

print(f"===== BLACK-BOX v4 vs {BASE} (суффикс {SUF}) =====\n")

# ---------- спека ----------
spec = json.load(urllib.request.urlopen(BASE + "/swagger/v1/swagger.json", timeout=15))
def opspec(p, m): return spec["paths"][p][m]
check("spec: /users/me/settings УДАЛЁН из контракта", "/users/me/settings" not in spec["paths"], "")
check("spec: client_ref описан в SendMessageIn",
      "client_ref" in spec["components"]["schemas"]["SendMessageIn"]["properties"], "")
sec = json.dumps(spec["paths"]["/users/me"]["get"].get("security", []))
check("spec: защищённые ручки — только SessionBearer", "SessionBearer" in sec and "SessionHeader" not in sec, sec)
check("spec: securitySchemes без SessionHeader",
      "SessionHeader" not in spec.get("components", {}).get("securitySchemes", {}), "")
e401 = spec["paths"]["/chats"]["get"]["responses"].get("401", {})
e_props = set(e401.get("content", {}).get("application/json", {}).get("schema", {}).get("properties", {}).keys())
check("spec: ошибки схемой {code,error,details,trace_id}", {"code", "error", "trace_id"} <= e_props, e_props)
oids = [op.get("operationId") for p in spec["paths"] for op in spec["paths"][p].values()]
check("spec: operationId уникальны", all(oids) and len(set(oids)) == len(oids), "")

st, proto, _ = api("GET", "/ws/protocol")
check("GET /ws/protocol: close codes + события",
      st == 200 and isinstance(proto, dict) and "close_codes" in proto and any(
          ev.get("type") == "read" for ev in proto["events"] if isinstance(ev, dict)), st)

# ---------- users ----------
for nm in ("alice", "bob", "carol", "dave", "erin"):
    api("POST", "/auth/register", {"login": nm + SUF, "password": "pass12345"})
def login(nm): return api("POST", "/auth/login", {"login": nm + SUF, "password": "pass12345"})[1]["user_session_token"]
A, B, C, D, E = login("alice"), login("bob"), login("carol"), login("dave"), login("erin")
def uid(t): return api("GET", "/users/me", token=t)[1]["id"]
A_ID, B_ID, C_ID, D_ID, E_ID = uid(A), uid(B), uid(C), uid(D), uid(E)
check("5 пользователей", all([A, B, C, D, E]), "")

# ---------- авторизация: только Bearer ----------
st, b, _ = api("GET", "/users/me", token=A, auth="xheader")
check("X-Session-Token больше не работает -> 401", st == 401, f"{st} {b}")
st, b, _ = api("GET", "/users/me?user_session_token=" + A)
check("query-токен на REST не работает -> 401", st == 401, f"{st}")
st, b, _ = api("GET", "/users/me")
check("без токена 401 единым форматом", st == 401 and is_err(b), f"{st} {b}")
st, b, _ = api("POST", "/auth/login", {"login": "alice" + SUF, "password": "nope12345"})
check("login 401: 'Неверный login или password'", st == 401 and "Неверный" in str(b.get("error")), f"{b}")

# ---------- настройки удалены ----------
st, b, _ = api("GET", "/users/me/settings", token=A)
check("GET settings -> 404", st == 404 and is_err(b), f"{st} {b}")
st, b, _ = api("PATCH", "/users/me/settings", {"save_history": False}, token=A)
check("PATCH settings -> 404", st == 404, f"{st}")

# ---------- чаты: гонка direct ----------
results = []
def create_direct(tok, other):
    st, b, _ = api("POST", "/chats", {"another_user_id": other}, token=tok)
    results.append((st, b.get("chat_id") if isinstance(b, dict) else None))
threads = [threading.Thread(target=create_direct, args=args) for args in
           [(A, B_ID), (B, A_ID), (A, B_ID), (B, A_ID)]]
[t.start() for t in threads]; [t.join() for t in threads]
ids = {r[1] for r in results}; codes = sorted(r[0] for r in results)
check("4 одновременных создания direct -> один chat_id", len(ids) == 1 and None not in ids, f"{results}")
check("ровно один 201, остальные 200", codes == [200, 200, 200, 201], codes)
DM = ids.pop() if len(ids) == 1 else results[0][1]

st, b, _ = api("POST", "/chats/group", {"title": "G", "member_ids": [B_ID, C_ID]}, token=A)
G1 = b.get("chat_id"); check("группа создана", st == 201 and G1, f"{st}")
st, b, _ = api("POST", "/chats/group", {"title": "G", "member_ids": [B_ID, C_ID]}, token=A)
check("та же группа создаётся снова (другой id)", st == 201 and b.get("chat_id") != G1, f"{st}")

# ---------- сообщения + идемпотентность ----------
st, b, _ = api("POST", f"/chats/{DM}/messages", {"text": "m1"}, token=A)
M1 = b.get("message_id"); check("send -> 201 + message_id", st == 201 and M1, f"{st} {b}")
st, b, _ = api("POST", f"/chats/{DM}/messages", {"text": "повтор", "client_ref": "ref-" + SUF}, token=A)
R1 = b.get("message_id"); check("send c client_ref -> 201", st == 201 and R1, f"{st} {b}")
st, b, _ = api("POST", f"/chats/{DM}/messages", {"text": "повтор", "client_ref": "ref-" + SUF}, token=A)
check("идемпотентный повтор: тот же message_id, 200", st == 200 and b.get("message_id") == R1, f"{st} {b}")
st, b, _ = api("POST", f"/chats/{DM}/messages", {"text": "ДРУГОЙ текст", "client_ref": "ref-" + SUF}, token=A)
check("повтор с другим содержимым -> 409 client_ref_conflict",
      st == 409 and b.get("code") == "client_ref_conflict", f"{st} {b}")
st, b, _ = api("POST", f"/chats/{DM}/messages", {"text": "x", "client_ref": "r" * 65}, token=A)
check("client_ref > 64 симв -> 400", st == 400, f"{st}")
st, b, _ = api("POST", f"/chats/{G1}/messages", {"text": "gm", "client_ref": "ref-" + SUF}, token=A)
check("client_ref уникален на отправителя глобально (другой чат -> 409)",
      st == 409 and b.get("code") == "client_ref_conflict", f"{st} {b}")
st, hist, _ = api("GET", f"/chats/{DM}/messages", token=B)
mrep = [m for m in hist if m["id"] == R1]
check("история: только ОДНА копия повторного сообщения", sum(1 for m in hist if m["id"] == R1) == 1
      and mrep and mrep[0]["text"] == "повтор" and mrep[0]["content_state"] == "stored", mrep)

st, b, _ = api("POST", f"/chats/{DM}/messages", {"text": "reply", "reply_to_id": M1}, token=B)
REPLY = b.get("message_id")
st, b, _ = api("POST", f"/chats/{DM}/messages", {"text": "x", "reply_to_id": 99999999}, token=A)
check("reply на несуществующее -> 404", st == 404, f"{st}")
st, b, _ = api("POST", f"/chats/{DM}/messages", {"text": "x", "reply_to_id": R1}, token=C)
check("send не-участником -> 403", st == 403, f"{st}")
# текст всегда сохраняется (никаких настроек)
st, hist, _ = api("GET", f"/chats/{DM}/messages", token=B)
check("тексты всегда stored", all(m["content_state"] == "stored" for m in hist), "")

# ---------- правка/удаление/реакции/вложения ----------
st, b, _ = api("PATCH", f"/messages/{M1}", {"text": "edited"}, token=A)
check("edit 204", st == 204, f"{st}")
st, b, _ = api("PATCH", f"/messages/{M1}", {"text": "x"}, token=B)
check("edit не автором 403", st == 403, f"{st}")
st, b, _ = api("DELETE", f"/messages/{M1}", raw_body=b"", token=A)
check("DELETE без for_everyone -> 400", st == 400 and is_err(b), f"{st} {b}")
st, b, _ = api("POST", f"/messages/{REPLY}/reactions", {"emoji": "👍"}, token=A)
check("reaction 204", st == 204, f"{st}")
pdf = b"%PDF-1.4 loadtest attachment\n" + b"y" * 200
body, ct = multipart([("file", "doc.pdf", pdf, "application/pdf")])
st, atts, _ = api("POST", f"/messages/{REPLY}/attachments", raw_body=body, ctype=ct, token=B)
ATT = atts[0]["id"] if isinstance(atts, list) and atts else None
check("attachment автором 201 (массив)", st == 201 and ATT, f"{st} {atts}")
st, atts, _ = api("POST", f"/messages/{REPLY}/attachments", raw_body=body, ctype=ct, token=A)
check("attachment не-автором -> 403", st == 403, f"{st}")
st, b, _ = api("DELETE", f"/messages/{REPLY}", {"for_everyone": True}, token=B)
check("delete for everyone 204", st == 204, f"{st}")
st, b, _ = api("GET", f"/attachments/{ATT}", token=B)
check("вложение удалённого сообщения -> 404", st == 404, f"{st}")
st, b, _ = api("PATCH", f"/messages/{REPLY}", {"text": "z"}, token=B)
check("edit удалённого -> 409 message_deleted", st == 409 and b.get("code") == "message_deleted", f"{b}")
st, b, _ = api("POST", f"/messages/{REPLY}/reactions", {"emoji": "😢"}, token=B)
check("reaction на удалённое -> 409", st == 409 and b.get("code") == "message_deleted", f"{b}")
st, b, _ = api("POST", f"/messages/{REPLY}/attachments", raw_body=body, ctype=ct, token=B)
check("attachment к удалённому -> 409", st == 409, f"{st}")
st, hist, _ = api("GET", f"/chats/{DM}/messages", token=B)
stub = [m for m in hist if m["id"] == REPLY]
check("заглушка удалённого", stub and stub[0]["content_state"] == "deleted" and stub[0]["text"] is None
      and stub[0]["attachments"] == [], stub)
# скрытие у себя
st, b, _ = api("POST", f"/chats/{DM}/messages", {"text": "hide-me"}, token=A)
HM = b.get("message_id")
st, b, _ = api("DELETE", f"/messages/{HM}", {"for_everyone": False}, token=A)
check("скрыть у себя 204", st == 204, f"{st}")
st, b, _ = api("GET", f"/chats/{DM}/messages", token=A)
check("у себя скрыто", not any(m["id"] == HM for m in b), "")
st, b, _ = api("GET", f"/chats/{DM}/messages", token=B)
check("у другого видно", any(m["id"] == HM and m["text"] == "hide-me" for m in b), "")

# ---------- пагинация/ошибки ----------
for q in ("limit=0", "limit=101", "limit=abc", "before_id=0", "before_id=-1", "before_id=abc"):
    st, b, _ = api("GET", f"/chats/{DM}/messages?{q}", token=A)
    check(f"{q} -> 400 телом", st == 400 and is_err(b), f"{st}")
st, b, _ = api("POST", "/chats", {}, token=A)
check("POST /chats {} -> 400", st == 400 and is_err(b), f"{st} {b}")
st, b, _ = api("POST", f"/chats/{DM}/messages", raw_body=b'{"text":"x"}', ctype="text/plain", token=A)
check("не-JSON Content-Type -> 415 телом", st == 415 and is_err(b), f"{st} {b}")
st, b, _ = api("GET", "/chats/abc", token=A)
check("404 маршрута единым форматом", st == 404 and is_err(b), f"{st} {b}")

# ---------- права в группе ----------
st, b, _ = api("POST", f"/chats/{G1}/members", {"user_id": D_ID}, token=B)
check("add member рядовым участником -> 403", st == 403, f"{st}")
st, b, _ = api("POST", f"/chats/{G1}/members", {"user_id": D_ID}, token=A)
check("add member owner 204", st == 204, f"{st}")
st, b, _ = api("PATCH", f"/chats/{G1}/members/{B_ID}", {"role": "admin"}, token=A)
check("назначить admin 204", st == 204, f"{st}")
st, b, _ = api("DELETE", f"/chats/{G1}/members/{C_ID}", token=B)
check("admin исключает member 204", st == 204, f"{st}")
st, b, _ = api("GET", f"/chats/{G1}", token=C)
check("исключённый -> 403", st == 403, f"{st}")
st, b, _ = api("POST", f"/chats/{G1}/owner", {"user_id": B_ID}, token=A)
check("передача владения 204", st == 204, f"{st}")
st, b, _ = api("GET", f"/chats/{G1}/members", token=D)
roles = {m["id"]: m["role"] for m in b}
check("ровно один owner, прежний admin", roles.get(B_ID) == "owner" and roles.get(A_ID) == "admin"
      and list(roles.values()).count("owner") == 1, roles)
st, b, _ = api("POST", f"/chats/{G1}/leave", None, token=B)
check("owner не выходит без передачи -> 409", st == 409 and b.get("code") == "owner_transfer_required", f"{b}")
st, b, _ = api("POST", f"/chats/{G1}/leave", None, token=A)
check("бывший owner (admin) выходит 204", st == 204, f"{st}")

# ---------- прочтения ----------
st, b, _ = api("POST", f"/chats/{DM}/messages", {"text": "read-me"}, token=A)
RM = b.get("message_id")
st, hist, _ = api("GET", f"/chats/{DM}/messages", token=B)
rm = [m for m in hist if m["id"] == RM]
check("GET не помечает прочитанным", rm and rm[0].get("read_at") is None, rm)
st, b, _ = api("POST", f"/chats/{DM}/read", {"message_id": RM}, token=B)
check("read 204", st == 204, f"{st}")
st, hist, _ = api("GET", f"/chats/{DM}/messages", token=A)
rm = [m for m in hist if m["id"] == RM]
check("read_at появился у отправителя", rm and rm[0].get("read_at"), rm)
st, b, _ = api("POST", f"/chats/{DM}/read", {"message_id": RM}, token=E)
check("read посторонним -> 403", st == 403, f"{st}")

# ---------- аватары ----------
png = base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==")
body, ct = multipart([("file", "me.png", png, "image/png")])
st, b, _ = api("POST", "/users/me/avatar", raw_body=body, ctype=ct, token=D)
check("PNG аватар 201", st == 201, f"{st}")
body, ct = multipart([("file", "fake.png", b"<svg onload=x>", "image/png")])
st, b, _ = api("POST", "/users/me/avatar", raw_body=body, ctype=ct, token=D)
check("поддельный PNG -> 400 invalid_avatar", st == 400 and b.get("code") == "invalid_avatar", f"{b}")
big = b"\x89PNG\r\n\x1a\n" + os.urandom(5_200_000)
body, ct = multipart([("file", "big.png", big, "image/png")])
st, b, _ = api("POST", "/users/me/avatar", raw_body=body, ctype=ct, token=D)
check("аватар >5МБ -> 413", st == 413 and b.get("code") == "file_too_large", f"{b}")

# ---------- 413 вложение ----------
st, b, _ = api("POST", f"/chats/{DM}/messages", {"text": "big"}, token=A)
BM = b.get("message_id")
big2 = os.urandom(10_100_000)
body, ct = multipart([("file", "big.bin", big2, "application/octet-stream")])
st, b, _ = api("POST", f"/messages/{BM}/attachments", raw_body=body, ctype=ct, token=A)
check("вложение >10МБ -> 413", st == 413, f"{st}")

# ---------- WS ----------
D2 = login("dave")
code, ws1 = ws_connect(f"/messenger/ws?user_session_token={D}")
ev = ws_recv(ws1); check("ws connected", ev.get("type") == "connected", ev)
code, ws2 = ws_connect(f"/messenger/ws?user_session_token={D2}")
ws_recv(ws2)
ws_send(ws1, {"action": "follow", "chat_id": G1}); ev = ws_recv(ws1)
check("ws followed", ev.get("type") == "followed", ev)
ws_send(ws2, {"action": "follow", "chat_id": G1}); ws_recv(ws2)
st, b, _ = api("POST", "/auth/logout", None, token=D2)
closed = False
try:
    while True: ws_recv(ws2, 3)
except ConnectionError as ex: closed = "4001" in str(ex)
except Exception: closed = False
check("logout закрыл WS сессии (4001)", closed, "")
st, b, _ = api("POST", f"/chats/{G1}/messages", {"text": "ws-привет"}, token=B)
ev = ws_recv(ws1)
check("первая сессия получает message", ev.get("type") == "message"
      and ev["message"]["text"] == "ws-привет", ev)
st, hist, _ = api("GET", f"/chats/{G1}/messages?limit=3", token=B)
wm = [m for m in hist if m["text"] == "ws-привет"]
check("delivered_at выставлен", wm and wm[0].get("delivered_at"), wm)
code, _ = ws_connect(f"/messenger/ws?user_session_token={D2}")
check("handshake с отозванным токеном -> 401", code == 401, code)
# пропущенные события и переподключение (личный чат B<->C, carol — участник)
st, b, _ = api("POST", "/chats", {"another_user_id": C_ID}, token=B)
DM2 = b.get("chat_id")
code, wsb = ws_connect(f"/messenger/ws?user_session_token={B}")
ws_recv(wsb)
ws_send(wsb, {"action": "follow", "chat_id": DM2}); ws_recv(wsb)
code, wsc = ws_connect(f"/messenger/ws?user_session_token={C}")
ws_recv(wsc)
api("POST", f"/chats/{DM2}/messages", {"text": "пока-неподписанный"}, token=B)
ws_recv(wsb)  # bob подписан и получает
check("без follow сообщение не приходит", ws_nothing(wsc), "пришло")
ws_send(wsc, {"action": "follow", "chat_id": DM2})
ev = ws_recv(wsc)
check("follow действительного участника -> followed", ev.get("type") == "followed", ev)
st, b, _ = api("POST", f"/chats/{DM2}/messages", {"text": "после-follow"}, token=B)
ev = ws_recv(wsc)
check("после follow приходит", ev.get("type") == "message" and ev["message"]["text"] == "после-follow", ev)
ws_recv(wsb)  # bob тоже подписан и получил это же сообщение
st, hist, _ = api("GET", f"/chats/{DM2}/messages?limit=3", token=C)
check("догоняет историей (включая пропущенное)",
      any(m["text"] == "пока-неподписанный" for m in hist)
      and any(m["text"] == "после-follow" for m in hist), "")
# read событие получает подписанный собеседник
st, b, _ = api("POST", f"/chats/{DM2}/read", {"message_id": ev["message"]["id"]}, token=C)
ev = ws_recv(wsb)
check("ws read событие подписанному", ev.get("type") == "read" and ev.get("user_id") == C_ID
      and ev.get("last_read_message_id"), ev)
try: wsb.close()
except Exception: pass
try: wsc.close()
except Exception: pass

# member_removed прекращает доставку
ws_send(ws1, {"action": "ping"})
check("ws жив", ws_recv(ws1).get("type") == "pong", "")
st, b, _ = api("DELETE", f"/chats/{G1}/members/{D_ID}", token=B)
ev = ws_recv(ws1)
check("member_removed исключённому", ev.get("type") == "member_removed" and ev.get("user_id") == D_ID, ev)
api("POST", f"/chats/{G1}/messages", {"text": "после-исключения"}, token=B)
check("исключённый больше не получает", ws_nothing(ws1), "пришло")
ws_send(ws1, {"action": "follow", "chat_id": G1})
ev = ws_recv(ws1)
check("follow исключённым -> error", ev.get("type") == "error", ev)
for s in (ws1, wsc):
    try: s.close()
    except Exception: pass

# ---------- legacy not_stored (только локально, прямой вставкой в БД) ----------
if DB_FILE:
    con = sqlite3.connect(DB_FILE)
    con.execute("INSERT INTO messages(chat_id, sender_id, reply_to_id, text, sent_at) VALUES(?, ?, NULL, '', ?)",
                (DM, A_ID, "2026-01-01T00:00:00.000Z"))
    con.commit(); legacy_id = con.execute("SELECT last_insert_rowid()").fetchone()[0]; con.close()
    st, hist, _ = api("GET", f"/chats/{DM}/messages?limit=100", token=B)
    lm = [m for m in hist if m["id"] == legacy_id]
    check("legacy not_stored: text=null + content_state=not_stored",
          lm and lm[0]["text"] is None and lm[0]["content_state"] == "not_stored", lm)
    st, b, _ = api("PATCH", f"/messages/{legacy_id}", {"text": "edit"}, token=A)
    check("edit legacy -> 409 content_not_stored", st == 409 and b.get("code") == "content_not_stored", f"{b}")
    small, sct = multipart([("file", "a.txt", b"legacy attach", "text/plain")])
    st, b, _ = api("POST", f"/messages/{legacy_id}/attachments", raw_body=small, ctype=sct, token=A)
    check("attachment к legacy -> 409 content_not_stored", st == 409 and b.get("code") == "content_not_stored", f"{b}")
    st, b, _ = api("POST", f"/chats/{DM}/messages", {"text": "ответ на legacy", "reply_to_id": legacy_id}, token=B)
    check("ответ на legacy разрешён", st == 201, f"{st}")

# ---------- аудит 2026-10-13: идемпотентность после правки ----------
st, b, _ = api("POST", f"/chats/{DM}/messages", {"text": "idem-оригинал", "client_ref": "fix1-" + SUF}, token=A)
IDM = b.get("message_id")
st, b, _ = api("PATCH", f"/messages/{IDM}", {"text": "idem-после-правки"}, token=A)
check("edit идемпотентного 204", st == 204, f"{st}")
st, b, _ = api("POST", f"/chats/{DM}/messages", {"text": "idem-оригинал", "client_ref": "fix1-" + SUF}, token=A)
check("повтор ИСХОДНОГО запроса после правки -> 200 тот же id", st == 200 and b.get("message_id") == IDM, f"{st} {b}")
st, b, _ = api("POST", f"/chats/{DM}/messages", {"text": "idem-после-правки", "client_ref": "fix1-" + SUF}, token=A)
check("повтор с отредактированным текстом -> 409 (сравнение с исходником)", st == 409 and b.get("code") == "client_ref_conflict", f"{st} {b}")
st, b, _ = api("POST", f"/chats/{DM}/messages", {"text": "idem-оригинал", "reply_to_id": M1, "client_ref": "fix1-" + SUF}, token=A)
check("тот же ref с другим reply_to_id -> 409", st == 409, f"{st}")
api("DELETE", f"/messages/{IDM}", {"for_everyone": True}, token=A)
st, b, _ = api("POST", f"/chats/{DM}/messages", {"text": "idem-оригинал", "client_ref": "fix1-" + SUF}, token=A)
check("повтор после удаления для всех -> 200 тот же id (ключ занят)", st == 200 and b.get("message_id") == IDM, f"{st} {b}")

# ---------- аудит: after_id синхронизация пропущенного ----------
st, b, _ = api("POST", f"/chats/{DM}/messages", {"text": "sync-a"}, token=A)
SA = b.get("message_id")
st, b, _ = api("POST", f"/chats/{DM}/messages", {"text": "sync-b"}, token=B)
SB = b.get("message_id")
st, b, _ = api("POST", f"/chats/{DM}/messages", {"text": "sync-c"}, token=A)
SC = b.get("message_id")
st, page, _ = api("GET", f"/chats/{DM}/messages?after_id={SA}&limit=2", token=B)
check("after_id: сообщения НОВЕЕ курсора по возрастанию",
      [m["id"] for m in page] == [SB, SC] and [m["text"] for m in page] == ["sync-b", "sync-c"], page)
st, page, _ = api("GET", f"/chats/{DM}/messages?after_id={SB}", token=B)
check("after_id до пустой страницы", [m["id"] for m in page] == [SC], page)
st, b, _ = api("GET", f"/chats/{DM}/messages?after_id=abc", token=B)
check("after_id=abc -> 400 телом", st == 400 and is_err(b), f"{st} {b}")
st, b, _ = api("GET", f"/chats/{DM}/messages?after_id=0", token=B)
check("after_id=0 -> 400", st == 400, f"{st}")

# ---------- аудит: read-событие монотонно ----------
code, wsr = ws_connect(f"/messenger/ws?user_session_token={B}")
ws_recv(wsr)
ws_send(wsr, {"action": "follow", "chat_id": DM}); ws_recv(wsr)
st, b, _ = api("POST", f"/chats/{DM}/read", {"message_id": SC}, token=B)
ev = ws_recv(wsr)
check("read событие: курсор вперёд", ev.get("type") == "read" and ev.get("last_read_message_id") == SC, ev)
st, b, _ = api("POST", f"/chats/{DM}/read", {"message_id": M1}, token=B)
check("read старее курсора: события-отката НЕТ", ws_nothing(wsr, 1.5), "пришло")
try: wsr.close()
except Exception: pass

# ---------- аудит: спека ----------
hist_op = opspec("/chats/{chat_id}/messages", "get")
params = {p["name"] for p in hist_op.get("parameters", [])}
check("spec: у истории есть limit/before_id/after_id", {"limit", "before_id", "after_id"} <= params, params)
no_summary = [p for p in spec["paths"] for m in spec["paths"][p]
              if not spec["paths"][p][m].get("summary")]
check("spec: summary есть у всех операций", not no_summary, no_summary[:4])
send415 = "415" in opspec("/chats/{chat_id}/messages", "post").get("responses", {})
check("spec: 415 описан у отправки", send415, "")
smi2 = spec["components"]["schemas"]["SendMessageIn"]
check("spec: text не nullable (вход)", smi2["properties"]["text"].get("nullable") is not True, "")
st, b, _ = api("POST", f"/chats/{DM}/messages", raw_body=json.dumps({"text": "спуф", "sender_id": B_ID, "unsupported": 1}).encode(), token=A)
check("лишние/спуф-поля тела -> 400 (строгий контракт)", st == 400 and is_err(b), f"{st} {b}")
cgi2 = spec["components"]["schemas"]["CreateGroupIn"]
check("spec: member_ids без uniqueItems (дубли нормализуются)", cgi2["properties"]["member_ids"].get("uniqueItems") is not True, "")
chats200 = json.dumps(opspec("/chats", "get").get("responses", {}).get("200", {}))
check("spec: GET /chats имеет схему oneOf карточек", "DirectChatOut" in chats200 and "GroupChatOut" in chats200, chats200[:80])

# ---------- rate limit ----------
codes = [api("POST", "/auth/login", {"login": "rl" + SUF, "password": "x" * 12})[0] for _ in range(35)]
st, _, h = api("POST", "/auth/login", {"login": "rl" + SUF, "password": "x" * 12})
check("rate limit 429 + Retry-After", 429 in codes and h.get("Retry-After") is not None,
      f"{sorted(set(codes))} RA={h.get('Retry-After')}")

print(f"\n===== ИТОГ: {len(PASSED)} passed, {len(FAILED)} failed =====")
if FAILED:
    print("\nПРОБЛЕМЫ:")
    for n, ctx in FAILED: print(f"  - {n}  [{ctx}]")
sys.exit(1 if FAILED else 0)
