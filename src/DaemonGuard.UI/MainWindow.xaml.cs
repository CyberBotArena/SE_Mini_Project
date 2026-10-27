using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace DaemonGuard.UI;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ProcessGrid.ItemsSource = new List<ProcessInfo>
        {
            new ProcessInfo { Pid = 4120, Name = "OldBrowserHelper.exe", Score = 8.5, Status = "Wasteful",
                  Explanation = "4 GB RAM, 40 min inactive, no network, no disk activity.",
                  Breakdown = "High memory: +3\nLong inactivity: +3\nVery low CPU: +2\nNo network: +1\nNo disk: +1" },
            new ProcessInfo { Pid = 2210, Name = "blender.exe", Score = 4.0, Status = "Suspicious",
                  Explanation = "High memory, but active disk writes detected.",
                  Breakdown = "High memory: +3\nNo network: +1\nActive disk writes: +0" },
            new ProcessInfo { Pid = 4, Name = "System", Score = 1.0, Status = "Healthy",
                  Explanation = "Protected Windows system process.", IsProtected = true,
                  Breakdown = "No network: +1" },
        };
    }

    private void ProcessGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
{
    if (ProcessGrid.SelectedItem is ProcessInfo p)
    {
        ExplanationText.Text = p.IsProtected
            ? p.Explanation + "\n\nProtected: this process cannot be closed."
            : p.Explanation;

        CloseButton.IsEnabled = !p.IsProtected;
        BreakdownText.Text = p.Breakdown;
    }
}
    private void CloseButton_Click(object sender, RoutedEventArgs e)
{
    if (ProcessGrid.SelectedItem is not ProcessInfo p)
    {
        MessageBox.Show("Please select a process first.");
        return;
    }

    var answer = MessageBox.Show(
        $"Close {p.Name} (PID {p.Pid})?\n\n{p.Explanation}",
        "Confirm action",
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning);

    if (answer == MessageBoxResult.Yes)
        MessageBox.Show($"{p.Name} would be closed (simulated).");
}

private void ActionButton_Click(object sender, RoutedEventArgs e)
{
    if (ProcessGrid.SelectedItem is not ProcessInfo p)
    {
        MessageBox.Show("Please select a process first.");
        return;
    }

    var action = ((Button)sender).Content;
    MessageBox.Show($"{action} on {p.Name} (PID {p.Pid}) - simulated.");
}

}

public class ProcessInfo
{
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    public double Score { get; set; }
    public string Status { get; set; } = "";
    public string Explanation { get; set; } = "";
    public bool IsProtected { get; set; }
    public string Breakdown { get; set; } = "";
}