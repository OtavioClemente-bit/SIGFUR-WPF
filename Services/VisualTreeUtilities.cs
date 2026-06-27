using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace SIGFUR.Wpf.Services;

public static class VisualTreeUtilities
{
    public static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        while (source is not null && visited.Add(source))
        {
            if (source is T typed) return typed;
            source = GetParent(source);
        }
        return null;
    }

    public static DependencyObject? GetParent(DependencyObject source)
    {
        try
        {
            if (source is Visual or Visual3D)
                return VisualTreeHelper.GetParent(source);
            if (source is FrameworkContentElement frameworkContent)
                return ContentOperations.GetParent(frameworkContent) ?? LogicalTreeHelper.GetParent(frameworkContent);
            if (source is ContentElement content)
                return ContentOperations.GetParent(content);
            return LogicalTreeHelper.GetParent(source);
        }
        catch
        {
            try { return LogicalTreeHelper.GetParent(source); }
            catch { return null; }
        }
    }
    public static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null) return null;
        var queue = new Queue<DependencyObject>();
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current)) continue;
            if (current is T typed) return typed;
            try
            {
                if (current is Visual or Visual3D)
                {
                    var count = VisualTreeHelper.GetChildrenCount(current);
                    for (var index = 0; index < count; index++) queue.Enqueue(VisualTreeHelper.GetChild(current, index));
                }
            }
            catch { }
        }
        return null;
    }

}
