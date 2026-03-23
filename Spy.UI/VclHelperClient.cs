/*
Copyright 2026 Daniyar Sagatov
Licensed under the Apache License 2.0
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Linq;
using Spy.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;
namespace Spy.UI;

internal sealed class VclBridgeClient : IDisposable, SpyCapture.IVclBackend
{
    readonly string _pipeName;
    readonly int? _targetPid;
    NamedPipeClientStream? _pipe;
    readonly JsonSerializerOptions _json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    readonly Dictionary<long, VclResolveResult> _resolveCache = new();
    readonly Queue<long> _resolveCacheOrder = new();
    readonly Dictionary<string, List<VclChildItem>> _childrenCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Queue<string> _childrenCacheOrder = new();
    long _lastOverlayHitTicks = 0;
    static readonly bool s_vclTrace = string.Equals(Environment.GetEnvironmentVariable("SPYDER_VCL_TRACE"), "1", StringComparison.Ordinal);
    static readonly object s_vclLogSync = new();
    static volatile bool s_fileLogEnabled;
    static readonly string s_vclLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Spyder", "logs", "spyder.log");

    public static void SetFileLoggingEnabled(bool enabled)
    {
        s_fileLogEnabled = enabled;
    }

    static void VclLog(string message)
    {
        if (s_vclTrace) Trace.WriteLine(message);
        if (!s_fileLogEnabled) return;
        try
        {
            lock (s_vclLogSync)
            {
                var dir = Path.GetDirectoryName(s_vclLogPath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(s_vclLogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
        }
    }

    static bool IsNoiseVclClass(string? cls)
    {
        if (string.IsNullOrWhiteSpace(cls)) return true;
        return cls.Equals("TApplication", StringComparison.OrdinalIgnoreCase) ||
               cls.Equals("TMenuItem", StringComparison.OrdinalIgnoreCase) ||
               cls.Equals("TMainMenu", StringComparison.OrdinalIgnoreCase) ||
               cls.Equals("TPopupMenu", StringComparison.OrdinalIgnoreCase) ||
               cls.Equals("TExMainMenu", StringComparison.OrdinalIgnoreCase) ||
               cls.Equals("TExPopupMenu", StringComparison.OrdinalIgnoreCase) ||
               cls.Equals("TTimer", StringComparison.OrdinalIgnoreCase) ||
               cls.Equals("TAction", StringComparison.OrdinalIgnoreCase) ||
               cls.Equals("TActionList", StringComparison.OrdinalIgnoreCase);
    }

    static List<SpyVclNode> ToChain(List<VclPathNode> path)
    {
        return path
            .Where(p => !IsNoiseVclClass(p.@class))
            .Select(p => new SpyVclNode(p.@class!, string.IsNullOrWhiteSpace(p.name) ? null : p.name))
            .ToList();
    }

    static bool LeafMatches(SpyVclNode last, string? leafClass, string? leafName)
    {
        if (string.IsNullOrWhiteSpace(leafClass)) return false;
        if (!string.Equals(last.ClassName, leafClass, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(leafName))
            return string.Equals(last.ComponentName ?? "", leafName, StringComparison.OrdinalIgnoreCase);
        return true;
    }

    static bool PathEndsWithSelf(List<VclPathNode> path, string leafSelf)
    {
        if (path.Count == 0) return false;
        var lastSelf = path[^1].self;
        if (string.IsNullOrWhiteSpace(lastSelf)) return false;
        return string.Equals(lastSelf, leafSelf, StringComparison.OrdinalIgnoreCase);
    }

    List<VclPathNode>? BuildPathViaChildrenGraph((IntPtr hwnd, string self, string cls, string? name) root, string leafSelf, int maxNodes)
    {
        if (string.IsNullOrWhiteSpace(root.self) || string.IsNullOrWhiteSpace(leafSelf))
            return null;

        if (string.Equals(root.self, leafSelf, StringComparison.OrdinalIgnoreCase))
        {
            return new List<VclPathNode>
            {
                new VclPathNode { self = root.self, @class = root.cls, name = root.name }
            };
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.self };
        var parentBySelf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nodeBySelf = new Dictionary<string, VclPathNode>(StringComparer.OrdinalIgnoreCase)
        {
            [root.self] = new VclPathNode { self = root.self, @class = root.cls, name = root.name }
        };

        var q = new Queue<string>();
        q.Enqueue(root.self);
        var processed = 0;
        var found = false;

        while (q.Count > 0 && processed < maxNodes && !found)
        {
            var cur = q.Dequeue();
            processed++;
            var children = GetChildren(cur);
            if (children == null) continue;

            foreach (var ch in children)
            {
                if (string.IsNullOrWhiteSpace(ch.self)) continue;
                if (!visited.Add(ch.self)) continue;

                parentBySelf[ch.self] = cur;
                nodeBySelf[ch.self] = new VclPathNode { self = ch.self, @class = ch.@class, name = ch.name };

                if (string.Equals(ch.self, leafSelf, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
                q.Enqueue(ch.self);
            }
        }

        if (!found) return null;

        var rev = new List<VclPathNode>();
        var curSelf = leafSelf;
        while (!string.IsNullOrWhiteSpace(curSelf))
        {
            if (nodeBySelf.TryGetValue(curSelf, out var node))
                rev.Add(node);
            if (string.Equals(curSelf, root.self, StringComparison.OrdinalIgnoreCase))
                break;
            if (!parentBySelf.TryGetValue(curSelf, out var p))
                break;
            curSelf = p;
        }

        rev.Reverse();
        if (rev.Count == 0) return null;
        if (!string.Equals(rev[^1].self, leafSelf, StringComparison.OrdinalIgnoreCase)) return null;
        return rev;
    }

    static List<VclPathNode> BuildPathFromHwndVclChain(List<(IntPtr hwnd, string self, string cls, string? name)> chain, string leafSelf, string leafClass, string? leafName)
    {
        var path = new List<VclPathNode>();
        foreach (var n in chain)
        {
            path.Add(new VclPathNode { self = n.self, @class = n.cls, name = n.name });
        }

        if (path.Count == 0)
            return path;

        var lastSelf = path[^1].self;
        if (string.IsNullOrWhiteSpace(lastSelf) || !string.Equals(lastSelf, leafSelf, StringComparison.OrdinalIgnoreCase))
        {
            path.Add(new VclPathNode { self = leafSelf, @class = leafClass, name = leafName });
        }
        return path;
    }

    static List<VclPathNode> SanitizePath(List<VclPathNode> path)
    {
        var result = new List<VclPathNode>(path.Count);
        var seenSelf = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var n in path)
        {
            if (string.IsNullOrWhiteSpace(n.@class) || IsNoiseVclClass(n.@class))
                continue;

            if (!string.IsNullOrWhiteSpace(n.self))
            {
                if (!seenSelf.Add(n.self))
                    continue;
            }

            if (result.Count > 0)
            {
                var prev = result[^1];
                var sameClass = string.Equals(prev.@class, n.@class, StringComparison.OrdinalIgnoreCase);
                var sameName = string.Equals(prev.name ?? "", n.name ?? "", StringComparison.OrdinalIgnoreCase);
                if (sameClass && sameName)
                    continue;
            }
            result.Add(n);
        }

        return result;
    }

    private VclChildItem? TryFindNonHwndChildAtPoint(string parentSelf, int x, int y)
    {
        return null;
    }

    VclHitTestResult? TryFindChildByHit(string parentSelf, IntPtr parentHwnd, int x, int y)
    {
        if (string.IsNullOrWhiteSpace(parentSelf) || parentHwnd == IntPtr.Zero)
            return null;

        var children = GetChildrenForEngine(parentSelf);
        if (children == null || children.Count == 0)
            return null;

        var scanned = 0;
        VclHitTestResult? containerCandidate = null;
        foreach (var ch in children)
        {
            if (string.IsNullOrWhiteSpace(ch.self))
                continue;
            if (scanned++ > 28)
                break;

            var h = HitTestOverlay(parentHwnd, ch.self!, x, y);
            if (h?.ok != true || h.hit != true || string.IsNullOrWhiteSpace(h.self) || string.IsNullOrWhiteSpace(h.@class))
                continue;

            var cls = h.@class!;
            var isContainer =
                cls.Contains("Panel", StringComparison.OrdinalIgnoreCase) ||
                cls.Contains("ToolBar", StringComparison.OrdinalIgnoreCase) ||
                cls.Contains("TabControl", StringComparison.OrdinalIgnoreCase) ||
                cls.Contains("TabSheet", StringComparison.OrdinalIgnoreCase);

            if (!isContainer)
                return h;

            if (containerCandidate == null)
                containerCandidate = h;
        }

        return containerCandidate;
    }

    static string? NormalizeSelf(string? self)
    {
        if (string.IsNullOrWhiteSpace(self)) return null;
        var s = self.Trim();
        if (string.Equals(s, "0x00000000", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(s, "0x0", StringComparison.OrdinalIgnoreCase)) return null;
        return s;
    }

    List<VclPathNode>? BuildPathViaParentSelfChain(string leafSelf, int maxDepth = 24)
    {
        var cur = NormalizeSelf(leafSelf);
        if (cur == null) return null;

        var rev = new List<VclPathNode>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < maxDepth; i++)
        {
            if (cur == null || !visited.Add(cur))
                break;

            var fp = GetFullProperties(cur);
            if (fp == null || fp.ok != true || string.IsNullOrWhiteSpace(fp.@class))
                break;

            rev.Add(new VclPathNode { self = cur, @class = fp.@class, name = fp.name });
            var parent = NormalizeSelf(fp.parent_self);
            if (parent == null || string.Equals(parent, cur, StringComparison.OrdinalIgnoreCase))
                break;
            cur = parent;
        }

        if (rev.Count == 0) return null;
        rev.Reverse();
        if (!string.Equals(rev[^1].self, leafSelf, StringComparison.OrdinalIgnoreCase))
            return null;
        return rev;
    }

    public bool IsConnected => _pipe?.IsConnected == true;

    public VclBridgeClient(int? pid = null)
    {
        _targetPid = pid;
        _pipeName = pid.HasValue ? $"Spyder.VclHelper.{pid.Value}" : "Spyder.VclHelper";
    }

    public bool Connect(int timeoutMs = 300)
    {
        if (_pipe is { IsConnected: true }) return true;
        _pipe?.Dispose();
        _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var attempts = Math.Max(1, timeoutMs / 200);
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                _pipe.Connect(200);
                _pipe.ReadMode = PipeTransmissionMode.Byte;
                _pipe.ReadTimeout = 250;
                _pipe.WriteTimeout = 250;
                return _pipe.IsConnected;
            }
            catch (IOException)
            {
                // wait and retry
            }
            catch
            {
            }
        }
        return false;
    }

    public VclResolveResult? ResolveByHwnd(IntPtr hwnd)
    {
        if (!IsConnected)
        {
            VclLog($"[VclBridge] ResolveByHwnd: Not connected");
            return null;
        }
        if (_targetPid.HasValue && hwnd != IntPtr.Zero)
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if ((int)pid != _targetPid.Value) return null;
        }

        if (_resolveCache.TryGetValue(hwnd.ToInt64(), out var cached))
            return cached;
        
        var req = JsonSerializer.SerializeToUtf8Bytes(new { cmd = "resolve_by_hwnd", hwnd = hwnd.ToInt64() });
        var bw = new BinaryWriter(_pipe!, Encoding.UTF8, leaveOpen: true);
        var br = new BinaryReader(_pipe!, Encoding.UTF8, leaveOpen: true);
        
        try
        {
            VclLog($"[VclBridge] Sending resolve_by_hwnd for {hwnd.ToInt64()}");
            bw.Write(req.Length);
            bw.Write(req);
            bw.Flush();
            
            int len = br.ReadInt32();
            if (len <= 0)
            {
                VclLog($"[VclBridge] ResolveByHwnd: received invalid len={len}");
                return null;
            }
            
            var bytes = br.ReadBytes(len);
            string raw = Encoding.UTF8.GetString(bytes);
            VclLog($"[VclBridge] ResolveByHwnd response: {raw}");
            
            var res = JsonSerializer.Deserialize<VclResolveResult>(bytes, _json);
            if (res == null)
                VclLog($"[VclBridge] ResolveByHwnd deserialized to NULL");

            if (res != null)
            {
                var key = hwnd.ToInt64();
                _resolveCache[key] = res;
                _resolveCacheOrder.Enqueue(key);
                while (_resolveCacheOrder.Count > 2048)
                {
                    var old = _resolveCacheOrder.Dequeue();
                    _resolveCache.Remove(old);
                }
            }
            return res;
        }
        catch (IOException ex)
        {
            VclLog($"[VclBridge] ResolveByHwnd IOException: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            VclLog($"[VclBridge] ResolveByHwnd Exception: {ex.Message}");
            return null;
        }
    }

    public IReadOnlyList<SpyVclNode>? ResolveVclChain(IntPtr hwnd, int screenX, int screenY, string? vclNameCandidate)
    {
        if (!IsConnected) return null;
        _childrenCache.Clear();
        while (_childrenCacheOrder.Count > 0) _childrenCacheOrder.Dequeue();
        
        VclLog($"[VclBridge] Resolving chain via HWND parent chain for HWND 0x{hwnd.ToInt64():X}");

        var vclHwndChain = new List<(IntPtr hwnd, string self, string cls, string? name)>();

        var current = hwnd;
        int guard = 0;
        while (current != IntPtr.Zero && guard < 64)
        {
            guard++;

            var res = ResolveByHwnd(current);
            if (res?.ok == true && res.is_vcl == true &&
                !string.IsNullOrWhiteSpace(res.vcl_self) &&
                !string.Equals(res.vcl_self, "0x00000000", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(res.vcl_class))
            {
                vclHwndChain.Add((current, res.vcl_self!, res.vcl_class!, res.vcl_name));
            }

            var parent = GetParent(current);
            if (parent == current) break;
            current = parent;
        }

        if (vclHwndChain.Count == 0) return null;

        vclHwndChain.Reverse();

        try
        {
            var rootParent = vclHwndChain[0];
            VclHitTestResult? bestHit = null;
            static int HitScore(string cls)
            {
                if (IsNoiseVclClass(cls)) return 0;
                if (cls.Contains("Panel", StringComparison.OrdinalIgnoreCase) ||
                    cls.Contains("ToolBar", StringComparison.OrdinalIgnoreCase) ||
                    cls.Contains("TabSheet", StringComparison.OrdinalIgnoreCase) ||
                    cls.Contains("TabControl", StringComparison.OrdinalIgnoreCase))
                    return 2;
                return 3;
            }
            var bestScore = -1;
            for (int i = vclHwndChain.Count - 1; i >= 0; i--)
            {
                var c = vclHwndChain[i];
                var h = HitTest(c.hwnd, c.self, screenX, screenY);
                if (h?.ok != true || h.hit != true || string.IsNullOrWhiteSpace(h.self) || string.IsNullOrWhiteSpace(h.@class))
                    continue;

                var cls = h.@class!;
                var score = HitScore(cls);
                if (score <= 0)
                    continue;
                if (score > bestScore)
                {
                    bestHit = h;
                    bestScore = score;
                }
                if (score >= 3)
                    break;
            }

            if ((bestHit?.ok != true || bestHit.hit != true || string.IsNullOrWhiteSpace(bestHit.self) || string.IsNullOrWhiteSpace(bestHit.@class)) &&
                vclHwndChain.Count > 0)
            {
                var byHwnd = ResolveByHwnd(hwnd);
                if (byHwnd?.ok == true && byHwnd.is_vcl == true &&
                    !string.IsNullOrWhiteSpace(byHwnd.vcl_self) &&
                    !string.Equals(byHwnd.vcl_self, "0x00000000", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(byHwnd.vcl_class))
                {
                    bestHit = new VclHitTestResult
                    {
                        ok = true,
                        hit = true,
                        self = byHwnd.vcl_self,
                        @class = byHwnd.vcl_class,
                        name = byHwnd.vcl_name
                    };
                }
            }

            if (bestHit != null &&
                ((bestHit.@class?.Contains("Panel", StringComparison.OrdinalIgnoreCase) ?? false) ||
                 (bestHit.@class?.Contains("ToolBar", StringComparison.OrdinalIgnoreCase) ?? false) ||
                 (bestHit.@class?.Contains("TabControl", StringComparison.OrdinalIgnoreCase) ?? false)))
            {
                var nestedHit = TryFindChildByHit(bestHit.self!, hwnd, screenX, screenY);
                if (nestedHit != null &&
                    !string.IsNullOrWhiteSpace(nestedHit.self) &&
                    ((nestedHit.@class?.Contains("Panel", StringComparison.OrdinalIgnoreCase) ?? false) ||
                     (nestedHit.@class?.Contains("ToolBar", StringComparison.OrdinalIgnoreCase) ?? false) ||
                     (nestedHit.@class?.Contains("TabControl", StringComparison.OrdinalIgnoreCase) ?? false)))
                {
                    var nested2 = TryFindChildByHit(nestedHit.self!, hwnd, screenX, screenY);
                    if (nested2 != null)
                        nestedHit = nested2;
                }
                if (nestedHit != null)
                {
                    bestHit = nestedHit;
                }
                else
                {
                    var childByRect = TryFindNonHwndChildAtPoint(bestHit.self!, screenX, screenY);
                    if (childByRect != null)
                    {
                        bestHit = new VclHitTestResult
                        {
                            ok = true,
                            hit = true,
                            self = childByRect.self,
                            @class = childByRect.@class,
                            name = childByRect.name
                        };
                    }
                }
            }

            string? leafSelf = bestHit?.self;
            string? leafClass = bestHit?.@class;
            string? leafName = bestHit?.name;

            var hitWeak =
                bestHit?.ok != true ||
                bestHit.hit != true ||
                string.IsNullOrWhiteSpace(leafSelf) ||
                string.IsNullOrWhiteSpace(leafClass) ||
                IsNoiseVclClass(leafClass) ||
                string.Equals(leafClass, "TPanel", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(leafClass, "TExPanel", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(leafClass, "TTabSheet", StringComparison.OrdinalIgnoreCase) ||
                (leafClass.StartsWith("Tfrm", StringComparison.OrdinalIgnoreCase) && vclHwndChain.Count > 1) ||
                (vclHwndChain.Count > 1 && string.Equals(leafSelf, rootParent.self, StringComparison.OrdinalIgnoreCase));

            if (hitWeak || string.IsNullOrWhiteSpace(leafSelf))
            {
                var lastHwndNode = vclHwndChain[^1];
                leafSelf = lastHwndNode.self;
                leafClass = lastHwndNode.cls;
                leafName = lastHwndNode.name;
            }

            var hitPath = GetPath(leafSelf);
            if (hitPath == null || hitPath.Count == 0)
                hitPath = BuildPathFromHwndVclChain(vclHwndChain, leafSelf, leafClass!, leafName);
            var pathHasLeafSelf = PathEndsWithSelf(hitPath, leafSelf);

            var leafLooksNonHwnd =
                (leafClass?.Contains("SpeedButton", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (leafClass?.Contains("Graphic", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (leafClass?.Contains("Label", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (leafClass?.Contains("Shape", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (leafClass?.Contains("PaintBox", StringComparison.OrdinalIgnoreCase) ?? false);

            if ((!pathHasLeafSelf || hitPath.Count < 3) && leafLooksNonHwnd)
            {
                var recoveredPath = BuildPathViaChildrenGraph(rootParent, leafSelf, maxNodes: 1200);
                if (recoveredPath != null && recoveredPath.Count > hitPath.Count && PathEndsWithSelf(recoveredPath, leafSelf))
                {
                    hitPath = recoveredPath;
                    pathHasLeafSelf = true;
                    VclLog($"[VclBridge] get_path recovered via children graph, nodes={hitPath.Count}, root={rootParent.self}");
                }
            }

            if (!pathHasLeafSelf || hitPath.Count < 3)
            {
                var hwndPath = BuildPathFromHwndVclChain(vclHwndChain, leafSelf, leafClass!, leafName);
                if (hwndPath.Count > hitPath.Count && PathEndsWithSelf(hwndPath, leafSelf))
                {
                    hitPath = hwndPath;
                    pathHasLeafSelf = true;
                    VclLog($"[VclBridge] get_path recovered via hwnd vcl chain, nodes={hitPath.Count}");
                }
            }

            hitPath = SanitizePath(hitPath);
            var directChain = ToChain(hitPath);
            if (directChain.Count == 0)
                return null;

            var last = directChain[^1];
            if ((!pathHasLeafSelf || !LeafMatches(last, leafClass, leafName)) && !IsNoiseVclClass(leafClass))
            {
                directChain.Add(new SpyVclNode(leafClass!, string.IsNullOrWhiteSpace(leafName) ? null : leafName));
                VclLog($"[VclBridge] chain leaf patched to detected leaf: {leafClass}/{leafName}");
            }

            VclLog($"[VclBridge] Using strict direct hit path, nodes={directChain.Count}");
            return directChain;
        }
        catch (Exception ex)
        {
            VclLog($"[VclBridge] hit_test failed: {ex.Message}");
            return null;
        }
    }

    List<VclPathNode>? GetPath(string self)
    {
        if (!IsConnected) return null;
        if (string.IsNullOrWhiteSpace(self)) return null;

        var req = JsonSerializer.SerializeToUtf8Bytes(new { cmd = "get_path", self });
        var bw = new BinaryWriter(_pipe!, Encoding.UTF8, leaveOpen: true);
        var br = new BinaryReader(_pipe!, Encoding.UTF8, leaveOpen: true);

        bw.Write(req.Length);
        bw.Write(req);
        bw.Flush();

        int len = br.ReadInt32();
        if (len <= 0) return null;

        var bytes = br.ReadBytes(len);
        string raw = Encoding.UTF8.GetString(bytes);
        VclLog($"[VclBridge] get_path response: {raw}");

        var pathRes = JsonSerializer.Deserialize<VclPathResult>(bytes, _json);
        if (pathRes?.ok != true) return null;
        return pathRes.path;
    }

    VclChildItem? FindDescendantByNameOrClass(string rootSelf, string candidate, int maxDepth, int maxNodes)
    {
        var started = Stopwatch.GetTimestamp();
        var budgetTicks = Stopwatch.Frequency / 33;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var q = new Queue<(string self, int depth)>();
        q.Enqueue((rootSelf, 0));
        visited.Add(rootSelf);

        int processed = 0;

        while (q.Count > 0)
        {
            if (Stopwatch.GetTimestamp() - started > budgetTicks) return null;
            var (cur, depth) = q.Dequeue();
            if (depth >= maxDepth) continue;

            var children = GetChildren(cur);
            if (children == null) continue;

            foreach (var ch in children)
            {
                processed++;
                if (processed > maxNodes) return null;

                if (string.IsNullOrWhiteSpace(ch.self)) continue;

                var name = ch.name ?? "";
                var cls = ch.@class ?? "";

                if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return ch;
                }

                if (visited.Add(ch.self))
                {
                    q.Enqueue((ch.self, depth + 1));
                }
            }
        }

        return null;
    }

    List<VclChildItem>? GetChildren(string self)
    {
        if (!IsConnected) return null;
        if (string.IsNullOrWhiteSpace(self)) return null;

        if (_childrenCache.TryGetValue(self, out var cached))
            return cached;

        var req = JsonSerializer.SerializeToUtf8Bytes(new { cmd = "get_children", self });
        var bw = new BinaryWriter(_pipe!, Encoding.UTF8, leaveOpen: true);
        var br = new BinaryReader(_pipe!, Encoding.UTF8, leaveOpen: true);

        bw.Write(req.Length);
        bw.Write(req);
        bw.Flush();

        int len = br.ReadInt32();
        if (len <= 0) return null;

        var bytes = br.ReadBytes(len);
        var raw = Encoding.UTF8.GetString(bytes);
        VclLog($"[VclBridge] get_children response: {raw}");

        var res = JsonSerializer.Deserialize<VclChildrenResult>(bytes, _json);
        if (res?.ok != true) return null;
        var list = res.children ?? new List<VclChildItem>();
        _childrenCache[self] = list;
        _childrenCacheOrder.Enqueue(self);
        while (_childrenCacheOrder.Count > 4096)
        {
            var old = _childrenCacheOrder.Dequeue();
            _childrenCache.Remove(old);
        }
        return list;
    }

    internal IReadOnlyList<VclChildItem> GetChildrenForEngine(string self)
    {
        var list = GetChildren(self);
        return list ?? (IReadOnlyList<VclChildItem>)Array.Empty<VclChildItem>();
    }

    VclHitTestResult? HitTest(IntPtr parentHwnd, string parentSelf, int x, int y)
        => HitTestWithCommand("hit_test", parentHwnd, parentSelf, x, y);

    VclHitTestResult? HitTestOverlay(IntPtr parentHwnd, string parentSelf, int x, int y)
        => HitTestWithCommand("hit_test_overlay", parentHwnd, parentSelf, x, y);

    VclHitTestResult? HitTestWithCommand(string cmd, IntPtr parentHwnd, string parentSelf, int x, int y)
    {
        if (!IsConnected) return null;
        if (parentHwnd == IntPtr.Zero) return null;
        if (string.IsNullOrWhiteSpace(parentSelf)) return null;

        var req = JsonSerializer.SerializeToUtf8Bytes(new { cmd, hwnd = parentHwnd.ToInt64(), self = parentSelf, x, y });
        var bw = new BinaryWriter(_pipe!, Encoding.UTF8, leaveOpen: true);
        var br = new BinaryReader(_pipe!, Encoding.UTF8, leaveOpen: true);

        bw.Write(req.Length);
        bw.Write(req);
        bw.Flush();

        int len = br.ReadInt32();
        if (len <= 0) return null;
        var bytes = br.ReadBytes(len);
        var raw = Encoding.UTF8.GetString(bytes);
        VclLog($"[VclBridge] hit_test response: {raw}");
        return JsonSerializer.Deserialize<VclHitTestResult>(bytes, _json);
    }

    public IReadOnlyList<string>? ResolveChain(IntPtr hwnd)
    {
        var chain = ResolveVclChain(hwnd, 0, 0, null);
        if (chain != null)
        {
            return chain.Select(n => !string.IsNullOrEmpty(n.ComponentName) ? $"{n.ComponentName} ({n.ClassName})" : n.ClassName).ToList();
        }
        return ResolveChainLegacy(hwnd);
    }

    private IReadOnlyList<string>? ResolveChainLegacy(IntPtr hwnd)
    {
        if (!IsConnected) return null;
        var chain = new List<string>();
        var current = hwnd;
        int guard = 0;
        
        while (current != IntPtr.Zero && guard < 32)
        {
            guard++;
            var r = ResolveByHwnd(current);
            if (r == null || r.ok != true) break;
            
            string displayName;
            if (!string.IsNullOrWhiteSpace(r.vcl_name))
                displayName = $"{r.vcl_name} ({r.vcl_class})";
            else if (!string.IsNullOrWhiteSpace(r.vcl_class))
                displayName = r.vcl_class!;
            else
                displayName = "Win32 Window";
                
            chain.Insert(0, displayName);
            
            if (r.parent_hwnd == null || r.parent_hwnd == 0) break;
            current = new IntPtr((long)r.parent_hwnd);
        }
        return chain.Count > 0 ? chain : null;
    }

    public class VclPathResult
    {
        public bool ok { get; set; }
        public string? error { get; set; }
        public List<VclPathNode>? path { get; set; }
    }

    public class VclPathNode
    {
        public string? self { get; set; }
        public string? @class { get; set; }
        public string? name { get; set; }
    }

    [DllImport("user32.dll")]
    static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public class VclChildrenResult
    {
        public bool ok { get; set; }
        public List<VclChildItem>? children { get; set; }
    }

    public class VclChildItem
    {
        public string? self { get; set; }
        public string? @class { get; set; }
        public string? name { get; set; }
    }

    public class VclHitTestResult
    {
        public bool ok { get; set; }
        public bool hit { get; set; }
        public string? self { get; set; }
        public string? @class { get; set; }
        public string? name { get; set; }
        public int left { get; set; }
        public int top { get; set; }
        public int right { get; set; }
        public int bottom { get; set; }
    }

    internal VclHitTestResult? TryHitTestForOverlay(IntPtr hwnd, int x, int y)
    {
        if (!IsConnected) return null;
        if (hwnd == IntPtr.Zero) return null;
        if (_targetPid.HasValue)
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if ((int)pid != _targetPid.Value) return null;
        }
        long now = Stopwatch.GetTimestamp();
        long minDelta = Stopwatch.Frequency / 8;
        if (_lastOverlayHitTicks != 0 && now - _lastOverlayHitTicks < minDelta)
            return null;
        _lastOverlayHitTicks = now;
        var res = ResolveByHwnd(hwnd);
        if (res?.ok != true || res.is_vcl != true) return null;
        if (string.IsNullOrWhiteSpace(res.vcl_self) || string.Equals(res.vcl_self, "0x00000000", StringComparison.OrdinalIgnoreCase))
            return null;
        var hit = HitTestOverlay(hwnd, res.vcl_self!, x, y);
        if (hit?.ok != true || hit.hit != true || string.IsNullOrWhiteSpace(hit.self) || string.IsNullOrWhiteSpace(hit.@class))
            return hit;

        static bool IsContainerClass(string cls) =>
            cls.Contains("Panel", StringComparison.OrdinalIgnoreCase) ||
            cls.Contains("ToolBar", StringComparison.OrdinalIgnoreCase) ||
            cls.Contains("TabControl", StringComparison.OrdinalIgnoreCase) ||
            cls.Contains("TabSheet", StringComparison.OrdinalIgnoreCase);

        if (!IsContainerClass(hit.@class!))
            return hit;

        var nested = TryFindChildByHit(hit.self!, hwnd, x, y);
        if (nested != null && !string.IsNullOrWhiteSpace(nested.self))
        {
            hit = nested;
            if (hit.@class != null && IsContainerClass(hit.@class))
            {
                var nested2 = TryFindChildByHit(hit.self!, hwnd, x, y);
                if (nested2 != null && !string.IsNullOrWhiteSpace(nested2.self))
                    hit = nested2;
            }
        }

        return hit;
    }


    public void Dispose()
    {
        try
        {
            if (_pipe is { IsConnected: true })
            {
                var bw = new BinaryWriter(_pipe, Encoding.UTF8, leaveOpen: true);
                var br = new BinaryReader(_pipe, Encoding.UTF8, leaveOpen: true);
                var req = JsonSerializer.SerializeToUtf8Bytes(new { cmd = "shutdown" });
                bw.Write(req.Length);
                bw.Write(req);
                bw.Flush();
                int len = br.ReadInt32();
                if (len > 0) _ = br.ReadBytes(len);
            }
        }
        catch { }
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
    }

    public class VclResolveResult
    {
        public bool ok { get; set; }
        public string? vcl_name { get; set; }
        public string? vcl_class { get; set; }
        public long? parent_hwnd { get; set; }
        public string? parent_vcl_name { get; set; }
        public bool? is_vcl { get; set; }
        public string? vcl_self { get; set; }
        public string? vcl_parent_self { get; set; }
        public int? confidence { get; set; }
    }
    
    public class VclPropertiesResult
    {
        public bool ok { get; set; }
        public Dictionary<string, string>? properties { get; set; }
    }

    public class VclFullPropertiesResult
    {
        public bool ok { get; set; }
        public string? @class { get; set; }
        public string? name { get; set; }
        public string? parent_self { get; set; }
        public int screen_left { get; set; }
        public int screen_top { get; set; }
        public int screen_right { get; set; }
        public int screen_bottom { get; set; }
    }

    public Dictionary<string, string>? GetProperties(string self)
    {
        if (!IsConnected || string.IsNullOrEmpty(self)) return null;
        
        var req = JsonSerializer.SerializeToUtf8Bytes(new { cmd = "get_properties", self = self });
        var bw = new BinaryWriter(_pipe!, Encoding.UTF8, leaveOpen: true);
        var br = new BinaryReader(_pipe!, Encoding.UTF8, leaveOpen: true);
        
        try
        {
            bw.Write(req.Length);
            bw.Write(req);
            bw.Flush();
            int len = br.ReadInt32();
            var bytes = br.ReadBytes(len);
            var res = JsonSerializer.Deserialize<VclPropertiesResult>(bytes, _json);
            
            if (res?.ok == true) return res.properties;
        }
        catch { }
        
        return null;
    }

    public VclFullPropertiesResult? GetFullProperties(string self)
    {
        if (!IsConnected || string.IsNullOrEmpty(self)) return null;

        var req = JsonSerializer.SerializeToUtf8Bytes(new { cmd = "get_full_properties", self = self });
        var bw = new BinaryWriter(_pipe!, Encoding.UTF8, leaveOpen: true);
        var br = new BinaryReader(_pipe!, Encoding.UTF8, leaveOpen: true);

        try
        {
            bw.Write(req.Length);
            bw.Write(req);
            bw.Flush();
            int len = br.ReadInt32();
            var bytes = br.ReadBytes(len);
            var res = JsonSerializer.Deserialize<VclFullPropertiesResult>(bytes, _json);
            if (res?.ok == true) return res;
        }
        catch { }

        return null;
    }

    Dictionary<string, string>? SpyCapture.IVclBackend.GetProperties(IntPtr hwnd)
    {
        var res = ResolveByHwnd(hwnd);
        if (res != null && res.ok && !string.IsNullOrEmpty(res.vcl_self))
        {
            return GetProperties(res.vcl_self);
        }
        return null;
    }
}
