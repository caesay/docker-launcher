using System.Net;
using System.Text;
using System.Text.Json;
using WinRMSharp;

namespace docker_launcher;

public class HyperVApi
{
    public record HyperVVm(string Name, string State, string IPAddress);

    private readonly WinRMClient _client;
    private readonly string _wsmanUrl;
    private readonly ILogger _logger;

    private HyperVVm[] _cache = Array.Empty<HyperVVm>();
    private DateTime _cacheExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private static readonly Dictionary<int, string> StateMap = new()
    {
        [1] = "Other",
        [2] = "Running",
        [3] = "Off",
        [4] = "Stopping",
        [5] = "Saved",
        [6] = "Paused",
        [7] = "Starting",
        [8] = "Reset",
        [9] = "Saving",
        [10] = "Starting",
        [11] = "OffCritical",
        [32768] = "Paused",
        [32769] = "Suspended",
    };

    public HyperVApi(string host, string username, string password, ILogger logger)
    {
        _logger = logger;
        _wsmanUrl = ParseHostUrl(host);

        var uri = new Uri(_wsmanUrl);
        var credentialCache = new CredentialCache();
        credentialCache.Add(uri, "Basic", new NetworkCredential(username, password));
        var options = new WinRMClientOptions
        {
            ReadTimeout = TimeSpan.FromSeconds(15),
            OperationTimeout = TimeSpan.FromSeconds(60),
        };
        _client = new WinRMClient(uri, credentialCache, options);

        _logger.LogInformation("Hyper-V integration initialized for {Url}", _wsmanUrl);
    }

    private static string ParseHostUrl(string host)
    {
        if (host.Contains("://"))
            return host.TrimEnd('/');

        var parts = host.Split(':', 2);
        var hostname = parts[0];
        var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 5985;
        var scheme = port == 5986 ? "https" : "http";
        return $"{scheme}://{hostname}:{port}";
    }

    public async Task<HyperVVm[]> GetAllVMs()
    {
        if (DateTime.UtcNow < _cacheExpiry)
            return _cache;

        await _semaphore.WaitAsync();
        try
        {
            // double-check after acquiring lock
            if (DateTime.UtcNow < _cacheExpiry)
                return _cache;

            try
            {
                _logger.LogDebug("Refreshing Hyper-V VM cache from {Url}", _wsmanUrl);
                var vms = await FetchVMs();
                _cache = vms;
                _logger.LogDebug("Hyper-V cache refreshed: {Count} VM(s) returned", vms.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch Hyper-V VMs from {Url}, returning stale cache ({Count} VM(s))",
                    _wsmanUrl, _cache.Length);
            }

            _cacheExpiry = DateTime.UtcNow + CacheTtl;
            return _cache;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<HyperVVm[]> FetchVMs()
    {
        var ps = "Get-VM | Select-Object Name, State, @{N='IPAddress'; E={($_.NetworkAdapters.IPAddresses -join ', ')}} | ConvertTo-Json";
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(ps));

        _logger.LogDebug("Executing PowerShell command via WinRM");

        var result = await _client.RunCommand("powershell.exe", ["-EncodedCommand", encodedCommand]);

        _logger.LogDebug("WinRM command completed with exit code {ExitCode}", result.StatusCode);

        if (!string.IsNullOrWhiteSpace(result.Stderr))
            _logger.LogWarning("Hyper-V PowerShell stderr: {Stderr}", result.Stderr);

        _logger.LogDebug("Hyper-V PowerShell stdout ({Length} chars): {Output}",
            result.Stdout.Length, result.Stdout.Length > 500 ? result.Stdout[..500] + "..." : result.Stdout);

        if (result.StatusCode != 0)
            _logger.LogWarning("Hyper-V PowerShell exited with non-zero status {ExitCode}", result.StatusCode);

        return ParseVmJson(result.Stdout);
    }

    private HyperVVm[] ParseVmJson(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            _logger.LogDebug("Hyper-V returned empty output, interpreting as zero VMs");
            return Array.Empty<HyperVVm>();
        }

        output = output.Trim();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(output.StartsWith("[") ? output : $"[{output}]");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse Hyper-V JSON output: {Output}",
                output.Length > 1000 ? output[..1000] + "..." : output);
            throw;
        }

        using (doc)
        {
            var vms = new List<HyperVVm>();

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var name = el.GetProperty("Name").GetString() ?? "";
                var stateRaw = el.GetProperty("State");
                string state;
                if (stateRaw.ValueKind == JsonValueKind.Number)
                {
                    var stateInt = stateRaw.GetInt32();
                    state = StateMap.TryGetValue(stateInt, out var mapped) ? mapped : $"Unknown({stateInt})";
                    if (!StateMap.ContainsKey(stateInt))
                        _logger.LogWarning("VM {Name} has unmapped state integer: {StateInt}", name, stateInt);
                }
                else
                {
                    state = stateRaw.GetString() ?? "Unknown";
                }

                var ip = "";
                if (el.TryGetProperty("IPAddress", out var ipProp) && ipProp.ValueKind == JsonValueKind.String)
                    ip = ipProp.GetString() ?? "";

                _logger.LogDebug("Parsed VM: {Name} State={State} IP={IP}", name, state, ip);
                vms.Add(new HyperVVm(name, state, ip));
            }

            return vms.ToArray();
        }
    }
}
