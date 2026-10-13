"""
Нагрузочный тест мессенджера (воспроизводимый). Только stdlib.

Конфигурация в CONFIG. Сценарий: setup (пользователи/чаты) -> параллельная отправка
сообщений с client_ref + чтение истории + правки/реакции + WS-приёмники.
Метрики: латентности p50/p95/p99 по операциям, ошибок, пропускная способность,
задержка доставки WS, потребление сервера (RSS/CPU сэмплер через ssh).

Запуск: python loadtest.py http://176.53.160.4:8091/messenger [ssh_host_alias]
"""
import json, socket, base64, os, struct, sys, threading, time, urllib.request, urllib.error, urllib.parse, uuid, statistics
from datetime import datetime, timezone

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:5080/messenger"
WSHOST = urllib.parse.urlparse(BASE).hostname
WSPORT = urllib.parse.urlparse(BASE).port or 80
SSH_ALIAS = sys.argv[2] if len(sys.argv) > 2 else None

CONFIG = {
    "users": 10,             # параллельные отправители (и по паре в ЛС)
    "messages_per_user": 60, # сообщений на пользователя
    "history_every": 5,      # чтение истории каждые N сообщений
    "ws_receivers": 8,       # WS-приёмники, подписанные на чаты
    "msg_len": 200,          # длина текста сообщения
}

LAT = {}
ERRS = []
LOCK = threading.Lock()
def stat(name, dt, ok=True):
    with LOCK:
        LAT.setdefault(name, []).append(dt)
        if not ok: ERRS.append(name)

import http.client
TLS = threading.local()
def _conn():
    if not hasattr(TLS, "conn") or TLS.conn is None:
        TLS.conn = http.client.HTTPConnection(WSHOST, WSPORT, timeout=20)
    return TLS.conn
def api(method, path, body=None, token=None, timeout=15):
    req = urllib.request.Request(BASE + path, method=method)
    if token: req.add_header("Authorization", "Bearer " + token)
    payload = json.dumps(body).encode() if body is not None else None
    t0 = time.perf_counter()
    try:
        c = _conn()
        headers = {"Content-Type": "application/json"}
        if token: headers["Authorization"] = "Bearer " + token
        c.request(method, path, body=payload, headers=headers)
        r = c.getresponse()
        raw = r.read()
        return r.status, json.loads(raw) if raw else None
    except Exception:
        try: TLS.conn = None
        except Exception: pass
        return 0, None
    finally:
        stat(path.split("?")[0][:34] + f" [{method}]", time.perf_counter() - t0)

def pct(xs, p):
    xs = sorted(xs)
    return xs[min(len(xs) - 1, int(len(xs) * p))] if xs else 0

# ---------- WS-приёмники ----------
SOCKBUF = {}
def ws_connect(path):
    s = socket.create_connection((WSHOST, WSPORT), timeout=10)
    key = base64.b64encode(os.urandom(16)).decode()
    s.sendall((f"GET {path} HTTP/1.1\r\nHost: {WSHOST}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
               f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n").encode())
    resp = b""
    while b"\r\n\r\n" not in resp: resp += s.recv(4096)
    SOCKBUF[s] = resp.partition(b"\r\n\r\n")[2]
    return s
def ws_send(s, obj):
    data = json.dumps(obj).encode(); mask = os.urandom(4)
    h = bytearray([0x81]); n = len(data)
    if n < 126: h.append(0x80 | n)
    elif n < 65536: h.append(0x80 | 126); h += struct.pack(">H", n)
    else: h.append(0x80 | 127); h += struct.pack(">Q", n)
    h += mask
    s.sendall(bytes(h) + bytes(b ^ mask[i % 4] for i, b in enumerate(data)))
def ws_recv(s, timeout):
    s.settimeout(timeout)
    def rd(n):
        out = b""
        b = SOCKBUF.pop(s, b"")
        if b: out += b[:n]; n -= len(out); SOCKBUF[s] = b[len(out):] if len(b) > len(out) else b""
        while n > 0:
            c = s.recv(n)
            if not c: raise ConnectionError("closed")
            out += c; n -= len(c)
        return out
    while True:
        b1, b2 = rd(2); op, ln = b1 & 0x0F, b2 & 0x7F
        if ln == 126: ln = struct.unpack(">H", rd(2))[0]
        elif ln == 127: ln = struct.unpack(">Q", rd(8))[0]
        p = rd(ln)
        if op in (0x8, 0x9, 0xA): continue
        return json.loads(p.decode())

WS_GOT = []          # (client_post_epoch, recv_epoch) для latency доставки (оба времени клиентские)
POST_TIMES = {}     # (sender_id, text) -> epoch отправки POST
WS_COUNT = [0]
WS_STOP = threading.Event()
def ws_receiver(token, chat_id):
    try:
        s = ws_connect(f"/messenger/ws?user_session_token={token}")
        ws_recv(s, 5)                      # connected
        ws_send(s, {"action": "follow", "chat_id": chat_id})
        ws_recv(s, 5)                      # followed
        s.settimeout(0.5)
        while not WS_STOP.is_set():
            try:
                ev = ws_recv(s, 0.5)
                if ev.get("type") == "message":
                    m = ev.get("message") or {}
                    WS_COUNT[0] += 1
                    key = (m.get("sender_id"), m.get("text"))
                    with LOCK:
                        t_post = POST_TIMES.pop(key, None)
                    if t_post is not None:
                        WS_GOT.append((t_post, time.time()))
            except socket.timeout:
                continue
            except Exception:
                break
    except Exception as e:
        with LOCK: ERRS.append(f"ws_receiver: {type(e).__name__}")

# ---------- сервер-сэмплер ----------
def cpu_rss_pid(pid):
    with open(f"/proc/{pid}/stat") as f: parts = f.read().split()
    utime = (int(parts[13]) + int(parts[14])) / os.sysconf("SC_CLK_TCK")
    with open(f"/proc/{pid}/status") as f:
        rss = next(int(l.split()[1]) for l in f if l.startswith("VmRSS"))
    return utime, rss

def sampler_ssh(alias, pid, stop, out):
    import subprocess
    while not stop.is_set():
        try:
            r = subprocess.run(["ssh", alias, f"cat /proc/{pid}/stat /proc/{pid}/status"],
                               capture_output=True, text=True, timeout=10)
            stat, status = r.stdout.split("\n", 1) if r.stdout else ("", "")
            parts = stat.split()
            utime = (int(parts[13]) + int(parts[14])) / 100
            rss = next((int(l.split()[1]) for l in status.splitlines() if l.startswith("VmRSS")), 0)
            out.append((time.time(), utime, rss))
        except Exception:
            pass
        time.sleep(2)

print(f"===== LOAD TEST vs {BASE} | config={CONFIG} =====")
t_setup0 = time.perf_counter()

# setup: пользователи (лимит /auth — 30/мин, поэтому <= 12 юзеров за прогон)
SUF = "lt" + str(int(time.time()))[-6:]
tokens, ids = [], []
for i in range(CONFIG["users"]):
    tag = f"{SUF}u{i}"
    st, _ = api("POST", "/auth/register", {"login": tag, "password": "password1"})
    assert st == 201, f"register {tag}: {st}"
    st, b = api("POST", "/auth/login", {"login": tag, "password": "password1"})
    assert st == 200, f"login {tag}: {st}"
    tokens.append(b["user_session_token"])
    ids.append(api("GET", "/users/me", token=tokens[-1])[1]["id"])

# чаты: user[2k] <-> user[2k+1] личные + одна общая группа
chats = []
for k in range(0, CONFIG["users"] - 1, 2):
    st, b = api("POST", "/chats", {"another_user_id": ids[k + 1]}, token=tokens[k])
    assert st in (200, 201)
    chats.append(b["chat_id"])
st, b = api("POST", "/chats/group", {"title": "LT group", "member_ids": ids[1:]}, token=tokens[0])
group = b["chat_id"]
print(f"setup: {CONFIG['users']} юзеров, {len(chats)} ЛС + группа за {time.perf_counter()-t_setup0:.1f}s")

# WS-приёмники: по одному на каждый ЛС-чат (токен — участник), + пара на группу
ws_threads = []
for i, chat in enumerate(chats[:CONFIG["ws_receivers"]]):
    t = threading.Thread(target=ws_receiver, args=(tokens[(i * 2) % len(tokens)], chat), daemon=True)
    t.start(); ws_threads.append(t)
time.sleep(1.5)

# сэмплер ресурсов сервера
samples = []
sampler_stop = threading.Event()
sampler = None
if SSH_ALIAS:
    import subprocess
    pid = subprocess.run(["ssh", SSH_ALIAS, "systemctl show messenger-load -p MainPID --value"],
                         capture_output=True, text=True).stdout.strip()
    print(f"server pid={pid}")
    sampler = threading.Thread(target=sampler_ssh, args=(SSH_ALIAS, pid, sampler_stop, samples), daemon=True)
    sampler.start()

MSG = "нагрузочное сообщение " * (CONFIG["msg_len"] // 20)
total_msgs = [0]
STATUS_TALLY = {}
def worker(i, chat_id):
    token = tokens[i]
    partner = tokens[i + 1] if i + 1 < len(tokens) else tokens[0]
    sent_ids = []
    for n in range(CONFIG["messages_per_user"]):
        text = f"{MSG}#{n}"
        with LOCK:
            POST_TIMES[(ids[i], text)] = time.time()
        st, b = api("POST", f"/chats/{chat_id}/messages",
                    {"text": text, "client_ref": f"lt-{SUF}-{i}-{n}"}, token=token)
        with LOCK: STATUS_TALLY[st] = STATUS_TALLY.get(st, 0) + 1
        if st == 201:
            sent_ids.append(b["message_id"])
            with LOCK: total_msgs[0] += 1
        if n % CONFIG["history_every"] == 0:
            api("GET", f"/chats/{chat_id}/messages?limit=20", token=partner)
    # правки и реакции на свои сообщения
    for mid in sent_ids[:5]:
        api("PATCH", f"/messages/{mid}", {"text": f"edited {mid}"}, token=token)
    for mid in sent_ids[5:10]:
        api("POST", f"/messages/{mid}/reactions", {"emoji": "🔥"}, token=partner)

t0 = time.perf_counter()
ths = [threading.Thread(target=worker, args=(i, chats[i // 2])) for i in range(CONFIG["users"])]
for t in ths: t.start()
for t in ths: t.join()
duration = time.perf_counter() - t0

# дадим WS-очередям доразгрузиться
time.sleep(2)
WS_STOP.set()
sampler_stop.set()
time.sleep(1)

# ---------- отчёт ----------
print("statuses:", dict(sorted(STATUS_TALLY.items())))
print(f"\nduration: {duration:.1f}s, messages sent: {total_msgs[0]}, "
      f"throughput: {total_msgs[0]/duration:.1f} msg/s, errors: {len(ERRS)}")
if ERRS: print("error samples:", ERRS[:10])
print(f"\n{'операция':44} {'n':>5} {'p50':>7} {'p95':>7} {'p99':>7} {'max':>7}  (ms)")
for name in sorted(LAT):
    xs = [x * 1000 for x in LAT[name]]
    print(f"{name:44} {len(xs):5} {pct(xs,0.5):7.0f} {pct(xs,0.95):7.0f} {pct(xs,0.99):7.0f} {max(xs):7.0f}")

if WS_COUNT[0]:
    pairs = WS_GOT
    if pairs:
        dl = sorted((r - s) * 1000 for s, r in pairs)
        print(f"\nWS: получено {WS_COUNT[0]} событий; доставка p50={pct(dl,0.5):.0f}ms "
              f"p95={pct(dl,0.95):.0f}ms p99={pct(dl,0.99):.0f}ms max={max(dl):.0f}ms")
    else:
        print(f"\nWS: получено {WS_COUNT[0]} событий (sent_epoch нет в модели — латенси n/a)")
else:
    print("\nWS: событий не получено")

if samples:
    rss = [s[2] for s in samples] or [0]
    cpu0, cpu1 = samples[0][1], samples[-1][1]
    wall = samples[-1][0] - samples[0][0] or 1
    print(f"\nserver: RSS min/peak = {min(rss)/1024:.0f}/{max(rss)/1024:.0f} MiB; "
          f"CPU avg = {max(0.0,(cpu1-cpu0)/wall)*100:.0f}% (1 ядро = 100%)")
print("\nLOAD TEST DONE")
