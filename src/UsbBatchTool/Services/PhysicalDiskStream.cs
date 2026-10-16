using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UsbBatchTool.Services;

/// <summary>
/// Synchronous, sector-aligned I/O on the already verified device handle.
/// FileStream treats some physical-device handles as non-seekable: its Position
/// must not be used to locate raw writes or reads. Every operation here positions
/// the Win32 handle explicitly and checks the resulting absolute offset.
/// The caller owns the handle and keeps it open for the whole write/verify job.
/// </summary>
internal sealed class PhysicalDiskStream : Stream
{
    private readonly SafeFileHandle _handle;
    private readonly long _length;
    private readonly int _sectorSize;
    private readonly bool _writable;
    private long _position;
    private bool _disposed;

    internal PhysicalDiskStream(SafeFileHandle handle, long length, int sectorSize, bool writable)
    {
        if (handle.IsInvalid || handle.IsClosed) throw new ArgumentException("磁盘句柄无效。");
        if (length <= 0 || sectorSize <= 0 || length % sectorSize != 0) throw new ArgumentException("磁盘容量或扇区大小无效。");
        _handle = handle;
        _length = length;
        _sectorSize = sectorSize;
        _writable = writable;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => !_disposed && _writable;
    public override long Length => _length;
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var absolute = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(_length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (absolute < 0 || absolute > _length || absolute % _sectorSize != 0)
            throw new IOException("磁盘读写位置超出容量或未按扇区对齐。");
        if (!SetFilePointerEx(_handle, absolute, out var actual, 0)) throw NativeDisk.LastError("无法定位物理磁盘。");
        if (actual != absolute) throw new IOException("物理磁盘实际读写位置不一致，已停止操作。");
        return _position = actual;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        Validate(buffer, offset, count);
        if (count == 0 || _position == _length) return 0;
        count = (int)Math.Min(count, _length - _position);
        Seek(_position, SeekOrigin.Begin);
        var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            if (!ReadFile(_handle, IntPtr.Add(pin.AddrOfPinnedObject(), offset), count, out var read, IntPtr.Zero))
                throw NativeDisk.LastError($"读取物理磁盘失败，偏移 {_position:N0}。");
            if (read < 0 || read > count || read % _sectorSize != 0) throw new IOException("物理磁盘返回了不完整的扇区。");
            _position = checked(_position + read);
            return read;
        }
        finally { pin.Free(); }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Validate(buffer, offset, count);
        if (!_writable) throw new NotSupportedException("此磁盘句柄仅允许读取。");
        if (count > _length - _position) throw new IOException("写入超出目标物理磁盘容量。");
        var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            while (count > 0)
            {
                Seek(_position, SeekOrigin.Begin);
                if (!WriteFile(_handle, IntPtr.Add(pin.AddrOfPinnedObject(), offset), count, out var written, IntPtr.Zero))
                    throw NativeDisk.LastError($"写入物理磁盘失败，偏移 {_position:N0}。");
                if (written <= 0 || written > count || written % _sectorSize != 0)
                    throw new IOException("物理磁盘未完整写入扇区。");
                _position = checked(_position + written);
                offset += written;
                count -= written;
            }
        }
        finally { pin.Free(); }
    }

    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_writable && !FlushFileBuffers(_handle)) throw NativeDisk.LastError("无法刷新物理磁盘缓存。");
    }

    private void Validate(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
        if (_position % _sectorSize != 0 || count % _sectorSize != 0) throw new IOException("物理磁盘读写必须按扇区对齐。");
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { _disposed = true; base.Dispose(disposing); }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFilePointerEx(SafeFileHandle file, long distance, out long position, uint origin);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(SafeFileHandle file, IntPtr buffer, int count, out int read, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(SafeFileHandle file, IntPtr buffer, int count, out int written, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle file);
}
