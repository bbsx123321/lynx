using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using PadShift.App.Services;
using PadShift.App.ViewModels;
using PadShift.Core;
using PadShift.Core.Host;
using PadShift.Core.Profile;
using PadShift.Engine.Hosting;
using Xunit;
using IOPath = System.IO.Path;

namespace PadShift.Tests;

/// <summary>
/// Regression tests for the 映射 ON toggle: the real failure ("启用映射失败") happened when no
/// config was live on the slot, and after a hot-plug host rebuild when the controller Guid changed.
/// Everything runs head-less against real <see cref="RemapHost"/> instances and fake devices.
/// </summary>
public sealed class RemapToggleTests
{
    // ------------------------------------------------------------------ fakes

    private sealed class FakeSource : IInputSource
    {
        public string DeviceId { get; } = "XInput#0";

        public bool IsConnected => true;

        public bool TryPoll(ControllerState state) => true;

        public void Dispose()
        {
        }
    }

    private sealed class FakeSink : IOutputSink
    {
        public void Submit(OutputIntent intent)
        {
        }

        public void ReleaseAll()
        {
        }

        public void Dispose()
        {
        }
    }

    // ------------------------------------------------------------------ helpers

    private static string NewProfilesRoot()
    {
        string root = IOPath.Combine(IOPath.GetTempPath(), "padshift-toggle-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string WriteProfile(string root, string name = "Aim & Move")
    {
        string folder = IOPath.Combine(root, "Demo Game");
        Directory.CreateDirectory(folder);
        string path = IOPath.Combine(folder, name + ".rewasd");
        ProfileIo.Save(path, Kit.Profile());
        return path;
    }

    private static RemapHost CreateHost(string root) =>
        new(new[] { new FakeSource() }, new FakeSink(), new HostOptions { ProfilesRoot = root, TickMs = 5 });

    // ------------------------------------------------------------------ a) no config

    [Fact]
    public void EnableWithoutAppliedConfig_IsRefused_WithActionableReason()
    {
        string root = NewProfilesRoot();
        using var host = CreateHost(root);
        using var viewModel = new MainViewModel(host, root);

        Assert.NotNull(viewModel.SelectedController);
        Guid id = viewModel.SelectedController!.Id;

        viewModel.RemapEnabled = true;

        Assert.False(viewModel.RemapEnabled);
        Assert.False(host.RemapEnabled);
        Assert.Equal(RemapState.NothingApplied, host.GetRemapState(id));
        Assert.NotNull(viewModel.InlineMessage);
        Assert.Contains("启用映射失败", viewModel.InlineMessage);
        Assert.Contains("配置", viewModel.InlineMessage);
        Assert.Contains("双击", viewModel.InlineMessage);
        Assert.DoesNotContain("no profile applied", viewModel.InlineMessage);
    }

    // ------------------------------------------------------------------ b) config applied

    [Fact]
    public async Task EnableWithAppliedConfig_Succeeds_AndStaysRemapOn()
    {
        string root = NewProfilesRoot();
        WriteProfile(root);
        using var host = CreateHost(root);
        using var viewModel = new MainViewModel(host, root);

        ProfileItem item = Assert.Single(Assert.Single(viewModel.Library.Games).Profiles);
        Assert.True(await viewModel.ApplyProfileAsync(item), viewModel.InlineMessage);
        Assert.True(viewModel.RemapEnabled);

        // Applying a config turns remap on; disable it so the toggle itself is what enables remap.
        viewModel.RemapEnabled = false;
        Assert.False(viewModel.RemapEnabled);
        Assert.Contains("映射已停用", viewModel.StatusText);

        viewModel.RemapEnabled = true;

        Guid id = viewModel.SelectedController!.Id;
        Assert.True(viewModel.RemapEnabled);
        Assert.True(host.RemapEnabled);
        Assert.Equal(RemapState.RemapOn, host.GetRemapState(id));
        Assert.Contains("映射已启用", viewModel.StatusText);
        Assert.Null(viewModel.InlineMessage);
    }

    // ------------------------------------------------------------------ c) selected but not applied

    [Fact]
    public void EnableWithSelectedConfig_AutoAppliesIt_AndSaysSo()
    {
        string root = NewProfilesRoot();
        string path = WriteProfile(root);
        using var host = CreateHost(root);
        using var viewModel = new MainViewModel(host, root);

        // The user selected the config in the library (single click only previews it) and pressed 映射 ON.
        ProfileItem item = Assert.Single(Assert.Single(viewModel.Library.Games).Profiles);
        viewModel.PreviewProfile(item);
        Assert.Empty(host.GetAppliedConfigs(viewModel.SelectedController!.Id, 0));

        viewModel.RemapEnabled = true;

        Guid id = viewModel.SelectedController!.Id;
        Assert.True(viewModel.RemapEnabled);
        Assert.True(host.RemapEnabled);
        Assert.Equal(new[] { path }, host.GetAppliedConfigs(id, 0));
        Assert.Equal(RemapState.RemapOn, host.GetRemapState(id));
        Assert.Contains("已自动应用配置", viewModel.InlineMessage);
        Assert.Contains(item.Name, viewModel.InlineMessage);
    }

    // ------------------------------------------------------------------ d) device Guid change

    [Fact]
    public async Task EnableAfterHostRebuild_StillWorks_WithTheGuidTheUiHolds()
    {
        string root = NewProfilesRoot();
        string path = WriteProfile(root);
        var source = new FakeSource();

        using var session = new DeviceSession(root, () => new IInputSource[] { source }, createVirtualPad: false);
        session.Start();

        Guid idBeforeRebuild = Assert.Single(session.Controllers).Id;
        ApplyConfigResult applied = await session.ApplyConfigAsync(idBeforeRebuild, path, 0);
        Assert.True(applied.Success, applied.Error);

        session.DisableRemap(idBeforeRebuild);
        Assert.Equal(RemapState.RemapOff, session.GetRemapState(idBeforeRebuild));

        int generation = session.HostGeneration;
        session.RebuildHostForTest();

        Assert.True(session.HostGeneration > generation, "the test seam did not rebuild the host");
        Guid idAfterRebuild = Assert.Single(session.Controllers).Id;

        // A rebuild used to assign Guid.NewGuid(), so the id the UI held became "unknown controller".
        Assert.Equal(idBeforeRebuild, idAfterRebuild);

        session.DisableRemap(idAfterRebuild);
        EnableRemapResult result = session.EnableRemap(idBeforeRebuild);

        Assert.True(result.Success, result.Error);
        Assert.True(session.RemapEnabled);
        Assert.Equal(RemapState.RemapOn, session.GetRemapState(idBeforeRebuild));
        Assert.True(session.TryGetState(idBeforeRebuild, out _));
    }

    [Fact]
    public void EnableAfterHostRebuild_AutoAppliesTheSelectedConfig_ThenEnables()
    {
        string root = NewProfilesRoot();
        string path = WriteProfile(root);
        var source = new FakeSource();

        using var session = new DeviceSession(root, () => new IInputSource[] { source }, createVirtualPad: false);
        session.Start();

        using var viewModel = new MainViewModel(session, root);
        Guid id = viewModel.SelectedController!.Id;

        int generation = session.HostGeneration;
        session.RebuildHostForTest();
        Assert.True(session.HostGeneration > generation);
        Assert.Equal(id, Assert.Single(session.Controllers).Id);

        viewModel.RemapEnabled = true;

        Assert.True(viewModel.RemapEnabled);
        Assert.True(session.RemapEnabled);
        Assert.Equal(new[] { path }, session.GetAppliedConfigs(id, 0));
        Assert.Equal(RemapState.RemapOn, session.GetRemapState(id));
        Assert.Contains("已自动应用配置", viewModel.InlineMessage);
    }

    // ------------------------------------------------------------------ e) disposed host

    [Fact]
    public void EnableWhenHostIsDisposed_ReturnsFriendlyError()
    {
        string root = NewProfilesRoot();
        var host = CreateHost(root);
        using var viewModel = new MainViewModel(host, root);
        Guid id = viewModel.SelectedController!.Id;

        host.Dispose();

        EnableRemapResult direct = host.EnableRemap(id);
        Assert.False(direct.Success);
        Assert.NotNull(direct.Error);
        Assert.Contains("host disposed", direct.Error);

        viewModel.RemapEnabled = true;

        Assert.False(viewModel.RemapEnabled);
        Assert.NotNull(viewModel.InlineMessage);
        Assert.Contains("启用映射失败", viewModel.InlineMessage);
        Assert.Contains("映射服务已停止", viewModel.InlineMessage);
        Assert.Contains("重新启动", viewModel.InlineMessage);
    }
}
