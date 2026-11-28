using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using MessengerDesktop;

var directory = Path.Combine(Path.GetTempPath(), "messenger-server-identity-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    ServerTrustStore.TestFilePath = Path.Combine(directory, "server-trust.json");
    (Preview.Server, Preview.OutDir) = ("identity-check", directory);
    await CheckCredentials();
    await CheckRunningServers(directory);
    LocalHistoryChecks.Run(directory);
}
finally
{
    Directory.Delete(directory, recursive: true);
}

static async Task CheckCredentials()
{
    const string address = "https://example.invalid/messenger";
    var original = new ApiFixture();
    using var client = new MessengerClient(address, "test-token", original);
    await client.GetMe();
    Expect(original.ProofRequests == 1 && original.Credentials == 1, "First contact did not verify before sending the token");
    Expect(ServerTrustStore.Lookup(address) == client.ServerFingerprint, "Public key was not pinned");

    original.Replay = true;
    await Rejected(() => client.Login("identity", "fixture-password"));
    await Rejected(() => client.GetMe());
    Expect(original.Credentials == 1 && original.ProofRequests == 3, "Stale proof released credentials");

    var wrongKey = new ApiFixture();
    using var impostor = new MessengerClient(address, "test-token", wrongKey);
    await Rejected(() => impostor.GetMe());
    await Rejected(() => impostor.Register("identity", "fixture-password"));
    using var compromisedAddress = new MessengerClient("https://different.invalid/messenger", "test-token", wrongKey,
        expectedFingerprint: client.ServerFingerprint);
    await Rejected(() => compromisedAddress.GetMe());
    Expect(wrongKey.Credentials == 0 && wrongKey.ProofRequests == 3, "Impostor received credentials");

    var missing = new ApiFixture { MissingProof = true };
    using var oldServer = new MessengerClient("https://no-proof.invalid/messenger", "test-token", missing);
    await Rejected(() => oldServer.GetMe());
    Expect(missing.Credentials == 0, "Server without proof received a token");

    await Rejected(() => Task.FromResult(new MessengerClient("http://remote.invalid/messenger")));
    var imported = new SessionStore.Saved { Server = "https://unpinned.invalid/messenger", Token = "test-token" };
    await Rejected(() => Task.FromResult(SessionStore.ConnectSaved(imported, "https://unknown.invalid/messenger")));

    var trustFile = ServerTrustStore.TestFilePath!;
    var savedPins = File.ReadAllBytes(trustFile);
    File.WriteAllText(trustFile, "{broken");
    var corruptStore = new ApiFixture();
    using var untrusted = new MessengerClient("https://corrupt-store.invalid/messenger", "test-token", corruptStore);
    await Rejected(() => untrusted.GetMe());
    Expect(corruptStore.Credentials == 0, "Unreadable trust store released credentials");
    File.WriteAllBytes(trustFile, savedPins);
    Console.WriteLine("PASS proof before credentials; replay, wrong key, missing proof, insecure address and corrupt pins are blocked");
}

static async Task CheckRunningServers(string directory)
{
    var firstPort = FreePort();
    var secondPort = FreePort();
    while (secondPort == firstPort) secondPort = FreePort();
    var encryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    var state = Path.Combine(directory, "state.db");
    var address = Address(firstPort);
    using var originalClient = new MessengerClient(address);
    using var live = new LiveConnection(originalClient);
    var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var identityRejected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    live.State += status => { if (status == "онлайн") connected.TrySetResult(); };
    live.IdentityRejected += warning => identityRejected.TrySetResult(warning);
    live.SessionRevoked += () => identityRejected.TrySetException(new Exception("Impostor received the WebSocket token"));
    Task liveLoop;
    SessionStore.Saved saved;
    RelayChecks.Fixture relay;
    string userId;
    using (var host = await Start(firstPort, state, encryptionKey))
    {
        using var http = new HttpClient();
        using var badNonce = await http.PostAsJsonAsync(address + "/identity/proof", new { nonce = "invalid" });
        Expect(badNonce.StatusCode == HttpStatusCode.BadRequest, "Malformed nonce was signed");
        using var oversized = await http.PostAsJsonAsync(address + "/identity/proof", new { nonce = new string('a', 513) });
        Expect(oversized.StatusCode == HttpStatusCode.RequestEntityTooLarge, "Oversized proof request was accepted");
        await originalClient.Register("identity", "fixture-password");
        await originalClient.Login("identity", "fixture-password");
        userId = (await originalClient.GetMe()).Id;
        SessionStore.Save(originalClient);
        saved = SessionStore.Load() ?? throw new Exception("Session was not saved");
        Expect(saved.ServerFingerprint == originalClient.ServerFingerprint && saved.ServerFingerprint?.Length == 64,
            "Session was not bound to fingerprint");
        Expect(string.IsNullOrEmpty(originalClient.WebSocketUri().Query), "WebSocket URL still carries credentials");
        liveLoop = live.RunAsync();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        relay = await RelayChecks.Run(originalClient, userId, state);
    }

    Expect(File.Exists(state + ".identity.key"), "Backend key was not persisted alongside the database");
    Expect(!File.ReadAllBytes(state + ".identity.key").SequenceEqual(Convert.FromBase64String(encryptionKey)),
        "Backend reused the data-encryption key");

    using (var moved = await Start(secondPort, state, encryptionKey))
    using (var replacement = await Start(firstPort, Path.Combine(directory, "replacement.db"), encryptionKey))
    {
        await identityRejected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await liveLoop.WaitAsync(TimeSpan.FromSeconds(2));
        using var stale = SessionStore.ConnectSaved(saved, address);
        await Rejected(() => stale.GetMe());
        using var socket = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Rejected(() => stale.ConnectAuthenticatedSocket(socket, stale.WebSocketUri(), timeout.Token));
        Expect(socket.State == WebSocketState.None && SessionStore.Load()?.Server == address,
            "Wrong key opened a socket or changed the stored session");

        var newAddress = Address(secondPort);
        using var client = SessionStore.ConnectSaved(saved, newAddress);
        Expect((await client.GetMe()).Id == userId && client.ServerFingerprint == saved.ServerFingerprint,
            "Restart or address change lost server identity or account");
        await RelayChecks.CheckRestart(client, relay, state);
        SessionStore.Save(client);
        var updated = SessionStore.Load();
        Expect(updated?.Server == newAddress && updated.ServerFingerprint == saved.ServerFingerprint,
            "New address not persisted");
        SessionStore.Clear();
        Expect(ServerTrustStore.Lookup(newAddress) == saved.ServerFingerprint,
            "Logout removed the server pin");
    }
    Console.WriteLine("PASS real backend: nonce validation, authenticated WebSocket, replacement blocked on reconnect, restart, address change and durable pins");
}

static string Address(int port) => $"http://127.0.0.1:{port}/messenger";

static int FreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
    finally { listener.Stop(); }
}

static async Task<BackendProcess> Start(int port, string state, string encryptionKey)
{
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Messenger.Api.dll"));
    start.WorkingDirectory = Path.GetDirectoryName(state)!;
    start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
    start.Environment["Db"] = state;
    start.Environment.Remove("MESSENGER_SERVER_IDENTITY_KEY_FILE");
    start.Environment["MESSENGER_PATH_BASE"] = "/messenger";
    start.Environment["MESSENGER_DEV_SEED"] = "false";
    start.Environment["MESSENGER_ANTISPAM"] = "false";
    start.Environment["MESSENGER_KEY"] = encryptionKey;
    start.Environment["Logging__LogLevel__Default"] = "Warning";
    var process = Process.Start(start) ?? throw new Exception("Cannot start test backend");
    var backend = new BackendProcess(process);
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!process.HasExited)
        {
            try
            {
                using var health = await http.GetAsync(Address(port) + "/health", timeout.Token);
                if (health.IsSuccessStatusCode) return backend;
            }
            catch (HttpRequestException) when (!timeout.IsCancellationRequested) { }
            await Task.Delay(80, timeout.Token);
        }
        throw new Exception($"Test backend exited with {process.ExitCode}");
    }
    catch
    {
        backend.Dispose();
        throw;
    }
}

static async Task Rejected(Func<Task> request)
{
    try { await request(); }
    catch (ServerIdentityException) { return; }
    throw new Exception("Expected server identity rejection");
}

static void Expect(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

sealed class ApiFixture : SignedTestServer
{
    internal int Credentials { get; private set; }

    protected override async Task<HttpResponseMessage> SendVerified(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Headers.Authorization?.Parameter == "test-token" ||
            (request.Content != null && (await request.Content.ReadAsStringAsync(ct)).Contains("fixture-password")))
            Credentials++;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { id = "fixture" }) };
    }
}

sealed class BackendProcess(Process process) : IDisposable
{
    public void Dispose()
    {
        if (!process.HasExited) process.Kill();
        process.WaitForExit();
        process.Dispose();
    }
}
