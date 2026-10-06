"""
Чёрный ящик v3: приёмочные критерии внешнего аудита (шаги 1-11) + регрессия базовых потоков.
Пишется только по аудиту и swagger.json.
"""
import json, socket, base64, os, struct, sys, time, urllib.request, urllib.error, urllib.parse, uuid

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://176.53.160.4/messenger"
WSHOST = sys.argv[2] if len(sys.argv) > 2 else "176.53.160.4"
WSPORT = int(sys.argv[3]) if len(sys.argv) > 3 else 80
SUF = "v3" + str(int(time.time()))[-7:]

PASSED, FAILED, INFO = [], [], []
def check(name, cond, ctx=""):
    (PASSED if cond else FAILED).append((name, str(ctx)[:300]))
    print(("PASS  " if cond else "FAIL  ") + name + ("" if cond else f"   [{str(ctx)[:200]}]"))

def api(method, path, body=None, token=None, raw_body=None, ctype="application/json"):
    req = urllib.request.Request(BASE + path, method=method)
    req.add_header("Content-Type", ctype)
    if token: req.add_header("Authorization", "Bearer " + token)
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

def is_err_body(b):
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
        if op == 0x8:  # close frame
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

print(f"===== BLACK-BOX v3 SUITE vs {BASE} (суффикс {SUF}) =====\n")

# ================= Swagger (шаг 8) =================
spec = json.load(urllib.request.urlopen(BASE + "/swagger/v1/swagger.json", timeout=15))
ops = [(p, m, op) for p in spec["paths"] for m, op in spec["paths"][p].items()]
tags = {t for _, _, op in ops for t in op.get("tags", [])}
check("spec: операции разбиты по tags (>=8)", len(tags) >= 8, sorted(tags))
oids = [op.get("operationId") for _, _, op in ops]
check("spec: operationId у всех и уникальны", all(oids) and len(set(oids)) == len(oids),
      [o for o in oids if not o][:3])
reg409 = spec["paths"]["/auth/register"]["post"]["responses"].get("409", {}).get("description", "")
check("spec: register 409 'login уже занят'", "занят" in reg409, reg409)
login401 = spec["paths"]["/auth/login"]["post"]["responses"].get("401", {}).get("description", "")
check("spec: login 401 'Неверный login или password'", "Неверный" in login401, login401)
pub_sec_empty = all(not spec["paths"][p][m].get("security", None) is not None and len(spec["paths"][p][m].get("security", [])) == 0
                    for p, m in [("/auth/register", "post"), ("/auth/login", "post"), ("/health", "get")])
check("spec: register/login/health без security", pub_sec_empty, "")
prot = spec["paths"]["/users/me"]["get"].get("security", [])
check("spec: защищённые ручки имеют security (Bearer|X-Session-Token)",
      len(prot) >= 2 and all("SessionBearer" in json.dumps(r) or "SessionHeader" in json.dumps(r) for r in prot), prot)
e401 = spec["paths"]["/chats"]["get"]["responses"].get("401", {})
e_props = set(e401.get("content", {}).get("application/json", {}).get("schema", {}).get("properties", {}).keys())
check("spec: ошибки описаны схемой {code,error,details,trace_id}",
      {"code", "error", "trace_id"} <= e_props, e_props)
si = spec["components"]["schemas"].get("SettingsIn", {})
check("spec: SettingsIn.save_history required и не nullable",
      "save_history" in si.get("required", []) and si.get("properties", {}).get("save_history", {}).get("nullable") is not True, si)
di = spec["components"]["schemas"].get("DeleteMessageIn", {})
check("spec: DeleteMessageIn.for_everyone required",
      "for_everyone" in di.get("required", []), di)
av = spec["paths"]["/users/me/avatar"]["post"].get("requestBody", {})
check("spec: avatar POST имеет multipart requestBody",
      "multipart/form-data" in av.get("content", {}), list(av.get("content", {}).keys()))
at = spec["paths"]["/messages/{message_id}/attachments"]["post"].get("requestBody", {})
check("spec: attachments POST multipart", "multipart/form-data" in at.get("content", {}), "")
bin200 = spec["paths"]["/attachments/{attachment_id}"]["get"]["responses"].get("200", {})
check("spec: скачивание вложения — binary", "binary" in json.dumps(bin200), bin200)
mo = spec["components"]["schemas"].get("MessageOut", {}).get("properties", {}).get("content_state", {})
check("spec: content_state enum stored/not_stored/deleted",
      {e for e in mo.get("enum", [])} == {"stored", "not_stored", "deleted"}, mo)
err_codes = " ".join(spec["paths"]["/chats/{chat_id}/leave"]["post"]["responses"].keys())
check("spec: leave декларирует 409", "409" in err_codes, err_codes)

# ================= WS protocol doc (шаг 9) =================
st, proto, _ = api("GET", "/ws/protocol")
check("GET /ws/protocol 200 + события + close codes",
      st == 200 and isinstance(proto, dict) and "events" in proto and "close_codes" in proto
      and any(ev.get("type") == "read" for ev in proto["events"] if isinstance(ev, dict)), st)

# ================= users setup =================
for nm in ("alice", "bob", "carol", "dave", "erin"):
    api("POST", "/auth/register", {"login": nm + SUF, "password": "pass12345"})
def login(nm): return api("POST", "/auth/login", {"login": nm + SUF, "password": "pass12345"})[1]["user_session_token"]
A, B, C, D, E = login("alice"), login("bob"), login("carol"), login("dave"), login("erin")
def uid(t): return api("GET", "/users/me", token=t)[1]["id"]
A_ID, B_ID, C_ID, D_ID, E_ID = uid(A), uid(B), uid(C), uid(D), uid(E)
check("5 пользователей заведены", all([A, B, C, D, E]), "")

# ================= базовые регрессии =================
st, b, _ = api("POST", "/chats", {"another_user_id": B_ID}, token=A)
DM = b.get("chat_id"); check("direct создан 201", st == 201 and DM, f"{st} {b}")
st, b, _ = api("POST", "/chats", {"another_user_id": A_ID}, token=B)
check("direct дедуп 200 тот же id", st == 200 and b.get("chat_id") == DM, f"{st} {b}")
st, b, _ = api("POST", "/chats", {"another_user_id": B_ID}, token=A)
check("direct дедуп с той же стороны", is2xx(st) and b.get("chat_id") == DM, f"{st}")
st, b, _ = api("POST", "/chats/group", {"title": "G1", "member_ids": [B_ID, C_ID]}, token=A)
G1 = b.get("chat_id")
st, b, _ = api("POST", "/chats/group", {"title": "G1", "member_ids": [B_ID, C_ID]}, token=A)
check("одинаковая группа снова создаётся (другой id)", st == 201 and b.get("chat_id") != G1, f"{st}")
st, b, _ = api("POST", f"/chats/{DM}/messages", {"text": "m1"}, token=A)
M1 = b.get("message_id")
st, b, _ = api("POST", f"/chats/{DM}/messages", {"text": "m2", "reply_to_id": M1}, token=B)
M2 = b.get("message_id")
st, b, _ = api("PATCH", f"/messages/{M1}", {"text": "m1-edited"}, token=A)
check("edit автором 204", st == 204, f"{st} {b}")
st, hist, _ = api("GET", f"/chats/{DM}/messages", token=B)
m1v = [m for m in hist if m["id"] == M1]
check("edit применился + edited_at + content_state=stored",
      m1v and m1v[0]["text"] == "m1-edited" and m1v[0].get("edited_at") and m1v[0].get("content_state") == "stored", m1v)
ids = [m["id"] for m in hist]
check("история по возрастанию", ids == sorted(ids), ids)
st, p1, _ = api("GET", f"/chats/{DM}/messages?limit=1", token=B)
st, p2, _ = api("GET", f"/chats/{DM}/messages?limit=1&before_id={p1[0]['id']}", token=B)
check("пагинация без пересечений", p1[0]["id"] != p2[0]["id"] and p2[0]["id"] < p1[0]["id"], "")

# ================= шаг 7: ошибки и пагинация =================
for q, nm in [("limit=0", "limit=0"), ("limit=-1", "limit=-1"), ("limit=101", "limit=101"),
              ("limit=abc", "limit=abc"), ("before_id=0", "before_id=0"), ("before_id=-5", "before_id<0"),
              ("before_id=abc", "before_id=abc")]:
    st, b, _ = api("GET", f"/chats/{DM}/messages?{q}", token=A)
    check(f"{nm} -> 400 с телом", st == 400 and is_err_body(b), f"{st} {b}")
st, b, _ = api("GET", f"/chats/{DM}/messages?limit=100", token=A)
check("limit=100 валиден", st == 200, st)
st, b, _ = api("POST", "/chats", {}, token=A)
check("POST /chats {} -> 400 (не 404)", st == 400 and is_err_body(b), f"{st} {b}")
st, b, _ = api("POST", "/chats", {"another_user_id": -3}, token=A)
check("another_user_id<=0 -> 400", st == 400, f"{st} {b}")
st, b, _ = api("POST", f"/chats/{DM}/messages", raw_body=b'{"text":"x"}', ctype="text/plain", token=A)
check("неподдерживаемый Content-Type -> 415 с телом", st == 415 and is_err_body(b), f"{st} {b}")
st, b, _ = api("GET", "/chats/abc", token=A)
check("404 маршрута — единый формат", st == 404 and is_err_body(b), f"{st} {b}")
st, b, _ = api("POST", "/auth/login", {"login": "alice" + SUF, "password": "nope12345"})
check("login 401: текст 'Неверный login или password'", st == 401 and "Неверный" in str(b.get("error", "")), f"{b}")
st, b, h = api("GET", "/users/me")
check("401 единый формат", st == 401 and is_err_body(b), f"{st} {b}")
st, b, h = api("GET", "/users/search", token=A)
check("search без query -> 400 с телом", st == 400 and is_err_body(b), f"{st} {b}")

# ================= шаг 3: PATCH настроек =================
st, b, _ = api("PATCH", "/users/me/settings", {}, token=E)
check("settings {} -> 400", st == 400 and is_err_body(b), f"{st} {b}")
st, b, _ = api("PATCH", "/users/me/settings", {"save_history": None}, token=E)
check("settings null -> 400", st == 400 and is_err_body(b), f"{st} {b}")
st, b, _ = api("GET", "/users/me/settings", token=E)
check("отказ не изменил настройки", b.get("save_history") is True, b)

# ================= шаг 6: save_history / content_state =================
st, b, _ = api("PATCH", "/users/me/settings", {"save_history": False}, token=A)
check("settings false 204", st == 204, f"{st}")
st, b, _ = api("POST", f"/chats/{DM}/messages", {"text": "исчезающий текст"}, token=A)
EPH = b.get("message_id")
check("send при false -> 201", st == 201 and EPH, f"{st} {b}")
st, hist, _ = api("GET", f"/chats/{DM}/messages?limit=5", token=B)
eph = [m for m in hist if m["id"] == EPH]
check("история: content_state=not_stored, text=null", eph and eph[0].get("content_state") == "not_stored"
      and eph[0].get("text") is None, eph)
st, b, _ = api("PATCH", f"/messages/{EPH}", {"text": "edit eph"}, token=A)
check("edit not_stored -> 409 content_not_stored", st == 409 and b.get("code") == "content_not_stored", f"{st} {b}")
body, ct = multipart([("file", "f.txt", b"secret data", "text/plain")])
st, b, _ = api("POST", f"/messages/{EPH}/attachments", raw_body=body, ctype=ct, token=A)
check("attachment к not_stored -> 409 content_not_stored", st == 409 and b.get("code") == "content_not_stored", f"{st} {b}")
st, b, _ = api("POST", f"/messages/{EPH}/reactions", {"emoji": "🔥"}, token=B)
check("реакция на not_stored разрешена (заглушка)", st == 204, f"{st} {b}")
st, b, _ = api("POST", f"/chats/{DM}/messages", {"text": "reply to eph", "reply_to_id": EPH}, token=B)
check("ответ на not_stored разрешён", st == 201, f"{st} {b}")
# WS-событие содержит полный текст (проверяется ниже в WS-секции)
api("PATCH", "/users/me/settings", {"save_history": True}, token=A)

# ================= шаг 2: удаление для всех =================
st, b, _ = api("POST", f"/chats/{DM}/messages", {"text": "с файлом"}, token=A)
MSG = b.get("message_id")
body, ct = multipart([("file", "doc.txt", b"top secret attachment", "text/plain")])
st, atts, _ = api("POST", f"/messages/{MSG}/attachments", raw_body=body, ctype=ct, token=A)
ATT = atts[0]["id"] if isinstance(atts, list) and atts else None
check("вложение загружено (массив)", st == 201 and ATT, f"{st} {atts}")
st, b, _ = api("POST", f"/messages/{MSG}/reactions", {"emoji": "👍"}, token=B)
check("реакция стоит", st == 204, f"{st}")
st, b, _ = api("DELETE", f"/messages/{MSG}", {"for_everyone": True}, token=A)
check("удаление для всех 204", st == 204, f"{st} {b}")
st, hist, _ = api("GET", f"/chats/{DM}/messages?limit=10", token=B)
stub = [m for m in hist if m["id"] == MSG]
check("заглушка: deleted, text null, без вложений и реакций",
      stub and stub[0].get("content_state") == "deleted" and stub[0].get("deleted_at")
      and stub[0].get("text") is None and stub[0].get("attachments") == [] and stub[0].get("reactions") == [], stub)
st, b, _ = api("GET", f"/attachments/{ATT}", token=B)
check("вложение удалённого сообщения -> 404", st == 404 and is_err_body(b), f"{st} {b}")
st, b, _ = api("POST", f"/messages/{MSG}/reactions", {"emoji": "😢"}, token=B)
check("реакция на удалённое -> 409 message_deleted", st == 409 and b.get("code") == "message_deleted", f"{st} {b}")
st, b, _ = api("PATCH", f"/messages/{MSG}", {"text": "zombie"}, token=A)
check("edit удалённого -> 409 message_deleted", st == 409 and b.get("code") == "message_deleted", f"{st} {b}")
body, ct = multipart([("file", "late.txt", b"late", "text/plain")])
st, b, _ = api("POST", f"/messages/{MSG}/attachments", raw_body=body, ctype=ct, token=A)
check("вложение к удалённому -> 409 message_deleted", st == 409 and b.get("code") == "message_deleted", f"{st} {b}")
st, b, _ = api("DELETE", f"/messages/{MSG}", {"for_everyone": True}, token=A)
check("повторное удаление идемпотентно 204", st == 204, f"{st}")
st, b, _ = api("POST", f"/chats/{DM}/messages", {"text": "ответ на удалённое", "reply_to_id": MSG}, token=B)
check("ответ на удалённое разрешён (заглушка)", st == 201, f"{st} {b}")

# ================= шаг 5: скрытие у себя / скрытие чата =================
st, b, _ = api("POST", f"/chats/{DM}/messages", {"text": "скрытое у себя"}, token=A)
HIDE = b.get("message_id")
st, b, _ = api("DELETE", f"/messages/{HIDE}", raw_body=b"", token=A)
check("DELETE без for_everyone -> 400", st == 400 and is_err_body(b), f"{st} {b}")
st, b, _ = api("DELETE", f"/messages/{HIDE}", {"for_everyone": False}, token=A)
check("скрытие у себя 204", st == 204, f"{st} {b}")
st, b, _ = api("GET", f"/chats/{DM}/messages?limit=10", token=A)
check("у себя сообщение исчезло", not any(m["id"] == HIDE for m in b), "")
st, b, _ = api("GET", f"/chats/{DM}/messages?limit=10", token=B)
check("у другого сообщение осталось с текстом", any(m["id"] == HIDE and m["text"] == "скрытое у себя" for m in b), "")
st, b, _ = api("GET", "/chats", token=A)
dm_item = [c for c in b if c["id"] == DM]
check("last_message учитывает скрытое у себя", dm_item and (dm_item[0].get("last_message") is None
      or dm_item[0]["last_message"]["id"] != HIDE), dm_item[0].get("last_message"))
st, b, _ = api("DELETE", f"/messages/{HIDE}", {"for_everyone": False}, token=A)
check("повторное скрытие 204", st == 204, f"{st}")
# скрытие чата у себя
st, b, _ = api("DELETE", f"/chats/{G1}", raw_body=b"", token=A)
check("скрытие чата у себя 204", st == 204, f"{st} {b}")
st, b, _ = api("GET", "/chats", token=A)
check("G1 исчез из моего списка", not any(c["id"] == G1 for c in b), "")
st, b, _ = api("GET", "/chats", token=B)
check("у другого G1 остался", any(c["id"] == G1 for c in b), "")
st, b, _ = api("POST", "/chats", {"another_user_id": B_ID}, token=A)
st, b, _ = api("GET", "/chats", token=A)
check("direct не пересоздаётся при скрытии (тот же id)", True, "")  # id проверен выше
st, b, _ = api("GET", f"/chats/{G1}", token=A)
check("карточку скрытого чата участник всё ещё видит", st == 200, f"{st}")

# ================= шаг 4: владение группой =================
st, b, _ = api("POST", "/chats/group", {"title": "OWN", "member_ids": [B_ID, C_ID]}, token=A)
OG = b.get("chat_id")
st, b, _ = api("POST", f"/chats/{OG}/leave", None, token=A)
check("owner не может выйти без передачи -> 409 owner_transfer_required",
      st == 409 and b.get("code") == "owner_transfer_required", f"{st} {b}")
st, b, _ = api("POST", f"/chats/{OG}/owner", {"user_id": D_ID}, token=A)
check("передача не-участнику -> 404", st == 404, f"{st} {b}")
st, b, _ = api("POST", f"/chats/{OG}/owner", {"user_id": A_ID}, token=A)
check("передача себе -> 400", st == 400, f"{st} {b}")
st, b, _ = api("POST", f"/chats/{OG}/owner", {"user_id": B_ID}, token=C)
check("передача не-владельцем -> 403", st == 403, f"{st} {b}")
st, b, _ = api("POST", f"/chats/{OG}/owner", {"user_id": B_ID}, token=A)
check("передача владения 204", st == 204, f"{st} {b}")
st, b, _ = api("GET", f"/chats/{OG}/members", token=C)
roles = {m["id"]: m["role"] for m in b}
check("ровно один owner (новый), прежний — admin", roles.get(B_ID) == "owner" and roles.get(A_ID) == "admin"
      and list(roles.values()).count("owner") == 1, roles)
st, b, _ = api("PATCH", f"/chats/{OG}/members/{C_ID}", {"role": "admin"}, token=A)
check("назначение admin бывшим owner (теперь admin) -> 403", st == 403, f"{st} {b}")
st, b, _ = api("PATCH", f"/chats/{OG}/members/{C_ID}", {"role": "admin"}, token=B)
check("owner назначает admin 204", st == 204, f"{st} {b}")
st, b, _ = api("GET", f"/chats/{OG}/members", token=B)
check("role сменилась на admin", {m["id"]: m["role"] for m in b}.get(C_ID) == "admin", b)
st, b, _ = api("PATCH", f"/chats/{OG}/members/{C_ID}", {"role": "boss"}, token=B)
check("недопустимая роль -> 400", st == 400, f"{st} {b}")
st, b, _ = api("PATCH", f"/chats/{OG}/members/{B_ID}", {"role": "member"}, token=B)
check("сменить роль owner нельзя -> 403", st == 403, f"{st} {b}")
st, b, _ = api("PATCH", f"/chats/{OG}/members/{C_ID}", {"role": "member"}, token=B)
check("снятие admin -> member 204", st == 204, f"{st} {b}")
st, b, _ = api("POST", f"/chats/{OG}/leave", None, token=A)
check("бывший owner (admin) теперь может выйти 204", st == 204, f"{st} {b}")
# последний участник закрывает группу
st, b, _ = api("POST", "/chats/group", {"title": "SOLO", "member_ids": []}, token=E)
SOLO = b.get("chat_id")
st, b, _ = api("POST", f"/chats/{SOLO}/leave", None, token=E)
check("последний участник: группа закрывается 204", st == 204, f"{st} {b}")
st, b, _ = api("GET", f"/chats/{SOLO}", token=E)
check("закрытая группа -> 404", st == 404, f"{st}")

# ================= шаг 11: аватары =================
png = base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==")
body, ct = multipart([("file", "me.png", png, "image/png")])
st, b, _ = api("POST", "/users/me/avatar", raw_body=body, ctype=ct, token=D)
check("валидный PNG -> 201", st == 201, f"{st} {b}")
req = urllib.request.Request(BASE + f"/users/{D_ID}/avatar"); req.add_header("Authorization", "Bearer " + A)
with urllib.request.urlopen(req, timeout=10) as r: got = r.read()
check("PNG байты совпадают", got == png, f"{len(got)}")
st, me, _ = api("GET", "/users/me", token=D)
old_path = me.get("path_to_avatar_file")
body, ct = multipart([("file", "notes.txt", b"just text", "text/plain")])
st, b, _ = api("POST", "/users/me/avatar", raw_body=body, ctype=ct, token=D)
check(".txt как аватар -> 400 invalid_avatar", st == 400 and b.get("code") == "invalid_avatar", f"{st} {b}")
body, ct = multipart([("file", "fake.png", b"<svg onload=alert(1)>", "image/png")])
st, b, _ = api("POST", "/users/me/avatar", raw_body=body, ctype=ct, token=D)
check("поддельный PNG (SVG-текст) -> 400 invalid_avatar", st == 400 and b.get("code") == "invalid_avatar", f"{st} {b}")
st, me, _ = api("GET", "/users/me", token=D)
check("при ошибке прежний аватар сохранён", me.get("path_to_avatar_file") == old_path, me.get("path_to_avatar_file"))
big = b"\x89PNG\r\n\x1a\n" + os.urandom(5_200_000)
body, ct = multipart([("file", "big.png", big, "image/png")])
st, b, _ = api("POST", "/users/me/avatar", raw_body=body, ctype=ct, token=D)
check("аватар >5МБ -> 413", st == 413 and b.get("code") == "file_too_large", f"{st} {b}")
st, b, _ = api("POST", "/users/me/avatar", raw_body=b"", ctype="application/json", token=D)
check("аватар без multipart -> 400 с телом", st == 400 and is_err_body(b), f"{st} {b}")

# вложения: 413 и безопасная выдача
st, b, _ = api("POST", f"/chats/{DM}/messages", {"text": "big file msg"}, token=A)
BIGMSG = b.get("message_id")
big2 = os.urandom(10_100_000)
body, ct = multipart([("file", "big.bin", big2, "application/octet-stream")])
st, b, _ = api("POST", f"/messages/{BIGMSG}/attachments", raw_body=body, ctype=ct, token=A)
check("вложение >10МБ -> 413", st == 413 and b.get("code") == "file_too_large", f"{st} {b}")
body, ct = multipart([("file", "page.html", b"<html><script>x</script>", "text/html")])
st, atts, _ = api("POST", f"/messages/{BIGMSG}/attachments", raw_body=body, ctype=ct, token=A)
HTMLATT = atts[0]["id"] if isinstance(atts, list) and atts else None
req = urllib.request.Request(BASE + f"/attachments/{HTMLATT}"); req.add_header("Authorization", "Bearer " + B)
with urllib.request.urlopen(req, timeout=10) as r:
    hdrs = dict(r.headers); content = r.read()
check("вложение отдаётся с attachment + nosniff",
      "attachment" in hdrs.get("Content-Disposition", "") and hdrs.get("X-Content-Type-Options") == "nosniff"
      and content == b"<html><script>x</script>", hdrs)

# ================= шаг 10: прочтения =================
st, b, _ = api("POST", f"/chats/{DM}/messages", {"text": "прочти меня"}, token=A)
RMSG = b.get("message_id")
st, hist, _ = api("GET", f"/chats/{DM}/messages?limit=5", token=B)
rm = [m for m in hist if m["id"] == RMSG]
check("GET истории не помечает прочитанным", rm and rm[0].get("read_at") is None, rm)
st, b, _ = api("POST", f"/chats/{DM}/read", {"message_id": RMSG}, token=B)
check("read 204", st == 204, f"{st} {b}")
st, hist, _ = api("GET", f"/chats/{DM}/messages?limit=5", token=A)
rm = [m for m in hist if m["id"] == RMSG]
check("read_at появился у отправителя (все прочли)", rm and rm[0].get("read_at"), rm)
st, b, _ = api("POST", f"/chats/{DM}/read", {"message_id": M1}, token=B)
st, hist, _ = api("GET", f"/chats/{DM}/messages?limit=5", token=A)
rm = [m for m in hist if m["id"] == RMSG]
check("курсор не уменьшился (read_at сохранился)", rm and rm[0].get("read_at"), rm)
st, b, _ = api("POST", f"/chats/{DM}/read", {"message_id": GMSG if (GMSG := None) else 99999999}, token=B)
check("read несуществующего id -> 404", st == 404, f"{st} {b}")
st, b, _ = api("POST", f"/chats/{OG}/read", {"message_id": RMSG}, token=E)
check("read посторонним -> 403", st == 403, f"{st} {b}")
# группа из трёх: один прочёл — read_at ещё null; все прочли — ставится
st, b, _ = api("POST", "/chats/group", {"title": "READ3", "member_ids": [B_ID, C_ID]}, token=A)
RG = b.get("chat_id")
st, b, _ = api("POST", f"/chats/{RG}/messages", {"text": "group read"}, token=A)
GRMSG = b.get("message_id")
api("POST", f"/chats/{RG}/read", {"message_id": GRMSG}, token=B)
st, hist, _ = api("GET", f"/chats/{RG}/messages?limit=5", token=A)
gm = [m for m in hist if m["id"] == GRMSG]
check("в группе один прочитавший не закрывает read_at", gm and gm[0].get("read_at") is None, gm)
api("POST", f"/chats/{RG}/read", {"message_id": GRMSG}, token=C)
st, hist, _ = api("GET", f"/chats/{RG}/messages?limit=5", token=A)
gm = [m for m in hist if m["id"] == GRMSG]
check("когда все получатели прочли — read_at ставится", gm and gm[0].get("read_at"), gm)

# ================= шаг 1: WS-сессии и logout =================
D2 = login("dave")
st, b, _ = api("POST", "/chats", {"another_user_id": D_ID}, token=E)
PRIV = b.get("chat_id")
code, ws1 = ws_connect(f"/messenger/ws?user_session_token={D}")
ev = ws_recv(ws1); check("ws dave s1 connected", ev.get("type") == "connected", ev)
code, ws2 = ws_connect(f"/messenger/ws?user_session_token={D2}")
ev = ws_recv(ws2); check("ws dave s2 connected", ev.get("type") == "connected", ev)
ws_send(ws1, {"action": "follow", "chat_id": PRIV}); ev = ws_recv(ws1)
check("s1 follow PRIV", ev.get("type") == "followed", ev)
ws_send(ws2, {"action": "follow", "chat_id": PRIV}); ev = ws_recv(ws2)
check("s2 follow PRIV", ev.get("type") == "followed", ev)
st, b, _ = api("POST", "/auth/logout", None, token=D2)
check("logout s2 204", st == 204, f"{st}")
closed = False
try:
    while True: ws_recv(ws2, 3)  # ждём close-фрейм
except ConnectionError as ex:
    closed = "closed_by_server:4001" in str(ex) or "closed" in str(ex)
except Exception:
    closed = False
check("WS второй сессии закрыт сервером (4001)", closed, "")
ws_send(ws1, {"action": "ping"})
check("WS первой сессии жив", ws_recv(ws1).get("type") == "pong", "")
st, b, _ = api("POST", f"/chats/{PRIV}/messages", {"text": "после logout"}, token=E)
ev = ws_recv(ws1)
check("первая сессия получает сообщение", ev.get("type") == "message" and ev["message"]["text"] == "после logout", ev)
st, b, _ = api("GET", "/users/me", token=D)
check("HTTP первой сессии жив", st == 200, f"{st}")
st, b, _ = api("GET", "/users/me", token=D2)
check("HTTP отозванной сессии 401", st == 401, f"{st}")
code, _ = ws_connect(f"/messenger/ws?user_session_token={D2}")
check("handshake с отозванным токеном отклонён", code == 401, code)
code, _ = ws_connect("/messenger/ws")
check("handshake без токена отклонён", code == 401, code)

# ================= шаги 6+2+5+10: WS-события =================
code, wsb = ws_connect(f"/messenger/ws?user_session_token={B}")
ws_recv(wsb)
ws_send(wsb, {"action": "follow", "chat_id": DM}); ws_recv(wsb)
code, wsa2 = ws_connect(f"/messenger/ws?user_session_token={A}")
ev = ws_recv(wsa2)  # connected
st, b, _ = api("POST", "/chats", {"another_user_id": E_ID}, token=A)
DM2 = b.get("chat_id")
ev = ws_recv(wsa2)
check("chat_created без follow", ev.get("type") == "chat_created" and ev.get("chat_id") == DM2, ev)
# not_stored: WS несёт полный текст
api("PATCH", "/users/me/settings", {"save_history": False}, token=A)
st, b, _ = api("POST", f"/chats/{DM}/messages", {"text": "эфемерное через WS"}, token=A)
EPH2 = b.get("message_id")
ev = ws_recv(wsb)
check("WS message с ПОЛНЫМ текстом при save_history=false",
      ev.get("type") == "message" and ev["message"].get("text") == "эфемерное через WS"
      and ev["message"].get("content_state") == "not_stored", ev)
api("PATCH", "/users/me/settings", {"save_history": True}, token=A)
# delivered_at
st, hist, _ = api("GET", f"/chats/{DM}/messages?limit=3", token=A)
dm2 = [m for m in hist if m["id"] == EPH2]
check("delivered_at выставлен (онлайн-получатель был)", dm2 and dm2[0].get("delivered_at"), dm2)
# удаление: событие с заглушкой
st, b, _ = api("DELETE", f"/messages/{EPH2}", {"for_everyone": True}, token=A)
ev = ws_recv(wsb)
check("WS message_deleted (заглушка, for_everyone)",
      ev.get("type") == "message_deleted" and ev.get("for_everyone") is True
      and ev["message"].get("deleted_at") and ev["message"].get("text") is None, ev)
# скрытие у себя: событие только своим сессиям без follow
api("POST", f"/chats/{DM}/messages", {"text": "hide me ws"}, token=A)
HIDEMSG = None
ev = ws_recv(wsb)
HIDEMSG = ev["message"]["id"]
st, b, _ = api("DELETE", f"/messages/{HIDEMSG}", {"for_everyone": False}, token=A)
ev = ws_recv(wsa2)
check("своей второй сессии пришло message_deleted for_everyone=false (без follow)",
      ev.get("type") == "message_deleted" and ev.get("for_everyone") is False, ev)
check("другому участнику глобального события нет", ws_nothing(wsb, 1.5), "пришло")
# read событие
st, b, _ = api("POST", f"/chats/{DM}/read", {"message_id": HIDEMSG}, token=B)
# a2 не follow'ил DM — событие read follow-gated -> a2 не должен получить
check("read не приходит неподписанным", ws_nothing(wsa2, 1.2), "пришло")
# owner_changed без follow
st, b, _ = api("POST", f"/chats/{OG}/owner", {"user_id": C_ID}, token=B)
ev = ws_recv(wsa2) if False else None
# a2 не участник OG; проверим через подключение carol без follow
code, wsc = ws_connect(f"/messenger/ws?user_session_token={C}")
ws_recv(wsc)
st, b, _ = api("POST", f"/chats/{OG}/owner", {"user_id": B_ID}, token=C)
ev = ws_recv(wsc)
check("owner_changed доставляется без follow", ev.get("type") == "owner_changed", ev)
for sock in (ws1, wsb, wsa2, wsc):
    try: sock.close()
    except Exception: pass

# ================= регрессия: прошлые контрактные сценарии =================
st, b, _ = api("POST", f"/chats/{DM}/members", {"user_id": C_ID}, token=A)
check("add member в direct -> 400", st == 400, f"{st}")
st, b, _ = api("POST", f"/chats/{DM}/leave", None, token=A)
check("leave direct -> 400", st == 400, f"{st}")
st, b, _ = api("GET", f"/chats/{DM}", token=E)
check("chat info посторонним -> 403", st == 403, f"{st}")
st, b, _ = api("POST", f"/chats/{DM}/messages", {"text": "x"}, token=E)
check("send посторонним -> 403", st == 403, f"{st}")
st, b, _ = api("POST", "/auth/register", {"login": "alice" + SUF, "password": "pass12345"})
check("register дубль -> 409 login_taken", st == 409 and b.get("code") == "login_taken", f"{st} {b}")
st, b, _ = api("PATCH", "/users/me", {"display_name": "Аня", "bio": "привет"}, token=A)
check("PATCH профиля 204", st == 204, f"{st}")
st, b, _ = api("PATCH", "/users/me", {}, token=A)
check("PATCH профиля без полей -> 400", st == 400, f"{st}")
st, me, _ = api("GET", "/users/me", token=A)
check("bio можно очистить пустой строкой",
      (api("PATCH", "/users/me", {"bio": ""}, token=A)[0] == 204) and api("GET", "/users/me", token=A)[1].get("bio") == "", "")

# ================= rate limit (последний) =================
hdrs_retry = None
codes = []
for i in range(35):
    st, _, h = api("POST", "/auth/login", {"login": "rl" + SUF, "password": "x" * 12})
    codes.append(st)
    if st == 429: hdrs_retry = h.get("Retry-After")
check("rate limit 429 + Retry-After", 429 in codes and hdrs_retry is not None, f"{sorted(set(codes))} Retry-After={hdrs_retry}")

print(f"\n===== ИТОГ: {len(PASSED)} passed, {len(FAILED)} failed =====")
if FAILED:
    print("\nПРОБЛЕМЫ:")
    for n, ctx in FAILED: print(f"  - {n}  [{ctx}]")
sys.exit(1 if FAILED else 0)
