using System.Runtime.InteropServices;

namespace Live2DPet.Cubism;

/// <summary>
/// A block of unmanaged memory with a guaranteed alignment.
///
/// Cubism Core deliberately does not allocate: the caller hands it buffers that
/// it then "revives" in place. Alignment and byte size are part of the contract,
/// so this type exists to make that contract impossible to get wrong by accident.
/// </summary>
internal sealed unsafe class AlignedBuffer : IDisposable
{
    private void* _pointer;

    public AlignedBuffer(int size, nuint alignment)
    {
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        Size = size;
        Alignment = alignment;
        _pointer = NativeMemory.AlignedAlloc((nuint)size, alignment);
        NativeMemory.Clear(_pointer, (nuint)size);
    }

    public int Size { get; }

    public nuint Alignment { get; }

    public void* Pointer => _pointer != null
        ? _pointer
        : throw new ObjectDisposedException(nameof(AlignedBuffer));

    public Span<byte> AsSpan() => new(_pointer, Size);

    public void Dispose()
    {
        if (_pointer == null)
        {
            return;
        }

        NativeMemory.AlignedFree(_pointer);
        _pointer = null;
        GC.SuppressFinalize(this);
    }

    ~AlignedBuffer() => Dispose();
}
