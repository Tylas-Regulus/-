using System.Runtime.InteropServices;

namespace Live2DPet.Platform;

/// <summary>
/// Turns a coverage mask into a window region.
///
/// GDI wants a list of rectangles, so each mask row is run-length encoded into
/// horizontal spans and the spans become the region's data. That is one
/// rectangle per run rather than one per cell, which keeps a 160x225 mask down to
/// a few hundred rectangles instead of thirty six thousand.
///
/// Measured on this engine: a window region does **not** clip the
/// DirectComposition visual, because a window created with
/// WS_EX_NOREDIRECTIONBITMAP has no redirection surface to clip. The region only
/// shapes what the window manager considers the window to be. That is what makes
/// this safe: a coarse region cannot cut into the picture, only into the input
/// area.
/// </summary>
internal sealed class WindowRegion : IDisposable
{
    private readonly List<Rect> _rectangles = new(256);
    private nint _buffer;
    private int _bufferSize;

    /// <summary>
    /// Builds a region covering every set cell of the mask. The caller hands the
    /// result to <c>SetWindowRgn</c>, which takes ownership of it - do not delete
    /// it afterwards.
    /// </summary>
    public nint FromMask(bool[] mask, int columns, int rows, int cellSize, int width, int height)
    {
        var rectangles = _rectangles;
        rectangles.Clear();

        for (var row = 0; row < rows; row++)
        {
            var offset = row * columns;
            var count = 0;
            var column = 0;

            while (column < columns)
            {
                if (!mask[offset + column])
                {
                    column++;
                    continue;
                }

                var start = column;
                while (column < columns && mask[offset + column])
                {
                    column++;
                }

                var left = start * cellSize;
                var right = Math.Min(column * cellSize, width);
                var top = row * cellSize;
                var bottom = Math.Min((row + 1) * cellSize, height);

                if (right > left && bottom > top)
                {
                    rectangles.Add(new Rect
                    {
                        Left = left,
                        Top = top,
                        Right = right,
                        Bottom = bottom,
                    });

                }
            }
        }

        var header = Marshal.SizeOf<RegionDataHeader>();
        var rectSize = Marshal.SizeOf<Rect>();
        var size = header + rectangles.Count * rectSize;

        // The buffer is reused rather than allocated per reshape: this runs five
        // times a second forever, and the allocation was showing up as a
        // multi-millisecond spike in the frame time tail.
        if (_buffer == 0 || _bufferSize < size)
        {
            if (_buffer != 0)
            {
                Marshal.FreeHGlobal(_buffer);
            }

            _bufferSize = Math.Max(size, 64 * 1024);
            _buffer = Marshal.AllocHGlobal(_bufferSize);
        }

        var data = new RegionDataHeader
        {
            // The header's own size, then the size of the rectangle array that
            // follows it. ExtCreateRegion rejects the data otherwise.
            Size = (uint)header,
            Type = 1, // RDH_RECTANGLES
            Count = (uint)rectangles.Count,
            RgnSize = (uint)(rectangles.Count * rectSize),
            Bounds = Bounds(rectangles),
        };

        Marshal.StructureToPtr(data, _buffer, false);

        var cursor = _buffer + header;
        foreach (var rectangle in rectangles)
        {
            Marshal.StructureToPtr(rectangle, cursor, false);
            cursor += rectSize;
        }

        return ExtCreateRegion(0, (uint)size, _buffer);
    }

    /// <summary>A region with no area, which makes the whole window transparent
    /// to the mouse without touching the picture.</summary>
    public static nint Empty()
    {
        var rectangles = new[] { new Rect { Left = 0, Top = 0, Right = 0, Bottom = 0 } };
        var header = Marshal.SizeOf<RegionDataHeader>();
        var size = header + Marshal.SizeOf<Rect>();
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            var data = new RegionDataHeader
            {
                Size = (uint)header,
                Type = 1,
                Count = 1,
                RgnSize = (uint)Marshal.SizeOf<Rect>(),
                Bounds = default,
            };

            Marshal.StructureToPtr(data, buffer, false);
            Marshal.StructureToPtr(rectangles[0], buffer + header, false);

            return ExtCreateRegion(0, (uint)size, buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static Rect Bounds(List<Rect> rectangles)
    {
        if (rectangles.Count == 0)
        {
            return default;
        }

        var bounds = rectangles[0];
        foreach (var rectangle in rectangles)
        {
            bounds.Left = Math.Min(bounds.Left, rectangle.Left);
            bounds.Top = Math.Min(bounds.Top, rectangle.Top);
            bounds.Right = Math.Max(bounds.Right, rectangle.Right);
            bounds.Bottom = Math.Max(bounds.Bottom, rectangle.Bottom);
        }

        return bounds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RegionDataHeader
    {
        public uint Size;
        public uint Type;
        public uint Count;
        public uint RgnSize;
        public Rect Bounds;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint ExtCreateRegion(nint transform, uint size, nint data);

    public void Dispose()
    {
        if (_buffer != 0)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = 0;
            _bufferSize = 0;
        }
    }
}
