using WasmMvcRuntime.Abstractions;

namespace MagicMirror.Native.Services;

/// <summary>
/// Lightweight IPlatformInterop for native rendering — no DOM, no WebView.
/// </summary>
internal sealed class NativePlatformInterop : IPlatformInterop
{
    private readonly Dictionary<string, string> _storage = new();
    private string _currentPath = "/";

    public void SetInnerHTML(string selector, string html) { }
    public void SetInnerText(string selector, string content) { }
    public string? GetInnerText(string selector) => null;
    public void SetAttribute(string selector, string attribute, string value) { }
    public void AddClass(string selector, string className) { }
    public void RemoveClass(string selector, string className) { }
    public void Show(string selector) { }
    public void Hide(string selector) { }
    public void StreamStart(string selector) { }
    public void StreamAppend(string selector, string html) { }
    public void StreamEnd(string selector) { }

    public string? StorageGetItem(string key) => _storage.GetValueOrDefault(key);
    public void StorageSetItem(string key, string value) => _storage[key] = value;
    public void StorageRemoveItem(string key) => _storage.Remove(key);

    public void ConsoleLog(string message) => System.Diagnostics.Debug.WriteLine($"[Cepha] {message}");
    public void ConsoleWarn(string message) => System.Diagnostics.Debug.WriteLine($"[Cepha⚠] {message}");
    public void ConsoleError(string message) => System.Diagnostics.Debug.WriteLine($"[Cepha❌] {message}");

    public string GetCurrentPath() => _currentPath;
    public string GetFingerprint() => $"MAUI/{DeviceInfo.Platform}/{DeviceInfo.Model}";
    public void PushState(string path) => _currentPath = path;
    public Task NavigateTo(string path) { _currentPath = path; return Task.CompletedTask; }

    public void DownloadFile(string filename, string base64Content, string contentType)
    {
        try
        {
            var path = Path.Combine(FileSystem.AppDataDirectory, filename);
            File.WriteAllBytes(path, Convert.FromBase64String(base64Content));
        }
        catch (Exception ex) { ConsoleError($"DownloadFile failed: {ex.Message}"); }
    }
    public void DispatchHubEvent(string hubName, string method, string? connectionId, string argsJson) { }
    public void StartCephaKit(int port) { }

    // ── CephaProcessBridge — native process execution (CephaProcessBridge parity) ──
    // Only processes started here are tracked/killed; we never touch processes we did not spawn.
    private readonly Dictionary<int, System.Diagnostics.Process> _processes = new();

    public void StartProcess(int processId, string fileName, string arguments, string environmentJson, string workingDirectory)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments ?? string.Empty,
                WorkingDirectory = string.IsNullOrEmpty(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (!string.IsNullOrEmpty(environmentJson))
            {
                try
                {
                    var env = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(environmentJson);
                    if (env != null)
                        foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
                }
                catch { /* ignore malformed env */ }
            }
            var proc = System.Diagnostics.Process.Start(psi);
            if (proc != null) _processes[processId] = proc;
        }
        catch (Exception ex) { ConsoleError($"StartProcess failed: {ex.Message}"); }
    }

    public void KillProcess(int processId)
    {
        if (_processes.TryGetValue(processId, out var p))
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            _processes.Remove(processId);
        }
    }

    public void WriteProcessStdin(int processId, string data)
    {
        if (_processes.TryGetValue(processId, out var p))
        {
            try { p.StandardInput.WriteLine(data); p.StandardInput.Flush(); } catch { }
        }
    }

    public bool IsProcessRunning(int processId)
        => _processes.TryGetValue(processId, out var p) && !p.HasExited;

    public void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { ConsoleError($"OpenUrl failed: {ex.Message}"); }
    }

    private string PersistPath(string name) => Path.Combine(FileSystem.AppDataDirectory, name);
    public Task OpfsWrite(string path, string data) { File.WriteAllText(PersistPath(path), data); return Task.CompletedTask; }
    public Task<string?> OpfsRead(string path) { var f = PersistPath(path); return Task.FromResult(File.Exists(f) ? File.ReadAllText(f) : (string?)null); }
    public Task<string?> RestoreDbFromOPFS() { var f = PersistPath("identity.db"); return Task.FromResult(File.Exists(f) ? Convert.ToBase64String(File.ReadAllBytes(f)) : (string?)null); }
    public Task PersistDbToOPFS(string base64Data) { File.WriteAllBytes(PersistPath("identity.db"), Convert.FromBase64String(base64Data)); return Task.CompletedTask; }
    public void BroadcastAuthChange(string action) { }
}