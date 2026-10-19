using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PadShift.Cli;
using Xunit;

namespace PadShift.Tests;

/// <summary>
/// CLI argument parsing table and exit codes, driven through a stub
/// <see cref="HttpMessageHandler"/> so no server is required.
/// </summary>
public class CliTests
{
    private static readonly Guid Controller = Guid.Parse("a1b2c3d4-e5f6-4711-8899-aabbccddeeff");

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public List<RecordedRequest> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult()
                          ?? string.Empty;
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, body));
            return Task.FromResult(_respond(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Ok(string body = "{\"IsSucceded\":true}") =>
        Json(HttpStatusCode.OK, body);

    private static async Task<(int Exit, string Output, string Error, IReadOnlyList<RecordedRequest> Requests)>
        InvokeAsync(string[] args, Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        using var handler = new StubHandler(respond ?? (_ => Ok()));
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var runner = new CliRunner(handler, output, error, TimeSpan.FromSeconds(2));

        int exit = await runner.RunAsync(args).ConfigureAwait(false);
        return (exit, output.ToString(), error.ToString(), handler.Requests.ToArray());
    }

    private static JsonElement BodyOf(RecordedRequest request)
    {
        using JsonDocument document = JsonDocument.Parse(request.Body);
        return document.RootElement.Clone();
    }

    // ------------------------------------------------------------- parser table

    public static TheoryData<string[], CliVerb, int?, string?, Guid?, bool, int?> ValidParseCases => new()
    {
        { Array.Empty<string>(), CliVerb.Help, null, null, null, false, null },
        { new[] { "help" }, CliVerb.Help, null, null, null, false, null },
        { new[] { "version" }, CliVerb.Version, null, null, null, false, null },
        { new[] { "version", "--port", "35500" }, CliVerb.Version, null, null, null, false, 35500 },
        { new[] { "apply", "--config", @"C:\Profiles\game.rewasd" }, CliVerb.Apply, 1, @"C:\Profiles\game.rewasd", null, false, null },
        { new[] { "apply", "--config=game.rewasd", "--slot", "3", "--controller", "A1B2C3D4-E5F6-4711-8899-AABBCCDDEEFF" }, CliVerb.Apply, 3, "game.rewasd", Controller, false, null },
        { new[] { "apply", "--config", "a.rewasd", "--slot=2", "--port", "36001" }, CliVerb.Apply, 2, "a.rewasd", null, false, 36001 },
        { new[] { "select_slot", "--slot", "2" }, CliVerb.SelectSlot, 2, null, null, false, null },
        { new[] { "select-slot", "--slot=1", "--controller", "A1B2C3D4-E5F6-4711-8899-AABBCCDDEEFF" }, CliVerb.SelectSlot, 1, null, Controller, false, null },
        { new[] { "clear_slot", "--slot=4", "--port", "36002" }, CliVerb.ClearSlot, 4, null, null, false, 36002 },
        { new[] { "remap", "--on" }, CliVerb.Remap, null, null, null, true, null },
        { new[] { "remap", "--off", "--controller", "A1B2C3D4-E5F6-4711-8899-AABBCCDDEEFF" }, CliVerb.Remap, null, null, Controller, false, null },
    };

    [Theory]
    [MemberData(nameof(ValidParseCases))]
    public void Parser_Accepts_Valid_Commands(
        string[] args,
        CliVerb verb,
        int? slot,
        string? config,
        Guid? controller,
        bool remapOn,
        int? port)
    {
        CliParseResult result = CliParser.Parse(args);

        Assert.True(result.Success, result.Error);
        CliCommand command = Assert.IsType<CliCommand>(result.Command);
        Assert.Equal(verb, command.Verb);
        Assert.Equal(slot, command.Slot);
        Assert.Equal(config, command.ConfigPath);
        Assert.Equal(controller, command.Controller);
        Assert.Equal(remapOn, command.RemapOn);
        Assert.Equal(port, command.Port);
    }

    public static TheoryData<string[]> InvalidParseCases => new()
    {
        new[] { "bogus" },
        new[] { "apply" },
        new[] { "apply", "--config" },
        new[] { "apply", "--config", "x", "--slot", "0" },
        new[] { "apply", "--config", "x", "--slot", "5" },
        new[] { "apply", "--config", "x", "--slot", "abc" },
        new[] { "apply", "--config", "x", "--on" },
        new[] { "apply", "--config", "x", "--controller", "not-a-guid" },
        new[] { "apply", "orphan" },
        new[] { "apply", "--config", "x", "--unknown", "1" },
        new[] { "select_slot" },
        new[] { "select_slot", "--slot", "2", "--config", "x" },
        new[] { "clear_slot" },
        new[] { "remap" },
        new[] { "remap", "--on", "--off" },
        new[] { "remap", "--on", "--slot", "1" },
        new[] { "version", "--config", "x" },
        new[] { "help", "--slot", "1" },
        new[] { "apply", "--config", "x", "--port", "70000" },
    };

    [Theory]
    [MemberData(nameof(InvalidParseCases))]
    public void Parser_Rejects_Bad_Usage(string[] args)
    {
        CliParseResult result = CliParser.Parse(args);

        Assert.False(result.Success);
        Assert.Null(result.Command);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    // ------------------------------------------------------------- HTTP / exit codes

    [Fact]
    public async Task Apply_Posts_To_ApplyConfig_And_Prints_The_Response()
    {
        var (exit, output, error, requests) = await InvokeAsync(new[]
        {
            "apply",
            "--config",
            @"C:\Games\Config.rewasd",
            "--slot",
            "2",
            "--controller",
            Controller.ToString(),
        });

        Assert.Equal(CliRunner.ExitOk, exit);
        Assert.Contains("IsSucceded", output);
        Assert.Equal(string.Empty, error);

        RecordedRequest request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://127.0.0.1:35474/v2.6/ControllerService/ApplyConfig", request.Uri.ToString());

        JsonElement body = BodyOf(request);
        Assert.Equal(Controller, body.GetProperty("ControllerGuid").GetGuid());
        Assert.Equal(@"C:\Games\Config.rewasd", body.GetProperty("Path").GetString());
        Assert.Equal("Slot2", body.GetProperty("Slot").GetString());
    }

    [Fact]
    public async Task Apply_Without_Controller_And_Slot_Uses_Defaults()
    {
        var (exit, _, _, requests) = await InvokeAsync(new[] { "apply", "--config", "game.rewasd" });

        Assert.Equal(CliRunner.ExitOk, exit);
        JsonElement body = BodyOf(Assert.Single(requests));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("ControllerGuid").ValueKind);
        Assert.Equal("Slot1", body.GetProperty("Slot").GetString());
    }

    [Fact]
    public async Task SelectSlot_Posts_The_One_Based_Slot_Name()
    {
        var (exit, _, _, requests) = await InvokeAsync(new[]
        {
            "select_slot",
            "--slot",
            "3",
            "--controller",
            Controller.ToString(),
        });

        Assert.Equal(CliRunner.ExitOk, exit);
        RecordedRequest request = Assert.Single(requests);
        Assert.Equal("/v2.6/ControllerService/SelectSlot", request.Uri.AbsolutePath);
        JsonElement body = BodyOf(request);
        Assert.Equal("Slot3", body.GetProperty("Slot").GetString());
        Assert.Equal(Controller, body.GetProperty("Guid").GetGuid());
    }

    [Fact]
    public async Task SelectSlot_Without_Controller_Resolves_Connected_Controllers_First()
    {
        var (exit, _, _, requests) = await InvokeAsync(
            new[] { "select_slot", "--slot", "2" },
            request => request.Method == HttpMethod.Get
                ? Ok($"[{{\"DataType\":\"PeripheralController\",\"Controllers\":[{{\"Guid\":\"{Controller}\"}}]}}]")
                : Ok());

        Assert.Equal(CliRunner.ExitOk, exit);
        Assert.Equal(2, requests.Count);

        Assert.Equal(HttpMethod.Get, requests[0].Method);
        Assert.Equal("/v2.6/ControllerService/Controllers", requests[0].Uri.AbsolutePath);

        Assert.Equal(HttpMethod.Post, requests[1].Method);
        Assert.Equal("/v2.6/ControllerService/SelectSlot", requests[1].Uri.AbsolutePath);
        JsonElement body = BodyOf(requests[1]);
        Assert.Equal(Controller, body.GetProperty("Guid").GetGuid());
        Assert.Equal("Slot2", body.GetProperty("Slot").GetString());
    }

    [Fact]
    public async Task SelectSlot_Without_Controller_Fails_When_No_Controller_Is_Connected()
    {
        var (exit, _, error, requests) = await InvokeAsync(
            new[] { "select_slot", "--slot", "2" },
            _ => Ok("[]"));

        Assert.Equal(CliRunner.ExitFailure, exit);
        Assert.Contains("没有已连接的控制器", error);
        Assert.Single(requests);
    }

    [Fact]
    public async Task ClearSlot_Sends_A_Slot_Array()
    {
        var (exit, _, _, requests) = await InvokeAsync(new[]
        {
            "clear_slot",
            "--slot",
            "4",
            "--controller",
            Controller.ToString(),
        });

        Assert.Equal(CliRunner.ExitOk, exit);
        RecordedRequest request = Assert.Single(requests);
        Assert.Equal("/v2.6/ControllerService/ClearSlot", request.Uri.AbsolutePath);
        JsonElement body = BodyOf(request);
        Assert.Equal(Controller, body.GetProperty("Guid").GetGuid());
        JsonElement slots = body.GetProperty("Slots");
        Assert.Equal(JsonValueKind.Array, slots.ValueKind);
        Assert.Equal("Slot4", Assert.Single(slots.EnumerateArray().ToArray()).GetString());
    }

    [Theory]
    [InlineData("--on", "/v2.6/ControllerService/EnableRemap")]
    [InlineData("--off", "/v2.6/ControllerService/DisableRemap")]
    public async Task Remap_Uses_Enable_Or_Disable_Route(string flag, string path)
    {
        var (exit, _, _, requests) = await InvokeAsync(new[] { "remap", flag });

        Assert.Equal(CliRunner.ExitOk, exit);
        RecordedRequest request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(path, request.Uri.AbsolutePath);

        JsonElement body = BodyOf(request);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("Guid").ValueKind);
        if (flag == "--on")
        {
            Assert.False(body.GetProperty("RemapNoToggled").GetBoolean());
        }
    }

    [Fact]
    public async Task Version_Gets_The_Protocol_Version()
    {
        var (exit, output, error, requests) = await InvokeAsync(
            new[] { "version" },
            _ => Ok("{\"Version\":\"v2.6\"}"));

        Assert.Equal(CliRunner.ExitOk, exit);
        Assert.Contains("v2.6", output);
        Assert.Equal(string.Empty, error);

        RecordedRequest request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/v2.6/Version/GetVersion", request.Uri.AbsolutePath);
        Assert.Equal(string.Empty, request.Body);
    }

    [Fact]
    public async Task Server_Error_Prints_The_Body_And_Returns_Exit_One()
    {
        var (exit, output, error, requests) = await InvokeAsync(
            new[] { "select_slot", "--slot", "2" },
            _ => Json(HttpStatusCode.Conflict, "\"controller is not connected\""));

        Assert.Equal(CliRunner.ExitFailure, exit);
        Assert.Contains("controller is not connected", output);
        Assert.Contains("409", error);
        Assert.Single(requests);
    }

    [Fact]
    public async Task Bad_Usage_Returns_Exit_Two_And_Prints_Chinese_Help()
    {
        var (exit, _, error, requests) = await InvokeAsync(new[] { "remap" });

        Assert.Equal(CliRunner.ExitUsage, exit);
        Assert.Contains("参数错误", error);
        Assert.Contains("用法", error);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task Help_Prints_Chinese_Help_Without_Any_Request()
    {
        var (exit, output, error, requests) = await InvokeAsync(new[] { "help" });

        Assert.Equal(CliRunner.ExitOk, exit);
        Assert.Contains("用法", output);
        Assert.Contains("apply", output);
        Assert.Contains("select_slot", output);
        Assert.Equal(string.Empty, error);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task Port_Override_Is_Used_For_The_Request()
    {
        var (exit, _, _, requests) = await InvokeAsync(
            new[] { "version", "--port", "41234" },
            _ => Ok("{\"Version\":\"v2.6\"}"));

        Assert.Equal(CliRunner.ExitOk, exit);
        RecordedRequest request = Assert.Single(requests);
        Assert.Equal(41234, request.Uri.Port);
        Assert.Equal("127.0.0.1", request.Uri.Host);
    }

    [Fact]
    public async Task Connection_Failures_Fall_Back_To_The_Next_Ports()
    {
        var (exit, output, _, requests) = await InvokeAsync(
            new[] { "version" },
            request => request.RequestUri!.Port == 35476
                ? Ok("{\"Version\":\"v2.6\"}")
                : throw new HttpRequestException("connection refused"));

        Assert.Equal(CliRunner.ExitOk, exit);
        Assert.Contains("v2.6", output);
        Assert.Equal(3, requests.Count);
        Assert.Equal(new[] { 35474, 35475, 35476 }, new[] { requests[0].Uri.Port, requests[1].Uri.Port, requests[2].Uri.Port });
    }

    [Fact]
    public async Task Unreachable_Server_Returns_Exit_One_After_All_Ports()
    {
        var (exit, _, error, requests) = await InvokeAsync(
            new[] { "remap", "--on" },
            _ => throw new HttpRequestException("connection refused"));

        Assert.Equal(CliRunner.ExitFailure, exit);
        Assert.Contains("无法连接", error);
        Assert.Equal(3, requests.Count);
    }
}
