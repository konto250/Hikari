namespace Hikari;

internal sealed record Spot(int X, int Y, int Radius, double Strength);
internal sealed record Peak(int X, int Y, FloatPixel Color, double Luminance);
internal sealed record Detection(Spot Left, Spot Right, Peak Peak);

internal static class HighlightDetector
{
    // 明るい点を複数の半径で探索し、左右の距離・傾き・反射色が整合する組を選ぶ。
    public static Detection? Find(Frame image)
    {
        double scale = Math.Max(1, Math.Max(image.Width, image.Height) / 1440.0);
        int w = (int)(image.Width / scale), h = (int)(image.Height / scale);
        var light = new byte[w*h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            var c = image.Pixel((int)(x*scale), (int)(y*scale));
            light[y*w+x] = (byte)Math.Clamp((54*c.R + 183*c.G + 19*c.B) / 256,0,255);
        }
        var spots = new List<Spot>();
        foreach (int radius in new[] { 2, 3, 5, 8, 12, 18, 27 })
        {
            int diagonal = (int)Math.Round(radius / Math.Sqrt(2));
            int[] offsets = [radius, -radius, radius*w, -radius*w, diagonal*(w+1), -diagonal*(w+1), diagonal*(w-1), -diagonal*(w-1)];
            int step = Math.Max(1, radius/3);
            int tile = Math.Max(10, radius*2);
            var cells = new Dictionary<(int,int),Spot>();
            for (int y = radius; y < h-radius; y += step)
            for (int x = radius; x < w-radius; x += step)
            {
                int i = y*w+x, center = light[i];
                if (center < 32 || center - light[i+radius] < 10 || center - light[i-radius] < 10) continue;
                int sum = 0, dark = 0;
                foreach (int offset in offsets)
                {
                    int difference = center - light[i+offset];
                    sum += Math.Clamp(difference, -40, 140);
                    if (difference > 12) dark++;
                }
                if (dark < 7 || sum < 150) continue;
                int cx = (int)(x*scale), cy = (int)(y*scale);
                var c = image.Pixel(cx, cy);
                if (c.A < 245) continue;
                // 点の中心が細い輪郭線や圧縮ノイズに乗っていないかを確認する。
                int inner = Math.Max(1, radius/4);
                int variation = new[] {inner,-inner,w*inner,-w*inner}.Sum(k => Math.Abs(center-light[i+k]));
                double strength = sum/8.0 - variation*0.22;
                var key = (x/tile,y/tile);
                if (strength > 14 && (!cells.TryGetValue(key,out var old) || strength > old.Strength))
                    cells[key] = new(cx,cy,Math.Max(1,(int)(radius*scale)),strength);
            }
            spots.AddRange(cells.Values.OrderByDescending(s => s.Strength).Take(280));
        }
        double best = 20;
        (Spot Left, Spot Right)? pair = null;
        foreach (var a in spots)
        foreach (var b in spots)
        {
            int dx = b.X-a.X, dy = b.Y-a.Y;
            double radius = (a.Radius+b.Radius)*0.5;
            if (dx < 8*radius || dx > 85*radius || Math.Abs(dy) > dx*0.52 ||
                Math.Max(a.Radius,b.Radius) > 2.5*Math.Min(a.Radius,b.Radius)) continue;
            var ca = image.Pixel(a.X,a.Y);
            var cb = image.Pixel(b.X,b.Y);
            float difference = Math.Abs(ca.R-cb.R)+Math.Abs(ca.G-cb.G)+Math.Abs(ca.B-cb.B);
            double score = Math.Sqrt(a.Strength*b.Strength) * Math.Exp(-difference/75.0
                - 1.4*Math.Abs(dy)/(double)dx - 0.55*Math.Abs(Math.Log(dx/(radius*28)))
                - 0.30*Math.Abs(Math.Log((double)a.Radius/b.Radius)));
            if (score <= best) continue;
            best = score;
            pair = (a,b);
        }
        if (pair is not {} selected) return null;
        var left = Brightest(image,selected.Left);
        var right = Brightest(image,selected.Right);
        return new(selected.Left,selected.Right,left.Luminance >= right.Luminance ? left : right);
    }

    static readonly double[] Linear = Enumerable.Range(0,256).Select(i => {
        double v = i/255.0;
        return v <= 0.04045 ? v/12.92 : Math.Pow((v+0.055)/1.055,2.4);
    }).ToArray();

    static double ToLinear(float code)
    {
        if (code >= 0 && code <= 255 && code == (int)code) return Linear[(int)code];
        double v = code/255.0;
        return v <= 0.04045 ? v/12.92 : Math.Pow((v+0.055)/1.055,2.4);
    }

    static double Luminance(FloatPixel c) => 0.2126*ToLinear(c.R)+0.7152*ToLinear(c.G)+0.0722*ToLinear(c.B);

    public static Peak? InRegion(Frame image, int x0, int y0, int x1, int y1)
    {
        Peak? best = null;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            var c = image.Pixel(x,y);
            if (c.A == 0) continue;
            double l = Luminance(c);
            if (best == null || l > best.Luminance) best = new(x,y,c,l);
        }
        return best;
    }

    // 元解像度で連結した明部をたどる。暗い境界の向こうにある白目は採色対象から外す。
    static Peak Brightest(Frame image, Spot seed)
    {
        int radius = seed.Radius;
        var center = image.Pixel(seed.X,seed.Y);
        var peak = new Peak(seed.X,seed.Y,center,Luminance(center));
        var ring = Enumerable.Range(0,8).Select(k => {
            double angle = k*Math.PI/4;
            int x = Math.Clamp(seed.X+(int)Math.Round(radius*Math.Cos(angle)),0,image.Width-1);
            int y = Math.Clamp(seed.Y+(int)Math.Round(radius*Math.Sin(angle)),0,image.Height-1);
            return Luminance(image.Pixel(x,y));
        }).Order().ToArray();
        double threshold = (peak.Luminance + ring[4])*0.5;
        int extent = 2*radius, side = 2*extent+1;
        var visited = new bool[side*side];
        var pending = new Queue<(int X,int Y)>();
        void Visit(int x,int y)
        {
            int dx = x-seed.X, dy = y-seed.Y;
            if ((uint)x >= image.Width || (uint)y >= image.Height || dx*dx+dy*dy > extent*extent) return;
            int i = (dy+extent)*side+dx+extent;
            if (visited[i]) return;
            visited[i] = true;
            var c = image.Pixel(x,y);
            double l = Luminance(c);
            if (c.A < 245 || l < threshold) return;
            if (l > peak.Luminance) peak = new(x,y,c,l);
            pending.Enqueue((x,y));
        }
        Visit(seed.X,seed.Y);
        while (pending.TryDequeue(out var p))
        {
            Visit(p.X-1,p.Y); Visit(p.X+1,p.Y);
            Visit(p.X,p.Y-1); Visit(p.X,p.Y+1);
        }
        return peak;
    }
}
