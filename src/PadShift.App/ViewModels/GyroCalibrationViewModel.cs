using CommunityToolkit.Mvvm.ComponentModel;
using PadShift.App.Infrastructure;
using PadShift.App.Services;
using PadShift.Core.Host;

namespace PadShift.App.ViewModels;

/// <summary>One controller row in the gyro wizard; only <see cref="HasGyro"/> devices can be picked.</summary>
public sealed class GyroDeviceItem
{
    public GyroDeviceItem(HostController controller)
    {
        Id = controller.Id;
        Name = string.IsNullOrWhiteSpace(controller.Name) ? Loc.T("g.pad") : controller.Name;
        HasGyro = controller.HasGyro;
        ControllerType = controller.ControllerType;
    }

    public Guid Id { get; }

    public string Name { get; }

    public string ControllerType { get; }

    public bool HasGyro { get; }

    public string DisplayName => HasGyro
        ? Loc.F("g.padlineF", Name, ControllerType)
        : Loc.F("g.padlineNoF", Name, ControllerType);
}

/// <summary>
/// Three-step gyro calibration wizard state. Step 1 picks a gyro-capable pad, step 2 collects two
/// seconds of static samples and computes the zero bias, step 3 shows the bias/drift and optionally
/// writes it into the loaded profile and the live <see cref="DeviceSession"/>.
/// </summary>
public sealed class GyroCalibrationViewModel : ObservableObject
{
    public const int SampleDurationMs = 2000;

    private readonly IRemapHost _host;
    private readonly DeviceSession? _session;
    private readonly AppSettings _settings;
    private readonly string? _settingsPath;
    private readonly List<GyroSample> _samples = new(256);

    private IReadOnlyList<GyroDeviceItem> _devices = Array.Empty<GyroDeviceItem>();
    private GyroDeviceItem? _selectedDevice;
    private int _step;
    private bool _isSampling;
    private double _progress;
    private string _status = "";
    private string? _biasText;
    private string? _driftText;
    private GyroBias? _bias;

    public GyroCalibrationViewModel(
        IRemapHost host,
        DeviceSession? session,
        AppSettings settings,
        string? settingsPath = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _session = session;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settingsPath = settingsPath;
        RefreshDevices();
    }

    public int Step
    {
        get => _step;
        private set
        {
            if (SetProperty(ref _step, value))
            {
                OnPropertyChanged(nameof(StepTitle));
                OnPropertyChanged(nameof(CanGoNext));
                OnPropertyChanged(nameof(IsLastStep));
                OnPropertyChanged(nameof(NextButtonText));
            }
        }
    }

    public IReadOnlyList<GyroDeviceItem> Devices
    {
        get => _devices;
        private set => SetProperty(ref _devices, value);
    }

    public GyroDeviceItem? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                OnPropertyChanged(nameof(CanGoNext));
                OnPropertyChanged(nameof(DeviceHint));
            }
        }
    }

    public bool HasGyroDevice => Devices.Any(device => device.HasGyro);

    public string DeviceHint => HasGyroDevice
        ? Loc.T("g.pickHint")
        : Loc.T("g.noGyroPads");

    public string StepTitle => Step switch
    {
        0 => Loc.T("g.step1"),
        1 => Loc.T("g.step2"),
        _ => Loc.T("g.step3"),
    };

    public string NextButtonText => Step switch
    {
        0 => Loc.T("g.next"),
        1 => Loc.T("g.finish"),
        _ => Loc.T("g.done"),
    };

    public bool IsLastStep => Step >= 2;

    /// <summary>Step 0 requires a gyro pad; step 1 requires a computed bias; step 2 closes.</summary>
    public bool CanGoNext => Step switch
    {
        0 => SelectedDevice?.HasGyro == true,
        1 => !IsSampling && _bias is not null,
        _ => true,
    };

    public bool IsSampling
    {
        get => _isSampling;
        private set
        {
            if (SetProperty(ref _isSampling, value))
            {
                OnPropertyChanged(nameof(CanGoNext));
            }
        }
    }

    public double Progress
    {
        get => _progress;
        private set
        {
            if (SetProperty(ref _progress, Math.Clamp(value, 0, 1)))
            {
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    public string ProgressText => $"{Math.Round(Progress * 100):0}%";

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string? BiasText
    {
        get => _biasText;
        private set => SetProperty(ref _biasText, value);
    }

    public string? DriftText
    {
        get => _driftText;
        private set => SetProperty(ref _driftText, value);
    }

    public GyroBias? Bias => _bias;

    public void RefreshDevices()
    {
        var items = new List<GyroDeviceItem>();
        try
        {
            foreach (HostController controller in _host.Controllers)
            {
                items.Add(new GyroDeviceItem(controller));
            }
        }
        catch (Exception ex)
        {
            Status = Loc.T("m.readpadsfail") + ex.Message;
        }

        Devices = items;
        SelectedDevice = items.FirstOrDefault(device => device.HasGyro) ?? items.FirstOrDefault();
        OnPropertyChanged(nameof(HasGyroDevice));
        OnPropertyChanged(nameof(DeviceHint));
        OnPropertyChanged(nameof(CanGoNext));

        if (items.Count == 0)
        {
            Status = Loc.T("g.noPad");
        }
        else if (!HasGyroDevice)
        {
            Status = Loc.T("g.noGyro");
        }
        else
        {
            Status = Loc.T("g.picknext");
        }
    }

    public bool MoveNext()
    {
        switch (Step)
        {
            case 0:
                if (SelectedDevice?.HasGyro != true)
                {
                    Status = Loc.T("g.pickPlease");
                    return false;
                }

                Step = 1;
                BeginSampling();
                return true;

            case 1:
                if (_bias is null)
                {
                    Status = Loc.T("g.pickFirst");
                    return false;
                }

                Step = 2;
                return true;

            default:
                return true;
        }
    }

    public void MoveBack()
    {
        if (Step == 0)
        {
            return;
        }

        Step--;
        if (Step == 1)
        {
            BeginSampling();
        }
    }

    // ------------------------------------------------------------------ sampling

    public void BeginSampling()
    {
        _samples.Clear();
        _bias = null;
        BiasText = null;
        DriftText = null;
        Progress = 0;
        IsSampling = true;
        OnPropertyChanged(nameof(CanGoNext));
        Status = Loc.T("g.sampling");

        if (_session is null)
        {
            Status = Loc.T("g.livesample");
            IsSampling = false;
        }
    }

    /// <summary>Feeds one sample (called by the wizard timer from <see cref="DeviceSession.LatestInput"/>).</summary>
    public bool AddSample(double x, double y, double z, double progress)
    {
        if (!IsSampling)
        {
            return false;
        }

        _samples.Add(new GyroSample(x, y, z));
        Progress = progress;
        return true;
    }

    /// <summary>Latest controller poll, read by the wizard timer.</summary>
    public ControllerSnapshot ReadInput() => _session?.LatestInput ?? ControllerSnapshot.None;

    /// <summary>Stops sampling and computes the bias; returns false with <see cref="Status"/> set.</summary>
    public bool CompleteSampling()
    {
        if (!IsSampling)
        {
            return _bias is not null;
        }

        IsSampling = false;
        Progress = 1;

        if (_samples.Count == 0)
        {
            Status = Loc.T("g.nosamples");
            OnPropertyChanged(nameof(CanGoNext));
            return false;
        }

        _bias = GyroBiasCalculator.Compute(_samples, GyroBiasMethod.Mean, rejectOutliers: true);
        BiasText = $"零偏 X {_bias.X:F3}  Y {_bias.Y:F3}  Z {_bias.Z:F3} °/s（{_bias.SampleCount} 个样本，已剔除离群值）";
        DriftText = $"漂移（峰峰值） X {_bias.DriftX:F3}  Y {_bias.DriftY:F3}  Z {_bias.DriftZ:F3} °/s";
        Status = _bias.DriftX + _bias.DriftY + _bias.DriftZ > 3
            ? Loc.T("g.moved")
            : Loc.T("g.done");
        OnPropertyChanged(nameof(CanGoNext));
        return true;
    }

    /// <summary>
    /// Applies the bias immediately to <see cref="DeviceSession"/> (the next poll subtracts it) and
    /// optionally writes <c>devices.hardware[].default.gyroBias</c> into the loaded profile.
    /// </summary>
    public bool ApplyBias(string? profilePath, bool writeProfile, out string? error)
    {
        error = null;
        if (_bias is null)
        {
            error = Loc.T("g.notsampled");
            return false;
        }

        _session?.SetGyroBias(_bias);

        _settings.GyroBias = _bias.Clone();
        if (!_settings.Save(_settingsPath))
        {
            error = Loc.T("g.writefail");
        }

        if (!writeProfile)
        {
            return true;
        }

        if (!GyroCalibrationService.TryWriteBias(profilePath, deviceId: null, _bias, out string? writeError))
        {
            error = writeError;
            return false;
        }

        return true;
    }
}
