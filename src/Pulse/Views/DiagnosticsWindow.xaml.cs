using System.IO;
using System.Windows;
using Microsoft.Win32;
using Pulse.Services;
using Wpf.Ui.Controls;

namespace Pulse.Views;

/// <summary>Shows the diagnostics report before it goes anywhere: copy it, save it, or close.</summary>
public partial class DiagnosticsWindow : FluentWindow
{
    public DiagnosticsWindow(string report)
    {
        InitializeComponent();
        Report.Text = report;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(Report.Text);
            Status.Text = "Copied. Paste it into your bug report.";
            Log.Write("Diagnostics copied");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another app is holding the clipboard.
            Status.Text = "The clipboard is busy. Try again, or save it as a file.";
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            FileName = $"pulse-diagnostics-{DateTime.Now:yyyy-MM-dd-HHmm}.txt",
            Filter = "Text file (*.txt)|*.txt",
            DefaultExt = ".txt",
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            File.WriteAllText(dialog.FileName, Report.Text);
            Status.Text = $"Saved as {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status.Text = $"Couldn't save it: {ex.Message}";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
