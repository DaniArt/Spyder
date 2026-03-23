/*
Copyright 2026 Daniyar Sagatov
Licensed under the Apache License 2.0
*/

using Spy.Core;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using System.IO;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Spy.Core.Vcl;
using System.Threading.Tasks;

namespace Spy.UI;

public record HierarchyNode(int? Index, string KindIcon, string Text);

public partial class MainWindow : Window
{
    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT lpPoint);
	private HwndSource? _source;
	private bool _registered;
	private OverlayWindow? _overlay;
	private GlobalMouseHook? _mouseHook;
	private SpyElement? _current;
	private bool _spyActive;
    private long _lastHighlightTick;
    private bool _vclMode;
    private VclBridgeClient? _vclClient;
    private Spy.Core.Vcl.VclHookManager _vclHookManager = new();
    private bool _fileLoggingEnabled;
    private readonly object _logSync = new();
    private readonly string _logFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Spyder", "logs", "spyder.log");
    private long _lastVclOverlayTick;
    private int _vclOverlayFailCount;
    private long _vclOverlayBackoffUntil;
    private int _vclOverlayIntervalMs = 95;

    private int _targetPid = 0;

    const int HOTKEY_ID = 1;
    const uint MOD_CONTROL = 0x0002;
    const uint MOD_SHIFT = 0x0004;
    const uint VK_F = 0x46; // F key

    readonly ObservableCollection<Row> _objectRows = new();
    readonly ObservableCollection<Row> _vclRows = new();
    readonly ObservableCollection<string> _vclChainLines = new();
    readonly ObservableCollection<Row> _win32Rows = new();
    readonly ObservableCollection<Row> _uiaRows = new();
    readonly ObservableCollection<Row> _msaaRows = new();
    readonly ObservableCollection<JsonNodeVm> _locatorTree = new();
    string _propsClipboardText = "";

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Register();
        Closed += (_, _) => Unregister();
        UpdateLocatorFileLabel();

        ListObject.ItemsSource = _objectRows;
        ListVcl.ItemsSource = _vclRows;
        ListVclChain.ItemsSource = _vclChainLines;
        ListWin32.ItemsSource = _win32Rows;
        ListUia.ItemsSource = _uiaRows;
        ListMsaa.ItemsSource = _msaaRows;
        TreeLocator.ItemsSource = _locatorTree;
    }
	
	private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
			{
				if (e.ClickCount == 2)
				{
					ToggleMaximize();
					return;
				}
				DragMove();
			}

	private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

	private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

	private void BtnMaximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

	private void ToggleMaximize()
	{
		WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
	}

    void Register()
    {
		if (_source != null) return;
		var hwnd = new WindowInteropHelper(this).Handle;

		_registered = RegisterHotKey(hwnd, HOTKEY_ID, MOD_CONTROL | MOD_SHIFT, VK_F);
		if (!_registered)
			MessageBox.Show("Хоткей Alt+Shift+F не зарегистрировался (занят или нет прав).", "Hotkey");

		_source = HwndSource.FromHwnd(hwnd);
		_source?.AddHook(WndProc);
    }

	void Unregister()
	{
		var hwnd = new WindowInteropHelper(this).Handle;

		if (_source != null)
		{
			_source.RemoveHook(WndProc);
			_source = null;
		}

		if (_registered)
		{
			UnregisterHotKey(hwnd, HOTKEY_ID);
			_registered = false;
		}
	}

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            try
            {
                var backend = (_vclMode && _vclClient?.IsConnected == true) ? (SpyCapture.IVclBackend)_vclClient : null;
                var el = SpyCapture.CaptureUnderCursor(backend);
                _current = el;

                Dictionary<string, string>? props = null;
                if (backend != null && el.VclChain is { Count: > 0 })
                    props = backend.GetProperties(el.Hwnd);

                VclBridgeClient.VclHitTestResult? leafHit = null;
                if (_vclMode && _vclClient?.IsConnected == true)
                {
                    GetCursorPos(out var cpt);
                    leafHit = _vclClient.TryHitTestForOverlay(el.Hwnd, cpt.X, cpt.Y);
                }

                UpdateInspector(el, props);
                UpdateHierarchy(el);
                UpdateSelector(el);
                AppendDiagnosticBlock(el, leafHit, props);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Spy error");
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    void UpdateInspector(SpyElement e, Dictionary<string, string>? vclProps)
    {
        _objectRows.Clear();
        _vclRows.Clear();
        _vclChainLines.Clear();
        _win32Rows.Clear();
        _uiaRows.Clear();
        _msaaRows.Clear();
        _locatorTree.Clear();
        TxtLocatorJson.Text = "";
        _propsClipboardText = "";

        void AddRow(ObservableCollection<Row> target, string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            target.Add(new Row(key, value.Trim()));
        }

        AddRow(_objectRows, "ProcessName", e.ProcessName);
        AddRow(_objectRows, "ProcessId", e.ProcessId.ToString());
        AddRow(_objectRows, "HWND", $"0x{e.Hwnd.ToInt64():X}");
        AddRow(_objectRows, "Rectangle", e.Rect.ToString());
        AddRow(_objectRows, "ProcessPath", e.ProcessPath);

        if (e.VclChain is { Count: > 0 })
        {
            var leaf = e.VclChain[^1];
            var vclDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (vclProps != null)
            {
                foreach (var kvp in vclProps)
                {
                    if (string.IsNullOrWhiteSpace(kvp.Key) || string.IsNullOrWhiteSpace(kvp.Value))
                        continue;
                    vclDict[kvp.Key.Trim()] = kvp.Value.Trim();
                }
            }

            var classValue = vclDict.TryGetValue("ClassName", out var classNameProp) ? classNameProp : leaf.ClassName;
            var nameValue = vclDict.TryGetValue("Name", out var nameProp) ? nameProp : leaf.ComponentName;
            AddRow(_vclRows, "VCL.ClassName", classValue);
            AddRow(_vclRows, "VCL.Name", nameValue);
            AddRow(_vclRows, "VCL.NameCandidate", e.VclNameCandidate);

            foreach (var n in e.VclChain)
            {
                var line = !string.IsNullOrWhiteSpace(n.ComponentName) ? n.ComponentName : n.ClassName;
                if (!string.IsNullOrWhiteSpace(line))
                    _vclChainLines.Add(line);
            }

            if (vclProps != null)
            {
                foreach (var kvp in vclProps.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    if (kvp.Key.Equals("ClassName", StringComparison.OrdinalIgnoreCase)) continue;
                    if (kvp.Key.Equals("Name", StringComparison.OrdinalIgnoreCase)) continue;
                    AddRow(_vclRows, $"VCL.{kvp.Key}", kvp.Value);
                }
            }
        }

        AddRow(_win32Rows, "Win32Class", e.Win32Class);
        AddRow(_win32Rows, "Win32Text", e.Win32Text);

        AddRow(_uiaRows, "UIA.ControlType", e.UiaControlType);
        AddRow(_uiaRows, "UIA.AutomationId", e.UiaAutomationId);
        AddRow(_uiaRows, "UIA.FrameworkId", e.UiaFrameworkId);
        AddRow(_uiaRows, "UIA.ClassName", e.UiaClassName);
        AddRow(_uiaRows, "UIA.Name", e.UiaName);

        AddRow(_msaaRows, "MSAA.Role", e.MsaaRole);
        AddRow(_msaaRows, "MSAA.State", e.MsaaState);
        AddRow(_msaaRows, "MSAA.Name", e.MsaaName);

        ExpVcl.Visibility = e.VclChain is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        ExpWindow.Visibility = _win32Rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExpUia.Visibility = _uiaRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExpMsaa.Visibility = _msaaRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        try
        {
            var structured = Spy.Core.StructuredLocatorBuilder.Build(e);
            var opts = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            var json = JsonSerializer.Serialize(structured, opts);
            TxtLocatorJson.Text = json;
            BuildJsonTree(json);
            ExpLocator.Visibility = Visibility.Visible;
        }
        catch
        {
            ExpLocator.Visibility = Visibility.Collapsed;
        }

        var sb = new StringBuilder();

        void AppendSection(string title, IEnumerable<Row> rows)
        {
            var list = rows.ToList();
            if (list.Count == 0) return;
            sb.AppendLine(title);
            sb.AppendLine(new string('-', title.Length));
            foreach (var r in list)
                sb.AppendLine($"{r.Key}: {r.Value}");
            sb.AppendLine();
        }

        AppendSection("OBJECT", _objectRows);

        if (e.VclChain is { Count: > 0 })
        {
            sb.AppendLine("VCL OBJECT");
            sb.AppendLine("----------");
            foreach (var r in _vclRows)
                sb.AppendLine($"{r.Key}: {r.Value}");
            if (_vclChainLines.Count > 0)
            {
                sb.AppendLine("VCL.ChainPreview:");
                foreach (var line in _vclChainLines)
                    sb.AppendLine($"  {line}");
            }
            sb.AppendLine();
        }

        AppendSection("WINDOW", _win32Rows);
        AppendSection("UI AUTOMATION", _uiaRows);
        AppendSection("MSAA", _msaaRows);

        if (!string.IsNullOrWhiteSpace(TxtLocatorJson.Text))
        {
            sb.AppendLine("LOCATOR PREVIEW");
            sb.AppendLine("--------------");
            sb.AppendLine(TxtLocatorJson.Text);
            sb.AppendLine();
        }

        _propsClipboardText = sb.ToString();
    }

    void BuildJsonTree(string json)
    {
        _locatorTree.Clear();
        using var doc = JsonDocument.Parse(json);
        var root = new JsonNodeVm("locator", "", Brushes.White, new List<JsonNodeVm>());
        PopulateJsonChildren(root, doc.RootElement);
        _locatorTree.Add(root);
    }

    void PopulateJsonChildren(JsonNodeVm parent, JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    var node = CreateJsonNode(prop.Name, prop.Value);
                    parent.Children.Add(node);
                }
                break;
            case JsonValueKind.Array:
                int idx = 0;
                foreach (var item in el.EnumerateArray())
                {
                    var node = CreateJsonNode($"[{idx}]", item);
                    parent.Children.Add(node);
                    idx++;
                }
                break;
        }
    }

    JsonNodeVm CreateJsonNode(string key, JsonElement el)
    {
        if (el.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            var node = new JsonNodeVm(key, "", Brushes.White, new List<JsonNodeVm>());
            PopulateJsonChildren(node, el);
            return node;
        }

        string value;
        Brush brush;
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                value = $"\"{el.GetString()}\"";
                brush = Brushes.LightGreen;
                break;
            case JsonValueKind.Number:
                value = el.GetRawText();
                brush = Brushes.LightSkyBlue;
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                value = el.GetRawText();
                brush = Brushes.Khaki;
                break;
            case JsonValueKind.Null:
                value = "null";
                brush = Brushes.Gray;
                break;
            default:
                value = el.GetRawText();
                brush = Brushes.White;
                break;
        }

        return new JsonNodeVm(key, value, brush, new List<JsonNodeVm>());
    }
	
		private void StartSpyMode_MinimizeApp()
		{
			if (_spyActive) return;
			_spyActive = true;

			// 1) свернуть/спрятать окно на время захвата
			WindowState = WindowState.Minimized;
			Hide();
			// 2) overlay поверх всего экрана
			_overlay ??= new OverlayWindow();
			_overlay.FadeIn();
			_overlay.Activate();

			// 3) глобальный хук мыши
			_mouseHook?.Dispose();
			_mouseHook = new GlobalMouseHook();
			_mouseHook.MouseMove += OnSpyMouseMove;
			_mouseHook.LeftUp += OnSpyLeftUp;
			_mouseHook.Start();
			System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Cross;
            _lastVclOverlayTick = 0;
            _vclOverlayFailCount = 0;
            _vclOverlayBackoffUntil = 0;
            _vclOverlayIntervalMs = 130;
		}

		private void StopSpyMode_RestoreApp()
		{
			_mouseHook?.Dispose();
			_mouseHook = null;

			_overlay?.HideRect();
			_overlay?.FadeOut();

			// вернуть окно
			WindowState = WindowState.Normal;
			Show();
			Activate();
			Topmost = true;   // чтобы точно поднялось
			Topmost = false;
			
			System.Windows.Input.Mouse.OverrideCursor = null;
			_spyActive = false;
		}

		private void OnSpyMouseMove(int x, int y)
		{
			// вызывается НЕ из UI потока -> через Dispatcher
			Dispatcher.BeginInvoke(() =>
			{
                var now = Environment.TickCount64;
                if (now - _lastHighlightTick < 70)
                    return;
                _lastHighlightTick = now;

                var el = Spy.Core.SpyCapture.CaptureWin32UnderCursor();
				_current = el;
                if (el.ProcessId == Process.GetCurrentProcess().Id)
                {
                    _overlay?.HideRect();
                    return;
                }

                var vclShown = false;
                if (el.UiaBoundingRect is { } r && !r.IsEmpty)
                {
                    _overlay?.ShowRectFromWpf(r.X, r.Y, r.Width, r.Height);
                }
                else
                    _overlay?.ShowRectFromDevice(el.Rect.Left, el.Rect.Top, el.Rect.Width, el.Rect.Height);

                if (_vclMode && _vclClient?.IsConnected == true)
                {
                    var canProbeByPid = _targetPid <= 0 || el.ProcessId == _targetPid;
                    var canProbeByTime = now - _lastVclOverlayTick >= _vclOverlayIntervalMs;
                    if (canProbeByPid && now >= _vclOverlayBackoffUntil && canProbeByTime)
                    {
                        _lastVclOverlayTick = now;
                        try
                        {
                            var hit = _vclClient.TryHitTestForOverlay(el.Hwnd, x, y);
                            if (hit?.ok == true && hit.hit &&
                                hit.right > hit.left && hit.bottom > hit.top &&
                                hit.right - hit.left < 50000 && hit.bottom - hit.top < 50000)
                            {
                                _overlay?.ShowRectFromDevice(hit.left, hit.top, hit.right - hit.left, hit.bottom - hit.top);
                                vclShown = true;
                                _vclOverlayFailCount = 0;
                                _vclOverlayIntervalMs = 130;
                            }
                            else
                            {
                                _vclOverlayFailCount++;
                            }
                        }
                        catch
                        {
                            _vclOverlayFailCount++;
                        }

                        if (_vclOverlayFailCount >= 3)
                        {
                            _vclOverlayBackoffUntil = now + 800;
                            _vclOverlayIntervalMs = 180;
                            _vclOverlayFailCount = 0;
                        }
                    }
                }

                _ = vclShown;
			});
		}

    private void OnSpyLeftUp()
    {
        Dispatcher.BeginInvoke(() =>
        {
            StopSpyMode_RestoreApp();
            try
            {
                // Pass VCL client to Capture
                // Need to pass it only if valid
                var backend = (_vclMode && _vclClient?.IsConnected == true) ? (SpyCapture.IVclBackend)_vclClient : null;
                var el = SpyCapture.CaptureUnderCursor(backend);
                _current = el;

                Dictionary<string, string>? props = null;
                VclBridgeClient.VclHitTestResult? leafHit = null;

                if (el.VclChain != null && el.VclChain.Count > 0)
                {
                    // Try to fetch properties for deep leaf via hit_test result self
                    if (_vclMode && _vclClient?.IsConnected == true)
                    {
                        GetCursorPos(out var cpt);
                        leafHit = _vclClient.TryHitTestForOverlay(el.Hwnd, cpt.X, cpt.Y);
                        if (leafHit?.ok == true && leafHit.hit && !string.IsNullOrWhiteSpace(leafHit.self))
                        {
                            props = _vclClient.GetProperties(leafHit.self);
                        }
                    }
                    // Fallback: by hwnd -> resolves container self
                    if (props == null && backend != null)
                    {
                        props = backend.GetProperties(el.Hwnd);
                    }
                }
                
                UpdateInspector(el, props);
                UpdateHierarchy(el);
                UpdateSelector(el);
                AppendDiagnosticBlock(el, leafHit, props);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Capture error");
            }
        });
    }
		
	private void BtnSpy_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
	{
		e.Handled = true;            // чтобы кнопка не "отжималась" и не делала лишнего
		StartSpyMode_MinimizeApp();  // стартуем сразу
	}

    private void AboutLabel_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (MainTabs != null)
            MainTabs.SelectedIndex = 1; // About
    }

    private void MainLabel_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (MainTabs != null)
            MainTabs.SelectedIndex = 0; // Main
    }
		
    public record Row(string Key, string Value);

    private void AppendDiagText(string text)
    {
        if (!_fileLoggingEnabled || string.IsNullOrWhiteSpace(text))
            return;
        try
        {
            lock (_logSync)
            {
                var dir = Path.GetDirectoryName(_logFilePath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(_logFilePath, text, Encoding.UTF8);
            }
        }
        catch { }
    }

    private static bool IsGraphicLikeClass(string? className)
    {
        if (string.IsNullOrWhiteSpace(className))
            return false;
        var c = className;
        return c.Contains("Graphic", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("Label", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("SpeedButton", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("Image", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("PaintBox", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("Shape", StringComparison.OrdinalIgnoreCase);
    }

    private void AppendDiagnosticBlock(SpyElement e, VclBridgeClient.VclHitTestResult? leafHit, Dictionary<string, string>? props)
    {
        if (e.ProcessId == Process.GetCurrentProcess().Id)
            return;
        var selectedHwndHex = $"0x{e.Hwnd.ToInt64():X8}";
        var selectedClass = string.IsNullOrWhiteSpace(e.Win32Class) ? "Unknown" : e.Win32Class!;
        var targetHex = leafHit?.left != 0 || leafHit?.top != 0 ? selectedHwndHex : selectedHwndHex;
        var targetClass = !string.IsNullOrWhiteSpace(leafHit?.@class) ? leafHit!.@class! : selectedClass;
        var instance = !string.IsNullOrWhiteSpace(leafHit?.self) ? leafHit!.self! : "n/a";

        int childCandidates = 0;
        bool hasGraphicDescendants = false;
        if (_vclClient?.IsConnected == true && !string.IsNullOrWhiteSpace(leafHit?.self))
        {
            try
            {
                var children = _vclClient.GetChildrenForEngine(leafHit.self!);
                childCandidates = children.Count;
                hasGraphicDescendants = children.Any(c => IsGraphicLikeClass(c.@class));
            }
            catch
            {
                childCandidates = 0;
                hasGraphicDescendants = false;
            }
        }

        string classChain;
        if (e.VclChain is { Count: > 0 })
            classChain = string.Join(" -> ", e.VclChain.Select(n => n.ClassName));
        else
            classChain = targetClass;

        var processBitness = "unknown";
        var baseDir = AppContext.BaseDirectory;
        var loader = System.IO.Path.Combine(baseDir, Environment.Is64BitProcess ? "VclHook64.dll" : "VclHook32.dll");
        var hookState = _vclHookManager.IsRunning ? "ON" : "OFF";
        var hookDetails = string.IsNullOrWhiteSpace(_vclHookManager.LastError) ? "ok" : _vclHookManager.LastError!;
        var remoteHookLoaded = false;
        if (_targetPid > 0)
        {
            processBitness = _vclHookManager.IsTarget64Bit(_targetPid) ? "x64" : "x86";
            remoteHookLoaded = _vclHookManager.IsHookDllLoaded();
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Target: {targetHex} [{targetClass}]");
        sb.AppendLine($"Instance: {instance}");
        sb.AppendLine(string.IsNullOrWhiteSpace(SelectorText.Text) ? "n/a" : SelectorText.Text);
        sb.AppendLine($"Class chain: {classChain}");
        sb.AppendLine($"Best-effort graphic scan: child candidates={childCandidates}");
        sb.AppendLine(hasGraphicDescendants
            ? "  TGraphicControl-like descendants detected."
            : "  No TGraphicControl-like descendants detected.");
        sb.AppendLine($"Hook diagnostics: state={hookState}; details={hookDetails}; loader={loader}; basedir={baseDir}");
        sb.AppendLine($"Target process bitness: {processBitness}");
        sb.AppendLine("Force inject: n/a");
        sb.AppendLine($"Remote modules: VclHook={remoteHookLoaded}; VclDelphiHook=n/a; VclVBHook=n/a; VclOpenAppHook=n/a");
        sb.AppendLine($"Window props: {(props is { Count: > 0 } ? "available" : "none")}");
        sb.AppendLine($"Non-HWND control candidate (TGraphicControl path): {IsGraphicLikeClass(targetClass)}");
        sb.AppendLine($"Selected HWND: {selectedHwndHex} [{selectedClass}]");
        sb.AppendLine();

        AppendDiagText(sb.ToString());
    }

    sealed class JsonNodeVm
    {
        public string Key { get; }
        public string Value { get; }
        public string Separator => string.IsNullOrEmpty(Value) && Children.Count > 0 ? "" : ": ";
        public Brush ValueBrush { get; }
        public List<JsonNodeVm> Children { get; }

        public JsonNodeVm(string key, string value, Brush valueBrush, List<JsonNodeVm> children)
        {
            Key = key;
            Value = value;
            ValueBrush = valueBrush;
            Children = children;
        }
    }

    void UpdateHierarchy(SpyElement e)
    {
        TreeHierarchy.Items.Clear();

        var models = Spy.Core.HierarchyBuilder.Build(e);

        int index = 0;
        foreach (var model in models)
        {
            string icon = model.Source switch
            {
                Spy.Core.HierarchySource.Uia => "",
                Spy.Core.HierarchySource.Msaa => "",
                _ => ""
            };

            var item = new TreeViewItem
            {
                Header = new HierarchyNode(index, icon, model.Label),
                IsExpanded = true
            };

            TreeHierarchy.Items.Add(item);
            index++;
        }

        if (TreeHierarchy.Items.Count > 0 && TreeHierarchy.Items[^1] is TreeViewItem last)
        {
            last.IsSelected = true;
            last.BringIntoView();
        }
    }
	
    void UpdateSelector(SpyElement e)
    {
        try
        {
            // Генератор селектора
            SelectorText.Text = Spy.Core.SelectorBuilder.Build(e);
        }
        catch
        {
            SelectorText.Text = string.Empty;
        }
    }

    private void BtnCopySelector_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(SelectorText.Text))
                Clipboard.SetText(SelectorText.Text);
        }
        catch
        {
            // ignore clipboard errors
        }
    }

    private void BtnCopyLocatorJson_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null)
        {
            MessageBox.Show("Сначала захватите элемент.", "Copy locator JSON", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var structured = Spy.Core.StructuredLocatorBuilder.Build(_current);

            if (_vclMode && _vclClient?.IsConnected == true && structured.VclChain is { Count: > 1 })
            {
                var stable = structured.VclChain.ToList();

                if (_current.VclChain is { Count: > 0 } && _current.VclChain.Count == stable.Count)
                {
                    stable = new List<string>(stable.Count);
                    for (int i = 0; i < _current.VclChain.Count; i++)
                    {
                        var n = _current.VclChain[i];
                        if (i == 0 && !string.IsNullOrWhiteSpace(n.ComponentName))
                        {
                            stable.Add(n.ComponentName);
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(n.ComponentName) && !VclSmartLocatorEngine.LooksUnstableName(n.ComponentName))
                            stable.Add(n.ComponentName);
                        else
                            stable.Add(n.ClassName);
                    }
                }

                var introspector = new VclBridgeIntrospector(_vclClient, _current.ProcessId);
                var engine = new VclSmartLocatorEngine(introspector);
                var optimized = engine.OptimizeVclChain(stable);
                structured = structured with { VclChain = optimized };
            }

            var json = LocatorStorage.SerializeLocatorOnly(structured);
            using var _ = JsonDocument.Parse(json);
            Clipboard.SetText(json);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Copy locator JSON error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // --- Locator file management ---

    private string? _currentLocatorFilePath;

    void UpdateLocatorFileLabel()
    {
        if (LocatorFileLabel == null)
            return;

        LocatorFileLabel.Text =
            string.IsNullOrWhiteSpace(_currentLocatorFilePath)
                ? "Current locator file: (none)"
                : $"Current locator file: {_currentLocatorFilePath}";
    }

    private void BtnCreateLocatorFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "JSON files (*.json)|*.json",
            DefaultExt = ".json",
            FileName = "locators.json"
        };

        if (dlg.ShowDialog() != true)
            return;

        _currentLocatorFilePath = dlg.FileName;

        var model = new LocatorStorage.LocatorFileModel();

        LocatorStorage.Save(_currentLocatorFilePath, model);
        UpdateLocatorFileLabel();
    }

    private void BtnAddSelector_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentLocatorFilePath))
        {
            MessageBox.Show("Choose locator file first.", "Locator file", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_current == null)
        {
            MessageBox.Show("Сначала захватите элемент.", "Add selector", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new AddSelectorWindow(SelectorText.Text) { Owner = this };
        if (dlg.ShowDialog() != true)
            return;

        var model = LocatorStorage.Load(_currentLocatorFilePath);
        try
        {
            var structured = Spy.Core.StructuredLocatorBuilder.Build(_current);

            if (_vclMode && _vclClient?.IsConnected == true && structured.VclChain is { Count: > 1 })
            {
                var stable = structured.VclChain.ToList();

                if (_current.VclChain is { Count: > 0 } && _current.VclChain.Count == stable.Count)
                {
                    stable = new List<string>(stable.Count);
                    for (int i = 0; i < _current.VclChain.Count; i++)
                    {
                        var n = _current.VclChain[i];
                        if (i == 0 && !string.IsNullOrWhiteSpace(n.ComponentName))
                        {
                            stable.Add(n.ComponentName);
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(n.ComponentName) && !VclSmartLocatorEngine.LooksUnstableName(n.ComponentName))
                            stable.Add(n.ComponentName);
                        else
                            stable.Add(n.ClassName);
                    }
                }

                var introspector = new VclBridgeIntrospector(_vclClient, _current.ProcessId);
                var engine = new VclSmartLocatorEngine(introspector);
                var optimized = engine.OptimizeVclChain(stable);
                structured = structured with { VclChain = optimized };
            }

            var human = Spy.Core.StructuredLocatorBuilder.BuildHumanSelector(_current, structured);

            model.Elements[dlg.DisplayName] = new LocatorStorage.LocatorElement
            {
                HumanSelector = human,
                Locator = structured,
                // оставляем старое поле пустым для новых записей
                Selector = null,
                Description = dlg.Description,
                UpdatedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Build locator error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        LocatorStorage.Save(_currentLocatorFilePath, model);
    }

    private void BtnManageLocators_Click(object sender, RoutedEventArgs e)
    {
        // Если файл ещё не выбран – даём выбрать существующий JSON
        if (string.IsNullOrWhiteSpace(_currentLocatorFilePath))
        {
            var dlg = new OpenFileDialog
            {
                Filter = "JSON files (*.json)|*.json",
                DefaultExt = ".json"
            };

            if (dlg.ShowDialog() == true)
            {
                _currentLocatorFilePath = dlg.FileName;
                UpdateLocatorFileLabel();
            }
            else
            {
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(_currentLocatorFilePath))
        {
            var win = new LocatorManagerWindow(_currentLocatorFilePath) { Owner = this };
            win.ShowDialog();
        }
    }
	
	
	protected override void OnClosed(EventArgs e)
	{
		try
		{
			_mouseHook?.Dispose();
			_overlay?.Close();
            _vclClient?.Dispose();
            _vclHookManager?.Stop();
		}
		catch { }

		base.OnClosed(e);
		Environment.Exit(0); // гарантированное завершение процесса
	}

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private void ChkVclMode_Checked(object sender, System.Windows.RoutedEventArgs e)
    {
        _vclMode = true;
        // _vclClient ??= new VclBridgeClient(); // Don't create client until PID is selected
        UpdateVclStatus();
        if (BtnSelectApp != null) BtnSelectApp.Visibility = Visibility.Visible;
    }

    private void ChkVclMode_Unchecked(object sender, System.Windows.RoutedEventArgs e)
    {
        _vclMode = false;
        UpdateVclStatus();
        if (BtnSelectApp != null) BtnSelectApp.Visibility = Visibility.Collapsed;
    }

    private void BtnSelectApp_Click(object sender, RoutedEventArgs e)
    {
        var picker = new ProcessPickerWindow { Owner = this };
        if (picker.ShowDialog() == true && picker.SelectedPid is int pid)
        {
            _targetPid = pid;
            var name = picker.SelectedProcessName;
            //LblAppInfo.Text = $"App: {name} ({_targetPid})";
            
            // Запуск VCL Helper
            if (_vclMode)
            {
                TryStartVclHelper(_targetPid);
            }
        }
    }

    private void UpdateVclStatus()
    {
        bool connected = _vclMode && (_vclClient?.IsConnected ?? false);
        bool hooked = _vclHookManager.IsRunning;
        
        if (!_vclMode)
        {
            LblVclStatus.Text = "VCL: Off";
            LblVclStatus.Foreground = System.Windows.Media.Brushes.Gray;
        }
        else if (_targetPid == 0)
        {
            LblVclStatus.Text = "VCL: Enabled (select target app...)";
            LblVclStatus.Foreground = System.Windows.Media.Brushes.Orange;
        }
        else if (_vclHookManager.LastError != null)
        {
            LblVclStatus.Text = $"VCL: Error: {_vclHookManager.LastError}";
            LblVclStatus.Foreground = System.Windows.Media.Brushes.Red;
        }
        else if (!hooked)
        {
            LblVclStatus.Text = "VCL: Installing hook...";
            LblVclStatus.Foreground = System.Windows.Media.Brushes.Orange;
        }
        else if (!connected)
        {
            LblVclStatus.Text = "VCL: Hook installed, waiting for pipe...";
            LblVclStatus.Foreground = System.Windows.Media.Brushes.Orange;
        }
        else
        {
            LblVclStatus.Text = "VCL: On (connected)";
            LblVclStatus.Foreground = System.Windows.Media.Brushes.LimeGreen;
        }
    }

    private void TryStartVclHelper(int pid)
    {
        // Try to start hook
        bool started = _vclHookManager.Start(pid);
        UpdateVclStatus();
        
        if (!started)
        {
            // Abort if Start failed (e.g. arch mismatch or dll not found)
            return;
        }
        
        // Start client
        _vclClient?.Dispose();
        _vclClient = new VclBridgeClient(pid);
        
        // Timer to check pipe status
        var t = new System.Windows.Threading.DispatcherTimer();
        t.Interval = TimeSpan.FromMilliseconds(200);
        int attempts = 0;
        t.Tick += (s, e) =>
        {
            if (!_vclMode || _targetPid != pid) { t.Stop(); return; }
            
            if (_vclClient.Connect(100))
            {
                t.Stop();
                UpdateVclStatus();
            }
            else
            {
                attempts++;
                if (attempts > 20) 
                {
                    t.Stop();
                    // Check if DLL is loaded
                    if (!_vclHookManager.IsHookDllLoaded())
                    {
                        // Update status manually if needed, but hook manager error should cover it
                        LblVclStatus.Text = "VCL: Error: Hook DLL not loaded into target";
                        LblVclStatus.Foreground = System.Windows.Media.Brushes.Red;
                    }
                    else
                    {
                        UpdateVclStatus();
                    }
                }
            }
        };
        t.Start();
        
        UpdateVclStatus();
    }

    private void BtnCopyProps_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_propsClipboardText)) return;
        try { Clipboard.SetText(_propsClipboardText); } catch { }
    }

    private void ChkFileLogging_Checked(object sender, RoutedEventArgs e)
    {
        _fileLoggingEnabled = true;
        VclBridgeClient.SetFileLoggingEnabled(true);
        AppendDiagText($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [Spyder] file logging enabled{Environment.NewLine}");
    }

    private void ChkFileLogging_Unchecked(object sender, RoutedEventArgs e)
    {
        AppendDiagText($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [Spyder] file logging disabled{Environment.NewLine}");
        _fileLoggingEnabled = false;
        VclBridgeClient.SetFileLoggingEnabled(false);
    }
}
