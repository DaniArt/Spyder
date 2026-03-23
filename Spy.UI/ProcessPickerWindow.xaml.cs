using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;

namespace Spy.UI;

public partial class ProcessPickerWindow : Window
{
    public int? SelectedPid { get; private set; }
    public string SelectedProcessName { get; private set; } = "";

    public ProcessPickerWindow()
    {
        InitializeComponent();
        RefreshList();
    }

    class ProcItem
    {
        public int Pid { get; init; }
        public string Name { get; init; } = "";
        public string Title { get; init; } = "";
        public string Path { get; init; } = "";
    }

    void RefreshList()
    {
        var items = new List<ProcItem>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string title = p.MainWindowTitle ?? "";
                if (string.IsNullOrWhiteSpace(title) && p.MainWindowHandle == IntPtr.Zero)
                    continue;
                string path = "";
                try { path = p.MainModule?.FileName ?? ""; } catch { }
                items.Add(new ProcItem { Pid = p.Id, Name = p.ProcessName, Title = title, Path = path });
            }
            catch { }
        }
        Lst.ItemsSource = items.OrderBy(i => i.Name).ToList();
    }

    void BtnRefresh_Click(object sender, RoutedEventArgs e) => RefreshList();

    void BtnSelect_Click(object sender, RoutedEventArgs e)
    {
        if (Lst.SelectedItem is ProcItem item)
        {
            SelectedPid = item.Pid;
            SelectedProcessName = item.Name;
            DialogResult = true;
        }
    }

    void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
