using System;
using System.Buffers;

namespace Downloader;

internal class Packet : IDisposable, ISizeableObject
{
    private byte[] rentedData;

    /// <summary>
    /// Exposes only the valid data without copying or slicing.
    /// </summary>
    public Memory<byte> Data => rentedData.AsMemory(0, Length);

    /// <summary>
    /// The full underlying array (may be larger than <see cref="Length"/>); the valid range is
    /// <c>[0, Length)</c>. Used by the legacy write path, where <see cref="Stream"/> has no
    /// <see cref="Memory{T}"/>-based async overloads.
    /// </summary>
    internal byte[] RentedData => rentedData;
    public int Length { get; }
    public long Position { get; }
    public long EndOffset { get; }
    public bool IsRented { get; }

    public Packet(long position, byte[] data, int length, bool isRented = true)
    {
        rentedData = data;
        Length = length;
        Position = position;
        EndOffset = position + length;
        IsRented = isRented;
    }

    public void Dispose()
    {
        if (rentedData != null)
        {
            if (IsRented)
            {
                ArrayPool<byte>.Shared.Return(rentedData);
            }
            rentedData = null;
        }
    }
}