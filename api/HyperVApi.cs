using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace docker_launcher;

public class HyperVApi
{
    public record HyperVVm(string Name, string State, string IPAddress);

    private readonly HttpClient _http;
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

        var handler = new HttpClientHandler();
        if (_wsmanUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }

        _http = new HttpClient(handler);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
        _http.Timeout = TimeSpan.FromSeconds(15);

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
        return $"{scheme}://{hostname}:{port}/wsman";
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

        string shellId = null;
        try
        {
            shellId = await CreateShell();
            _logger.LogDebug("WinRM shell created: {ShellId}", shellId);

            var commandId = await ExecuteCommand(shellId, encodedCommand);
            _logger.LogDebug("WinRM command started: {CommandId} in shell {ShellId}", commandId, shellId);

            try
            {
                var (stdout, stderr) = await ReceiveOutput(shellId, commandId);
                await SignalTerminate(shellId, commandId);

                if (!string.IsNullOrWhiteSpace(stderr))
                    _logger.LogWarning("Hyper-V PowerShell stderr: {Stderr}", stderr);

                _logger.LogDebug("Hyper-V PowerShell stdout ({Length} chars): {Output}",
                    stdout.Length, stdout.Length > 500 ? stdout[..500] + "..." : stdout);

                return ParseVmJson(stdout);
            }
            finally
            {
                try { await SignalTerminate(shellId, commandId); }
                catch (Exception ex) { _logger.LogDebug(ex, "WinRM signal terminate failed (may already be done)"); }
            }
        }
        finally
        {
            if (shellId != null)
            {
                try { await DeleteShell(shellId); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete WinRM shell {ShellId}, it may leak on the host", shellId); }
            }
        }
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

    // WinRM SOAP helpers

    private static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly XNamespace Wsman = "http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd";
    private static readonly XNamespace Wsa = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
    private static readonly XNamespace Rsp = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell";

    private async Task<string> PostSoap(string body, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        var content = new StringContent(body, Encoding.UTF8, "application/soap+xml");
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync(_wsmanUrl, content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WinRM connection failed during {Step} to {Url}", caller, _wsmanUrl);
            throw;
        }

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync();
            _logger.LogError("WinRM {Step} returned HTTP {StatusCode}: {Body}",
                caller, (int)response.StatusCode,
                responseBody.Length > 1000 ? responseBody[..1000] + "..." : responseBody);
            response.EnsureSuccessStatusCode(); // throw
        }

        return await response.Content.ReadAsStringAsync();
    }

    private async Task<string> CreateShell()
    {
        var soap = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:wsa=""http://schemas.xmlsoap.org/ws/2004/08/addressing""
            xmlns:wsman=""http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd""
            xmlns:rsp=""http://schemas.microsoft.com/wbem/wsman/1/windows/shell"">
  <s:Header>
    <wsa:To>{_wsmanUrl}</wsa:To>
    <wsman:ResourceURI s:mustUnderstand=""true"">http://schemas.microsoft.com/wbem/wsman/1/windows/shell/cmd</wsman:ResourceURI>
    <wsa:ReplyTo>
      <wsa:Address s:mustUnderstand=""true"">http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</wsa:Address>
    </wsa:ReplyTo>
    <wsa:Action s:mustUnderstand=""true"">http://schemas.xmlsoap.org/ws/2004/09/transfer/Create</wsa:Action>
    <wsman:MaxEnvelopeSize s:mustUnderstand=""true"">153600</wsman:MaxEnvelopeSize>
    <wsman:OperationTimeout>PT60S</wsman:OperationTimeout>
  </s:Header>
  <s:Body>
    <rsp:Shell>
      <rsp:OutputStreams>stdout stderr</rsp:OutputStreams>
    </rsp:Shell>
  </s:Body>
</s:Envelope>";

        var xml = XDocument.Parse(await PostSoap(soap));
        var shellId = xml.Descendants(Rsp + "ShellId").FirstOrDefault()?.Value
            ?? xml.Descendants(Rsp + "Shell").Attributes("ShellId").FirstOrDefault()?.Value;

        if (string.IsNullOrEmpty(shellId))
            throw new InvalidOperationException("Failed to create WinRM shell: no ShellId in response");

        return shellId;
    }

    private async Task<string> ExecuteCommand(string shellId, string encodedCommand)
    {
        var soap = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:wsa=""http://schemas.xmlsoap.org/ws/2004/08/addressing""
            xmlns:wsman=""http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd""
            xmlns:rsp=""http://schemas.microsoft.com/wbem/wsman/1/windows/shell"">
  <s:Header>
    <wsa:To>{_wsmanUrl}</wsa:To>
    <wsman:ResourceURI s:mustUnderstand=""true"">http://schemas.microsoft.com/wbem/wsman/1/windows/shell/cmd</wsman:ResourceURI>
    <wsa:ReplyTo>
      <wsa:Address s:mustUnderstand=""true"">http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</wsa:Address>
    </wsa:ReplyTo>
    <wsa:Action s:mustUnderstand=""true"">http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Command</wsa:Action>
    <wsman:MaxEnvelopeSize s:mustUnderstand=""true"">153600</wsman:MaxEnvelopeSize>
    <wsman:OperationTimeout>PT60S</wsman:OperationTimeout>
    <wsman:SelectorSet>
      <wsman:Selector Name=""ShellId"">{shellId}</wsman:Selector>
    </wsman:SelectorSet>
  </s:Header>
  <s:Body>
    <rsp:CommandLine>
      <rsp:Command>powershell.exe</rsp:Command>
      <rsp:Arguments>-EncodedCommand {encodedCommand}</rsp:Arguments>
    </rsp:CommandLine>
  </s:Body>
</s:Envelope>";

        var xml = XDocument.Parse(await PostSoap(soap));
        var commandId = xml.Descendants(Rsp + "CommandId").FirstOrDefault()?.Value;

        if (string.IsNullOrEmpty(commandId))
            throw new InvalidOperationException("Failed to execute WinRM command: no CommandId in response");

        return commandId;
    }

    private async Task<(string stdout, string stderr)> ReceiveOutput(string shellId, string commandId)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var receiveCount = 0;

        while (true)
        {
            receiveCount++;
            var soap = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:wsa=""http://schemas.xmlsoap.org/ws/2004/08/addressing""
            xmlns:wsman=""http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd""
            xmlns:rsp=""http://schemas.microsoft.com/wbem/wsman/1/windows/shell"">
  <s:Header>
    <wsa:To>{_wsmanUrl}</wsa:To>
    <wsman:ResourceURI s:mustUnderstand=""true"">http://schemas.microsoft.com/wbem/wsman/1/windows/shell/cmd</wsman:ResourceURI>
    <wsa:ReplyTo>
      <wsa:Address s:mustUnderstand=""true"">http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</wsa:Address>
    </wsa:ReplyTo>
    <wsa:Action s:mustUnderstand=""true"">http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Receive</wsa:Action>
    <wsman:MaxEnvelopeSize s:mustUnderstand=""true"">153600</wsman:MaxEnvelopeSize>
    <wsman:OperationTimeout>PT60S</wsman:OperationTimeout>
    <wsman:SelectorSet>
      <wsman:Selector Name=""ShellId"">{shellId}</wsman:Selector>
    </wsman:SelectorSet>
  </s:Header>
  <s:Body>
    <rsp:Receive>
      <rsp:DesiredStream CommandId=""{commandId}"">stdout stderr</rsp:DesiredStream>
    </rsp:Receive>
  </s:Body>
</s:Envelope>";

            var xml = XDocument.Parse(await PostSoap(soap));

            foreach (var stream in xml.Descendants(Rsp + "Stream"))
            {
                var streamName = stream.Attribute("Name")?.Value;
                if (string.IsNullOrEmpty(stream.Value))
                    continue;

                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(stream.Value));
                if (streamName == "stdout")
                    stdout.Append(decoded);
                else if (streamName == "stderr")
                    stderr.Append(decoded);
            }

            var state = xml.Descendants(Rsp + "CommandState").FirstOrDefault();
            var stateAttr = state?.Attribute("State")?.Value ?? "";
            if (stateAttr.Contains("Done"))
                break;
        }

        _logger.LogDebug("WinRM receive completed after {Count} round-trip(s)", receiveCount);
        return (stdout.ToString(), stderr.ToString());
    }

    private async Task SignalTerminate(string shellId, string commandId)
    {
        var soap = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:wsa=""http://schemas.xmlsoap.org/ws/2004/08/addressing""
            xmlns:wsman=""http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd""
            xmlns:rsp=""http://schemas.microsoft.com/wbem/wsman/1/windows/shell"">
  <s:Header>
    <wsa:To>{_wsmanUrl}</wsa:To>
    <wsman:ResourceURI s:mustUnderstand=""true"">http://schemas.microsoft.com/wbem/wsman/1/windows/shell/cmd</wsman:ResourceURI>
    <wsa:ReplyTo>
      <wsa:Address s:mustUnderstand=""true"">http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</wsa:Address>
    </wsa:ReplyTo>
    <wsa:Action s:mustUnderstand=""true"">http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Signal</wsa:Action>
    <wsman:MaxEnvelopeSize s:mustUnderstand=""true"">153600</wsman:MaxEnvelopeSize>
    <wsman:OperationTimeout>PT60S</wsman:OperationTimeout>
    <wsman:SelectorSet>
      <wsman:Selector Name=""ShellId"">{shellId}</wsman:Selector>
    </wsman:SelectorSet>
  </s:Header>
  <s:Body>
    <rsp:Signal CommandId=""{commandId}"">
      <rsp:Code>http://schemas.microsoft.com/wbem/wsman/1/windows/shell/signal/terminate</rsp:Code>
    </rsp:Signal>
  </s:Body>
</s:Envelope>";

        await PostSoap(soap);
    }

    private async Task DeleteShell(string shellId)
    {
        var soap = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:wsa=""http://schemas.xmlsoap.org/ws/2004/08/addressing""
            xmlns:wsman=""http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd""
            xmlns:rsp=""http://schemas.microsoft.com/wbem/wsman/1/windows/shell"">
  <s:Header>
    <wsa:To>{_wsmanUrl}</wsa:To>
    <wsman:ResourceURI s:mustUnderstand=""true"">http://schemas.microsoft.com/wbem/wsman/1/windows/shell/cmd</wsman:ResourceURI>
    <wsa:ReplyTo>
      <wsa:Address s:mustUnderstand=""true"">http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</wsa:Address>
    </wsa:ReplyTo>
    <wsa:Action s:mustUnderstand=""true"">http://schemas.xmlsoap.org/ws/2004/09/transfer/Delete</wsa:Action>
    <wsman:MaxEnvelopeSize s:mustUnderstand=""true"">153600</wsman:MaxEnvelopeSize>
    <wsman:OperationTimeout>PT60S</wsman:OperationTimeout>
    <wsman:SelectorSet>
      <wsman:Selector Name=""ShellId"">{shellId}</wsman:Selector>
    </wsman:SelectorSet>
  </s:Header>
  <s:Body/>
</s:Envelope>";

        await PostSoap(soap);
    }
}
