using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace Spy.UI;

public partial class LocatorManagerWindow : Window
{
    string _path;
    LocatorStorage.LocatorFileModel _model;

    public LocatorManagerWindow(string path)
    {
        InitializeComponent();
        _path = path;
        TxtPath.Text = path;
        _model = LocatorStorage.Load(_path);
        RefreshList();
    }

    class LocatorListItem
    {
        public string Name { get; init; } = "";
        public string SelectorPreview { get; init; } = "";
    }

    void RefreshList()
    {
        var items = _model.Elements
            .Select(kvp => new LocatorListItem
            {
                Name = kvp.Key,
                SelectorPreview = BuildPreview(kvp.Value)
            })
            .OrderBy(i => i.Name)
            .ToList();

        Lst.ItemsSource = items;
    }

    static string BuildPreview(LocatorStorage.LocatorElement el)
    {
        // Предпочитаем human_selector; если нет — падаем назад на старое поле selector
        var s = el.HumanSelector;
        if (string.IsNullOrWhiteSpace(s))
            s = el.Selector ?? "";
        if (string.IsNullOrEmpty(s))
            return "";
        return s.Length > 100 ? s[..100] + "..." : s;
    }

    void BtnClearItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string name)
            return;

        if (_model.Elements.Remove(name))
        {
            LocatorStorage.Save(_path, _model);
            RefreshList();
        }
    }

    void BtnClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Clear all selectors from this file?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes)
            return;

        _model.Elements.Clear();
        LocatorStorage.Save(_path, _model);
        RefreshList();
    }

    void BtnClearSelected_Click(object sender, RoutedEventArgs e)
    {
        if (Lst.SelectedItem is not LocatorListItem item)
            return;

        if (_model.Elements.Remove(item.Name))
        {
            LocatorStorage.Save(_path, _model);
            RefreshList();
        }
    }

    void BtnChangeFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "JSON files (*.json)|*.json",
            DefaultExt = ".json"
        };

        if (dlg.ShowDialog() == true)
        {
            _path = dlg.FileName;
            TxtPath.Text = _path;
            _model = LocatorStorage.Load(_path);
            RefreshList();
        }
    }

    void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

