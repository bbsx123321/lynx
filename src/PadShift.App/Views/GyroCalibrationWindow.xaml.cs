using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using PadShift.App.Services;
using PadShift.App.ViewModels;

namespace PadShift.App.Views;

/// <summary>
/// Modal three-step gyro calibration wizard. All decisions live in
/// <see cref="GyroCalibrationViewModel"/>; the window only drives the 2 s sampling timer and the
/// panel visibility.
/// </summary>
public partial class GyroCalibrationWindow : Window
{
    private readonly GyroCalibrationViewModel _viewModel;
    private readonly DispatcherTimer _sampleTimer;
    private readonly Stopwatch _stopwatch = new();

    public GyroCalibrationWindow(GyroCalibrationViewModel viewModel, string? profilePath)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        ProfilePath = profilePath;
        DataContext = viewModel;

        _sampleTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(25),
        };
        _sampleTimer.Tick += OnSampleTick;
        Closed += (_, _) => _sampleTimer.Stop();
        Loaded += (_, _) => UpdatePanels();
    }

    /// <summary>Profile that receives <c>gyroBias</c> when the user leaves the checkbox on.</summary>
    public string? ProfilePath { get; }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsLastStep)
        {
            ApplyAndClose();
            return;
        }

        if (_viewModel.MoveNext())
        {
            UpdatePanels();
            if (_viewModel.Step == 1)
            {
                StartSampling();
            }
        }
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        _sampleTimer.Stop();
        _viewModel.MoveBack();
        UpdatePanels();
        if (_viewModel.Step == 1)
        {
            StartSampling();
        }
    }

    private void OnRestartSampling(object sender, RoutedEventArgs e)
    {
        _viewModel.BeginSampling();
        StartSampling();
    }

    private void OnRefreshDevices(object sender, RoutedEventArgs e) => _viewModel.RefreshDevices();

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void StartSampling()
    {
        _stopwatch.Restart();
        _sampleTimer.Start();
    }

    private void OnSampleTick(object? sender, EventArgs e)
    {
        double elapsed = _stopwatch.Elapsed.TotalMilliseconds;
        double progress = Math.Clamp(elapsed / GyroCalibrationViewModel.SampleDurationMs, 0, 1);

        ControllerSnapshot snapshot = _viewModel.ReadInput();
        if (snapshot.HasState)
        {
            _viewModel.AddSample(snapshot.GyroX, snapshot.GyroY, snapshot.GyroZ, progress);
        }

        if (elapsed >= GyroCalibrationViewModel.SampleDurationMs)
        {
            _sampleTimer.Stop();
            _viewModel.CompleteSampling();
            UpdatePanels();
        }
    }

    private void ApplyAndClose()
    {
        bool writeProfile = WriteProfileCheck.IsChecked == true;
        if (!_viewModel.ApplyBias(ProfilePath, writeProfile, out string? error))
        {
            ApplyMessage.Text = error ?? "写回配置失败";
            return;
        }

        ApplyMessage.Text = error ?? "";
        DialogResult = true;
    }

    private void UpdatePanels()
    {
        Step0Panel.Visibility = _viewModel.Step == 0 ? Visibility.Visible : Visibility.Collapsed;
        Step1Panel.Visibility = _viewModel.Step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Panel.Visibility = _viewModel.Step == 2 ? Visibility.Visible : Visibility.Collapsed;
    }
}
