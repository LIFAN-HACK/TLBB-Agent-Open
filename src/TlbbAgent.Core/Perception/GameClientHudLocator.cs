using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Perception;

internal static class GameClientHudLocator
{
    public static int ResolveClientTop(CaptureResult frame)
    {
        if (frame.Width <= 0
            || frame.Height <= 0
            || frame.Pixels.Length < frame.Width * frame.Height * 4)
            return 0;

        var x1 = frame.Width / 3;
        var x2 = Math.Max(x1 + 1, frame.Width * 2 / 3);
        var scanRows = Math.Min(60, frame.Height);
        var brightRun = 0;
        var titleBandSeen = false;

        for (var y = 0; y < scanRows; y++)
        {
            var brightNeutral = 0;
            for (var x = x1; x < x2; x++)
            {
                var offset = (y * frame.Width + x) * 4;
                var b = frame.Pixels[offset];
                var g = frame.Pixels[offset + 1];
                var r = frame.Pixels[offset + 2];
                var min = Math.Min(r, Math.Min(g, b));
                var max = Math.Max(r, Math.Max(g, b));
                if (min > 185 && max - min < 30)
                    brightNeutral++;
            }

            var ratio = brightNeutral / (double)(x2 - x1);
            brightRun = ratio >= 0.55 ? brightRun + 1 : 0;
            if (brightRun >= 8)
                titleBandSeen = true;
            if (titleBandSeen && y >= 10 && ratio <= 0.20)
                return y;
        }

        // CaptureClient only contains the game client, so zero is the normal path.
        return 0;
    }
}
