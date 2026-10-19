using System.Globalization;
using System.Text.Json;

namespace PadShift.Cli;

/// <summary>
/// Executes one parsed command against the local PadShift HTTP API
/// (<c>http://127.0.0.1:{port}/v2.6/...</c>), trying ports 35474, 35475 and 35476 in order
/// unless <c>--port</c> pins one. Exit codes: 0 = HTTP 2xx, 1 = request/connection failure,
/// 2 = bad usage.
/// </summary>
public sealed class CliRunner : IDisposable
{
    public const int ExitOk = 0;
    public const int ExitFailure = 1;
    public const int ExitUsage = 2;

    /// <summary>the reference HTTP port fallback list (docs/03-engine.md §1.2).</summary>
    public static readonly int[] DefaultPorts = [35474, 35475, 35476];

    private readonly HttpClient _http;
    private readonly TextWriter _output;
    private readonly TextWriter _error;

    public CliRunner(HttpMessageHandler handler, TextWriter output, TextWriter error, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        // The caller owns the handler (tests reuse / inspect it).
        _http = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(5),
        };
        _output = output;
        _error = error;
    }

    public int Run(IReadOnlyList<string> args) => RunAsync(args).GetAwaiter().GetResult();

    public async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        CliParseResult parsed = CliParser.Parse(args);
        if (!parsed.Success || parsed.Command is null)
        {
            _error.WriteLine($"参数错误：{parsed.Error}");
            _error.WriteLine();
            _error.Write(HelpText.Text);
            return ExitUsage;
        }

        CliCommand command = parsed.Command;
        return command.Verb switch
        {
            CliVerb.Help => PrintHelp(),
            CliVerb.Version => await SendAsync(
                    command,
                    HttpMethod.Get,
                    "/v2.6/Version/GetVersion",
                    body: null)
                .ConfigureAwait(false),
            CliVerb.Apply => await SendAsync(
                    command,
                    HttpMethod.Post,
                    "/v2.6/ControllerService/ApplyConfig",
                    new
                    {
                        ControllerGuid = command.Controller,
                        Path = command.ConfigPath,
                        Slot = SlotName(command.Slot ?? CliParser.DefaultSlot),
                    })
                .ConfigureAwait(false),
            CliVerb.SelectSlot => await SelectSlotAsync(command).ConfigureAwait(false),
            CliVerb.ClearSlot => await SendAsync(
                    command,
                    HttpMethod.Post,
                    "/v2.6/ControllerService/ClearSlot",
                    new
                    {
                        Guid = command.Controller,
                        Slots = new[] { SlotName(command.Slot ?? CliParser.DefaultSlot) },
                    })
                .ConfigureAwait(false),
            CliVerb.Remap when command.RemapOn => await SendAsync(
                    command,
                    HttpMethod.Post,
                    "/v2.6/ControllerService/EnableRemap",
                    new
                    {
                        Guid = command.Controller,
                        RemapNoToggled = false,
                    })
                .ConfigureAwait(false),
            CliVerb.Remap => await SendAsync(
                    command,
                    HttpMethod.Post,
                    "/v2.6/ControllerService/DisableRemap",
                    new
                    {
                        Guid = command.Controller,
                    })
                .ConfigureAwait(false),
            _ => ExitFailure,
        };
    }

    public void Dispose() => _http.Dispose();

    // --------------------------------------------------------------- internals

    private int PrintHelp()
    {
        _output.Write(HelpText.Text);
        return ExitOk;
    }

    /// <summary>
    /// <c>select_slot</c> needs a concrete controller GUID (the PadShift route rejects a null
    /// Guid), so when <c>--controller</c> is omitted we resolve the connected controllers first
    /// and switch the slot on each of them.
    /// </summary>
    private async Task<int> SelectSlotAsync(CliCommand command)
    {
        string slot = SlotName(command.Slot ?? CliParser.DefaultSlot);

        if (command.Controller is { } controller)
        {
            return await SendAsync(
                    command,
                    HttpMethod.Post,
                    "/v2.6/ControllerService/SelectSlot",
                    new { Guid = controller, Slot = slot })
                .ConfigureAwait(false);
        }

        (int listExit, List<Guid> controllers) = await GetControllerIdsAsync(command).ConfigureAwait(false);
        if (listExit != ExitOk)
        {
            return listExit;
        }

        if (controllers.Count == 0)
        {
            _error.WriteLine("没有已连接的控制器；请先用 --controller <GUID> 指定目标。");
            return ExitFailure;
        }

        foreach (Guid controllerId in controllers)
        {
            int exit = await SendAsync(
                    command,
                    HttpMethod.Post,
                    "/v2.6/ControllerService/SelectSlot",
                    new { Guid = controllerId, Slot = slot })
                .ConfigureAwait(false);

            if (exit != ExitOk)
            {
                return exit;
            }
        }

        return ExitOk;
    }

    /// <summary>GET <c>/v2.6/ControllerService/Controllers</c> and extract the nested device GUIDs.</summary>
    private async Task<(int Exit, List<Guid> Controllers)> GetControllerIdsAsync(CliCommand command)
    {
        int[] ports = command.Port is { } pinned ? [pinned] : DefaultPorts;
        Exception? lastFailure = null;

        foreach (int port in ports)
        {
            string url = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/v2.6/ControllerService/Controllers";
            try
            {
                using HttpResponseMessage response = await _http.GetAsync(url).ConfigureAwait(false);
                string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    WriteResponse(text, response);
                    _error.WriteLine(
                        $"请求失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}（GET {url}）");
                    return (ExitFailure, new List<Guid>());
                }

                return (ExitOk, ParseControllerIds(text));
            }
            catch (Exception exception)
            {
                lastFailure = exception;
            }
        }

        string detail = lastFailure is null ? string.Empty : $"，最后一次错误：{lastFailure.Message}";
        _error.WriteLine(
            $"无法连接到 PadShift API（尝试端口 {string.Join("、", ports)}）{detail}。请确认 PadShift 正在运行。");
        return (ExitFailure, new List<Guid>());
    }

    /// <summary>
    /// Parse the PadShift controller list (<c>[{ DataType, Controllers: [{ Guid, ... }] }]</c>).
    /// Also accepts a flat object array with a direct <c>Guid</c> so future shapes keep working.
    /// </summary>
    internal static List<Guid> ParseControllerIds(string json)
    {
        var ids = new List<Guid>();
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return ids;
            }

            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (item.TryGetProperty("Controllers", out JsonElement nested)
                    && nested.ValueKind == JsonValueKind.Array)
                {
                    AddIds(nested, ids);
                }

                AddIds(item, ids);
            }
        }
        catch (JsonException)
        {
            // A malformed list simply yields no controller.
        }

        return ids;
    }

    private static void AddIds(JsonElement element, List<Guid> ids)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray())
            {
                AddIds(child, ids);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (element.TryGetProperty("Guid", out JsonElement guid)
            && guid.ValueKind == JsonValueKind.String
            && guid.TryGetGuid(out Guid id)
            && !ids.Contains(id))
        {
            ids.Add(id);
        }
    }

    private async Task<int> SendAsync(CliCommand command, HttpMethod method, string path, object? body)
    {
        string? json = body is null ? null : JsonSerializer.Serialize(body);
        int[] ports = command.Port is { } pinned ? [pinned] : DefaultPorts;
        Exception? lastFailure = null;

        foreach (int port in ports)
        {
            string url = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}{path}";
            using var request = new HttpRequestMessage(method, url);
            if (json is not null)
            {
                request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            }

            HttpResponseMessage response;
            try
            {
                response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Connection refused / timeout: try the next candidate port.
                lastFailure = exception;
                continue;
            }

            using (response)
            {
                string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                WriteResponse(text, response);

                if (response.IsSuccessStatusCode)
                {
                    return ExitOk;
                }

                _error.WriteLine(
                    $"请求失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}（{method.Method} {url}）");
                return ExitFailure;
            }
        }

        string detail = lastFailure is null ? string.Empty : $"，最后一次错误：{lastFailure.Message}";
        _error.WriteLine(
            $"无法连接到 PadShift API（尝试端口 {string.Join("、", ports)}）{detail}。请确认 PadShift 正在运行。");
        return ExitFailure;
    }

    private void WriteResponse(string text, HttpResponseMessage response)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            _output.WriteLine(text.TrimEnd());
        }
        else
        {
            _output.WriteLine($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        _output.Flush();
    }

    private static string SlotName(int slot) => "Slot" + slot.ToString(CultureInfo.InvariantCulture);
}
