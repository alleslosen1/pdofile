using System.Numerics;

namespace InkPDF.Ink;

public static class Eraser
{
    /// <summary>
    /// Erases along the segment a-b (page points) with the given radius. Only strokes in the
    /// grid cells around the segment are examined, so cost is independent of page ink count.
    /// </summary>
    public static void Erase(InkStore store, int page, Vector2 a, Vector2 b, float radius, bool wholeStroke, ChangeBuilder change)
    {
        var seg = Box.Around(a, b).Inflate(radius);
        var candidates = store.Query(page, seg.Inflate(store.MaxRadius(page) + 0.01f));
        HashSet<int>? groups = null;

        foreach (var s in candidates)
        {
            if (s.Selected || !s.Bounds.Intersects(seg)) continue;
            var pts = s.HitPoints;
            int n = pts.Length;

            if (wholeStroke)
            {
                for (int i = 0; i < n; i++)
                {
                    float lim = radius + pts[i].R;
                    if (StrokeMath.DistToSegmentSq(pts[i].P, a, b) < lim * lim)
                    {
                        store.Remove(s);
                        change.Removed(s);
                        if (s.Group != 0) (groups ??= new()).Add(s.Group);
                        break;
                    }
                }
                continue;
            }

            bool[]? hit = null;
            int hitCount = 0;
            for (int i = 0; i < n; i++)
            {
                float lim = radius + 0.5f * pts[i].R;
                if (StrokeMath.DistToSegmentSq(pts[i].P, a, b) < lim * lim)
                {
                    hit ??= new bool[n];
                    hit[i] = true;
                    hitCount++;
                }
            }
            if (hitCount == 0) continue;

            store.Remove(s);
            change.Removed(s);
            if (hitCount == n) continue;

            // Keep the runs of points the eraser didn't touch as new strokes.
            int start = -1;
            for (int i = 0; i <= n; i++)
            {
                bool h = i == n || hit![i];
                if (!h && start < 0) start = i;
                else if (h && start >= 0)
                {
                    int len = i - start;
                    if (len >= 2)
                    {
                        var piece = s.Clone(StrokeMath.Simplify(pts.AsSpan(start, len), 0.03f));
                        store.Add(piece);
                        change.Added(piece);
                    }
                    start = -1;
                }
            }
        }

        if (groups != null)
        {
            foreach (var s in store.GroupMembers(page, groups))
            {
                if (s.Selected) continue;
                store.Remove(s);
                change.Removed(s);
            }
        }
    }
}
