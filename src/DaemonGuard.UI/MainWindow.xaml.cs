using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DaemonGuard.Database;
using DaemonGuard.UI.Services;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace DaemonGuard.UI;

public partial class MainWindow : Window
{
    private readonly IDashboardService _service;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _refreshing;
    private readonly PlotModel _trendModel = new() { TitleFontSize = 12 };
    private readonly LineSeries _scoreSeries = new() { Color = OxyColors.SteelBlue, MarkerType = MarkerType.Circle, MarkerSize = 2.5 };

    // App.xaml StartupUri uses this one. The database file is shared with the Collector/Engine.
    public MainWindow() : this(CreateService()) { }

    private static IDashboardService CreateService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DaemonGuard");
        Directory.CreateDirectory(dir);
        return new DatabaseDashboardService(new DatabaseRepository(Path.Combine(dir, "daemonguard.db")));
    }

    public MainWindow(IDashboardService service)
    {
        InitializeComponent();
        _service = service;

        _trendModel.Axes.Add(new DateTimeAxis { Position = AxisPosition.Bottom, StringFormat = "HH:mm:ss" });
        _trendModel.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Minimum = 0, Maximum = 10, Title = "Waste score" });
        _trendModel.Series.Add(_scoreSeries);
        TrendPlot.Model = _trendModel;

        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) => { await RefreshAsync(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var data = await _service.GetProcessesAsync();
            var selectedPid = (ProcessGrid.SelectedItem as ProcessInfo)?.Pid;

            _refreshing = true;                       // keep the selection when the list reloads
            ProcessGrid.ItemsSource = data;
            var match = selectedPid is null ? null : data.FirstOrDefault(p => p.Pid == selectedPid);
            ProcessGrid.SelectedItem = match;
            _refreshing = false;

            UpdateDetails(match);
            await LoadTrendAsync(match);
        }
        catch (Exception ex)
        {
            _refreshing = false;
            StatusText.Text = "Refresh failed: " + ex.Message;
        }
    }

    private async void ProcessGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        var p = ProcessGrid.SelectedItem as ProcessInfo;
        UpdateDetails(p);
        await LoadTrendAsync(p);
    }

    private async Task LoadTrendAsync(ProcessInfo? p)
    {
        _scoreSeries.Points.Clear();
        _trendModel.Title = p is null ? "" : $"{p.Name} (PID {p.Pid})";
        if (p is not null)
        {
            try
            {
                foreach (var t in await _service.GetHistoryAsync(p))
                    _scoreSeries.Points.Add(new DataPoint(DateTimeAxis.ToDouble(t.Time), t.Score));
            }
            catch (Exception ex) { StatusText.Text = "Trend failed: " + ex.Message; }
        }
        _trendModel.InvalidatePlot(true);
    }

    private void UpdateDetails(ProcessInfo? p)
    {
        if (p is null)
        {
            ExplanationText.Text = "Select a process to see the explanation.";
            BreakdownText.Text = "";
            CloseButton.IsEnabled = true;
            return;
        }

        ExplanationText.Text = p.IsProtected
            ? p.Explanation + "\n\nProtected: this process cannot be closed."
            : p.Explanation;
        BreakdownText.Text = p.Breakdown;
        CloseButton.IsEnabled = !p.IsProtected;
    }

    private async void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessGrid.SelectedItem is not ProcessInfo p)
        {
            MessageBox.Show("Please select a process first.");
            return;
        }
        if (p.IsProtected) return;   // safety: never close protected processes

        var answer = MessageBox.Show(
            $"Close {p.Name} (PID {p.Pid})?\n\n{p.Explanation}",
            "Confirm action", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (answer == MessageBoxResult.Yes)
            await RunActionAsync(p, "Close");
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessGrid.SelectedItem is not ProcessInfo p)
        {
            MessageBox.Show("Please select a process first.");
            return;
        }
        await RunActionAsync(p, ((Button)sender).Content.ToString()!);
    }

    private async Task RunActionAsync(ProcessInfo p, string action)
    {
        // TODO: call the Engine's ExecuteProcessAction(pid, action) here when it exists.
        const string result = "Simulated";
        try
        {
            await _service.LogActionAsync(p, action, result);
            StatusText.Text = $"{action} on {p.Name} (PID {p.Pid}): {result}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"{action} logging failed: {ex.Message}";
        }
    }
}