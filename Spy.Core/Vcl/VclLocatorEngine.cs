using System;
using System.Collections.Generic;
using System.Linq;

namespace Spy.Core.Vcl;

public record VclControlInfo(nuint Self, string ClassName, string? Name);

public interface IVclIntrospector
{
    int ProcessId { get; }
    bool TryFindForm(string formId, out VclControlInfo form);
    IReadOnlyList<VclControlInfo> GetChildren(nuint parentSelf);
}

sealed class VclTreeNode
{
    public nuint Self { get; }
    public string ClassName { get; }
    public string? Name { get; }
    public nuint? Parent { get; set; }
    public List<nuint> Children { get; } = new();
    public bool ChildrenLoaded { get; set; }

    public VclTreeNode(nuint self, string className, string? name)
    {
        Self = self;
        ClassName = className;
        Name = name;
    }
}

sealed class VclTreeCache
{
    readonly IVclIntrospector _vcl;
    readonly Dictionary<nuint, VclTreeNode> _nodes = new();

    public VclTreeCache(IVclIntrospector vcl)
    {
        _vcl = vcl;
    }

    public VclTreeNode GetOrAdd(VclControlInfo info)
    {
        if (_nodes.TryGetValue(info.Self, out var node))
            return node;
        node = new VclTreeNode(info.Self, info.ClassName, info.Name);
        _nodes.Add(info.Self, node);
        return node;
    }

    public VclTreeNode? TryGet(nuint self)
    {
        _nodes.TryGetValue(self, out var node);
        return node;
    }

    public void EnsureChildrenLoaded(VclTreeNode node)
    {
        if (node.ChildrenLoaded) return;
        node.ChildrenLoaded = true;
        var children = _vcl.GetChildren(node.Self);
        foreach (var ch in children)
        {
            var childNode = GetOrAdd(ch);
            if (!node.Children.Contains(childNode.Self))
                node.Children.Add(childNode.Self);
            if (childNode.Parent == null)
                childNode.Parent = node.Self;
        }
    }
}

public sealed class VclSmartLocatorEngine
{
    readonly IVclIntrospector _vcl;
    readonly VclTreeCache _cache;
    readonly StringComparer _cmp = StringComparer.OrdinalIgnoreCase;

    public VclSmartLocatorEngine(IVclIntrospector vcl)
    {
        _vcl = vcl;
        _cache = new VclTreeCache(vcl);
    }

    public bool TryResolveResilient(IReadOnlyList<string> vclChain, out VclControlInfo resolved)
    {
        resolved = default!;
        if (vclChain.Count == 0) return false;

        if (!_vcl.TryFindForm(vclChain[0], out var form))
            return false;

        var current = _cache.GetOrAdd(form);

        for (int i = 1; i < vclChain.Count; i++)
        {
            var token = vclChain[i];
            var next = FindDirectChild(current, token);
            if (next == null)
                next = FindInSubtree(current, token, maxNodes: 25000);
            if (next == null)
                return false;
            current = next;
        }

        resolved = new VclControlInfo(current.Self, current.ClassName, current.Name);
        return true;
    }

    public IReadOnlyList<string> OptimizeVclChain(IReadOnlyList<string> vclChain)
    {
        if (vclChain.Count < 2) return vclChain;
        if (!TryResolveResilient(vclChain, out var target))
            return vclChain;

        var cur = vclChain.ToList();
        bool changed;
        do
        {
            changed = false;
            for (int i = 1; i < cur.Count - 1; i++)
            {
                var candidate = cur.Where((_, idx) => idx != i).ToList();
                if (TryResolveUnique(candidate, out var resolved) && resolved.Self == target.Self)
                {
                    cur = candidate;
                    changed = true;
                    break;
                }
            }
        } while (changed && cur.Count > 2);

        return cur;
    }

    bool TryResolveUnique(IReadOnlyList<string> vclChain, out VclControlInfo resolved)
    {
        resolved = default!;
        var matches = FindMatches(vclChain, maxMatches: 2, maxNodes: 40000);
        if (matches.Count != 1) return false;
        resolved = matches[0];
        return true;
    }

    List<VclControlInfo> FindMatches(IReadOnlyList<string> vclChain, int maxMatches, int maxNodes)
    {
        var matches = new List<VclControlInfo>();
        if (vclChain.Count == 0) return matches;

        if (!_vcl.TryFindForm(vclChain[0], out var form))
            return matches;

        var start = _cache.GetOrAdd(form);
        var frontier = new List<VclTreeNode> { start };

        int visited = 0;

        for (int step = 1; step < vclChain.Count; step++)
        {
            var token = vclChain[step];
            var nextFrontier = new List<VclTreeNode>();

            foreach (var node in frontier)
            {
                _cache.EnsureChildrenLoaded(node);
                var direct = node.Children
                    .Select(s => _cache.TryGet(s))
                    .Where(n => n != null)
                    .Cast<VclTreeNode>()
                    .Where(n => MatchesToken(n, token))
                    .ToList();

                if (direct.Count > 0)
                {
                    nextFrontier.AddRange(direct);
                }
                else
                {
                    var inSub = FindAllInSubtree(node, token, maxMatches - nextFrontier.Count, maxNodes - visited, ref visited);
                    nextFrontier.AddRange(inSub);
                }

                if (nextFrontier.Count >= maxMatches)
                    break;
            }

            frontier = nextFrontier;
            if (frontier.Count == 0) return matches;
            if (frontier.Count >= maxMatches) break;
            if (visited >= maxNodes) break;
        }

        foreach (var n in frontier)
        {
            matches.Add(new VclControlInfo(n.Self, n.ClassName, n.Name));
            if (matches.Count >= maxMatches) break;
        }

        return matches;
    }

    VclTreeNode? FindDirectChild(VclTreeNode parent, string token)
    {
        _cache.EnsureChildrenLoaded(parent);
        foreach (var childSelf in parent.Children)
        {
            var n = _cache.TryGet(childSelf);
            if (n == null) continue;
            if (MatchesToken(n, token))
                return n;
        }
        return null;
    }

    VclTreeNode? FindInSubtree(VclTreeNode root, string token, int maxNodes)
    {
        var q = new Queue<VclTreeNode>();
        q.Enqueue(root);
        var visited = new HashSet<nuint>();
        visited.Add(root.Self);

        int processed = 0;
        while (q.Count > 0 && processed < maxNodes)
        {
            var n = q.Dequeue();
            processed++;
            _cache.EnsureChildrenLoaded(n);
            foreach (var childSelf in n.Children)
            {
                if (!visited.Add(childSelf)) continue;
                var child = _cache.TryGet(childSelf);
                if (child == null) continue;
                if (MatchesToken(child, token))
                    return child;
                q.Enqueue(child);
            }
        }
        return null;
    }

    List<VclTreeNode> FindAllInSubtree(VclTreeNode root, string token, int limit, int maxNodes, ref int visitedCount)
    {
        var matches = new List<VclTreeNode>();
        var q = new Queue<VclTreeNode>();
        q.Enqueue(root);
        var visited = new HashSet<nuint>();
        visited.Add(root.Self);

        while (q.Count > 0 && matches.Count < limit && visitedCount < maxNodes)
        {
            var n = q.Dequeue();
            visitedCount++;
            _cache.EnsureChildrenLoaded(n);
            foreach (var childSelf in n.Children)
            {
                if (!visited.Add(childSelf)) continue;
                var child = _cache.TryGet(childSelf);
                if (child == null) continue;
                if (MatchesToken(child, token))
                    matches.Add(child);
                if (matches.Count >= limit) break;
                q.Enqueue(child);
            }
        }
        return matches;
    }

    bool MatchesToken(VclTreeNode node, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        if (!string.IsNullOrWhiteSpace(node.Name) && _cmp.Equals(node.Name, token))
            return true;
        return _cmp.Equals(node.ClassName, token);
    }

    public static bool LooksUnstableName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        var n = name.Trim();
        int us = n.LastIndexOf('_');
        if (us > 0 && us < n.Length - 1)
        {
            bool allDigits = true;
            for (int i = us + 1; i < n.Length; i++)
            {
                if (!char.IsDigit(n[i])) { allDigits = false; break; }
            }
            if (allDigits) return true;
        }
        return false;
    }
}
