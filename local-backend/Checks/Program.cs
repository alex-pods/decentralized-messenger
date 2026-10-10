using LocalBackend.Checks;

var local = await GroupSelfCheck.RunAsync();
if (local != 0) return local;
return args is ["--server", var server] ? await SyncSelfCheck.RunAsync(server) : 0;
