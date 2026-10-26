using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Systemic.Kernel;

public interface IPopulationCodec<T> where T : unmanaged
{
    string Id { get; }

    int Version { get; }

    int ElementSize { get; }

    int GetEncodedLength(int valueCount);

    void Encode(ReadOnlySpan<T> values, Span<byte> destination);

    void Decode(ReadOnlySpan<byte> source, Span<T> values);
}

public sealed class PopulationColumnKey<T> where T : unmanaged
{
    public PopulationColumnKey(string id, int version, IPopulationCodec<T> codec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentException.ThrowIfNullOrWhiteSpace(codec.Id);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(codec.Version);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(codec.ElementSize);

        Id = id;
        Version = version;
        Codec = codec;
    }

    public string Id { get; }

    public int Version { get; }

    public IPopulationCodec<T> Codec { get; }
}

public static class PopulationCodecs
{
    public static IPopulationCodec<bool> Boolean { get; } = new Codec<bool>(
        "kernel.bool", 1, static (value, destination) => destination[0] = value ? (byte)1 : (byte)0,
        static source => source[0] switch
        {
            0 => false,
            1 => true,
            _ => throw new FormatException("A population boolean value must be encoded as zero or one."),
        });

    public static IPopulationCodec<byte> Byte { get; } = new Codec<byte>(
        "kernel.u8", 1, static (value, destination) => destination[0] = value,
        static source => source[0]);

    public static IPopulationCodec<sbyte> SByte { get; } = new Codec<sbyte>(
        "kernel.i8", 1, static (value, destination) => destination[0] = unchecked((byte)value),
        static source => unchecked((sbyte)source[0]));

    public static IPopulationCodec<char> Char { get; } = new Codec<char>(
        "kernel.char16", 2, static (value, destination) => BinaryPrimitives.WriteUInt16LittleEndian(destination, value),
        static source => (char)BinaryPrimitives.ReadUInt16LittleEndian(source));

    public static IPopulationCodec<short> Int16 { get; } = new Codec<short>(
        "kernel.i16", 2, static (value, destination) => BinaryPrimitives.WriteInt16LittleEndian(destination, value),
        static source => BinaryPrimitives.ReadInt16LittleEndian(source));

    public static IPopulationCodec<ushort> UInt16 { get; } = new Codec<ushort>(
        "kernel.u16", 2, static (value, destination) => BinaryPrimitives.WriteUInt16LittleEndian(destination, value),
        static source => BinaryPrimitives.ReadUInt16LittleEndian(source));

    public static IPopulationCodec<int> Int32 { get; } = new Codec<int>(
        "kernel.i32", 4, static (value, destination) => BinaryPrimitives.WriteInt32LittleEndian(destination, value),
        static source => BinaryPrimitives.ReadInt32LittleEndian(source));

    public static IPopulationCodec<uint> UInt32 { get; } = new Codec<uint>(
        "kernel.u32", 4, static (value, destination) => BinaryPrimitives.WriteUInt32LittleEndian(destination, value),
        static source => BinaryPrimitives.ReadUInt32LittleEndian(source));

    public static IPopulationCodec<long> Int64 { get; } = new Codec<long>(
        "kernel.i64", 8, static (value, destination) => BinaryPrimitives.WriteInt64LittleEndian(destination, value),
        static source => BinaryPrimitives.ReadInt64LittleEndian(source));

    public static IPopulationCodec<ulong> UInt64 { get; } = new Codec<ulong>(
        "kernel.u64", 8, static (value, destination) => BinaryPrimitives.WriteUInt64LittleEndian(destination, value),
        static source => BinaryPrimitives.ReadUInt64LittleEndian(source));

    public static IPopulationCodec<float> Single { get; } = new Codec<float>(
        "kernel.f32", 4,
        static (value, destination) => BinaryPrimitives.WriteInt32LittleEndian(destination, BitConverter.SingleToInt32Bits(value)),
        static source => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source)));

    public static IPopulationCodec<double> Double { get; } = new Codec<double>(
        "kernel.f64", 8,
        static (value, destination) => BinaryPrimitives.WriteInt64LittleEndian(destination, BitConverter.DoubleToInt64Bits(value)),
        static source => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(source)));

    public static IPopulationCodec<decimal> Decimal { get; } = new Codec<decimal>(
        "kernel.decimal128", 16,
        static (value, destination) =>
        {
            Span<int> bits = stackalloc int[4];
            decimal.GetBits(value, bits);
            for (var index = 0; index < bits.Length; index++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(destination[(index * sizeof(int))..], bits[index]);
            }
        },
        static source =>
        {
            var low = BinaryPrimitives.ReadInt32LittleEndian(source);
            var middle = BinaryPrimitives.ReadInt32LittleEndian(source[4..]);
            var high = BinaryPrimitives.ReadInt32LittleEndian(source[8..]);
            var flags = BinaryPrimitives.ReadInt32LittleEndian(source[12..]);
            var scale = (byte)((flags >> 16) & 0xff);
            var isNegative = (flags & int.MinValue) != 0;
            return new decimal(low, middle, high, isNegative, scale);
        });

    public static IPopulationCodec<Vector2> Vector2 { get; } = new Codec<System.Numerics.Vector2>(
        "system.numerics.vector2.f32", 8,
        static (value, destination) =>
        {
            WriteSingle(value.X, destination);
            WriteSingle(value.Y, destination[4..]);
        },
        static source => new(ReadSingle(source), ReadSingle(source[4..])));

    public static IPopulationCodec<Vector3> Vector3 { get; } = new Codec<System.Numerics.Vector3>(
        "system.numerics.vector3.f32", 12,
        static (value, destination) =>
        {
            WriteSingle(value.X, destination);
            WriteSingle(value.Y, destination[4..]);
            WriteSingle(value.Z, destination[8..]);
        },
        static source => new(ReadSingle(source), ReadSingle(source[4..]), ReadSingle(source[8..])));

    public static IPopulationCodec<Vector4> Vector4 { get; } = new Codec<System.Numerics.Vector4>(
        "system.numerics.vector4.f32", 16,
        static (value, destination) =>
        {
            WriteSingle(value.X, destination);
            WriteSingle(value.Y, destination[4..]);
            WriteSingle(value.Z, destination[8..]);
            WriteSingle(value.W, destination[12..]);
        },
        static source => new(ReadSingle(source), ReadSingle(source[4..]), ReadSingle(source[8..]), ReadSingle(source[12..])));

    private static void WriteSingle(float value, Span<byte> destination) =>
        BinaryPrimitives.WriteInt32LittleEndian(destination, BitConverter.SingleToInt32Bits(value));

    private static float ReadSingle(ReadOnlySpan<byte> source) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source));

    private sealed class Codec<T>(string id, int elementSize, ValueWriter<T> write, ValueReader<T> read)
        : IPopulationCodec<T> where T : unmanaged
    {
        public string Id { get; } = id;

        public int Version => 1;

        public int ElementSize { get; } = elementSize;

        public int GetEncodedLength(int valueCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(valueCount);
            return checked(valueCount * ElementSize);
        }

        public void Encode(ReadOnlySpan<T> values, Span<byte> destination)
        {
            if (destination.Length != GetEncodedLength(values.Length))
            {
                throw new ArgumentException("The destination length does not match the value count.", nameof(destination));
            }

            for (var index = 0; index < values.Length; index++)
            {
                write(values[index], destination.Slice(index * ElementSize, ElementSize));
            }
        }

        public void Decode(ReadOnlySpan<byte> source, Span<T> values)
        {
            if (source.Length != GetEncodedLength(values.Length))
            {
                throw new ArgumentException("The source length does not match the value count.", nameof(source));
            }

            for (var index = 0; index < values.Length; index++)
            {
                values[index] = read(source.Slice(index * ElementSize, ElementSize));
            }
        }
    }

    private delegate void ValueWriter<T>(T value, Span<byte> destination) where T : unmanaged;

    private delegate T ValueReader<T>(ReadOnlySpan<byte> source) where T : unmanaged;
}

internal sealed class PopulationStorage(World world)
{
    private World? _world = world;

    public int Count { get; set; }

    public World World => _world ?? throw new InvalidOperationException("Population storage is detached from its world.");

    public bool HasActiveTransaction => _world?.HasActiveScope == true;

    public void ValidateRange(int start, int length)
    {
        EnsureActive();
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (start > Count || length > Count - start)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The range must fit inside the live population rows.");
        }
    }

    public void EnsureActive() => (_world ?? throw new InvalidOperationException("Population storage is detached from its world."))
        .EnsurePopulationActive();

    public void Rebind(World world) => _world = world;

    public void Detach() => _world = null;
}

internal sealed class SharedPopulationBuffer<T>(T[] values) where T : unmanaged
{
    private T[] _values = values;
    private bool _needsDetach;

    public T[] Values => _values;

    public SharedPopulationBuffer<T> Share()
    {
        _needsDetach = true;
        return new SharedPopulationBuffer<T>(_values) { _needsDetach = true };
    }

    public SharedPopulationBuffer<T> EnsureWritable()
    {
        if (!_needsDetach)
        {
            return this;
        }

        return new SharedPopulationBuffer<T>((T[])_values.Clone());
    }

    public SharedPopulationBuffer<T> EnsureCapacity(int capacity)
    {
        var writable = EnsureWritable();
        if (writable._values.Length < capacity)
        {
            Array.Resize(ref writable._values, capacity);
        }

        return writable;
    }
}

public abstract class PopulationColumnBase(
    string id,
    int version,
    Type valueType,
    string codecId,
    int codecVersion,
    int elementSize,
    bool isDoubleBuffered)
{
    private readonly Stack<PopulationPageUndoBatch> _freePageUndoBatches = new();

    public string Id { get; } = id;

    public int Version { get; } = version;

    public Type ValueType { get; } = valueType;

    public string CodecId { get; } = codecId;

    public int CodecVersion { get; } = codecVersion;

    public int ElementSize { get; } = elementSize;

    public bool IsDoubleBuffered { get; } = isDoubleBuffered;

    internal abstract void EnsureCapacity(int capacity);

    internal abstract PopulationColumnSaveState CaptureSaveState(int count);

    internal abstract Action PrepareRestore(PopulationColumnSaveState state, int count);

    protected void ValidateSaveState(PopulationColumnSaveState state, bool isDoubleBuffered, int expectedDataLength)
    {
        if (state is null || !string.Equals(state.Id, Id, StringComparison.Ordinal)
            || state.Version != Version || !string.Equals(state.CodecId, CodecId, StringComparison.Ordinal)
            || state.CodecVersion != CodecVersion || state.ElementSize != ElementSize
            || state.IsDoubleBuffered != isDoubleBuffered || state.Data is null)
        {
            throw new InvalidDataException($"Population column '{Id}' has an unknown or incompatible schema.");
        }

        if (state.Data.Length != expectedDataLength)
        {
            throw new InvalidDataException($"Population column '{Id}' payload has an invalid length.");
        }
    }

    internal abstract void ClearRange(int start, int length);

    internal abstract void MoveRow(int source, int destination);

    internal abstract PopulationColumnBase CloneTo(PopulationStorage storage);

    internal IColumnUndoBatch CapturePageUndo(int bufferIndex, int start, int length)
    {
        var batch = _freePageUndoBatches.Count > 0
            ? _freePageUndoBatches.Pop()
            : new PopulationPageUndoBatch(this);
        try
        {
            batch.Capture(bufferIndex, start, length);
            return batch;
        }
        catch
        {
            batch.Reset();
            _freePageUndoBatches.Push(batch);
            throw;
        }
    }

    internal abstract int GetPageSnapshotByteLength(int rowCount);

    internal abstract void CopyPageToSnapshot(int bufferIndex, int start, Span<byte> destination);

    internal abstract void RestorePageFromSnapshot(int bufferIndex, int start, ReadOnlySpan<byte> source);

    internal void ReleasePageUndo(PopulationPageUndoBatch batch)
    {
        batch.Reset();
        _freePageUndoBatches.Push(batch);
    }
}

internal sealed class PopulationPageUndoBatch(PopulationColumnBase column) : IColumnUndoBatch
{
    private byte[] _snapshot = [];
    private int _bufferIndex;
    private int _start;
    private int _byteLength;

    public void Capture(int bufferIndex, int start, int length)
    {
        _bufferIndex = bufferIndex;
        _start = start;
        _byteLength = column.GetPageSnapshotByteLength(length);
        if (_snapshot.Length < _byteLength)
        {
            Array.Resize(ref _snapshot, _byteLength);
        }

        column.CopyPageToSnapshot(bufferIndex, start, _snapshot.AsSpan(0, _byteLength));
    }

    public void Undo() => column.RestorePageFromSnapshot(
        _bufferIndex, _start, _snapshot.AsSpan(0, _byteLength));

    public void Release() => column.ReleasePageUndo(this);

    public void Reset()
    {
        _bufferIndex = 0;
        _start = 0;
        _byteLength = 0;
    }
}

public sealed class PopulationColumn<T> : PopulationColumnBase where T : unmanaged
{
    private readonly PopulationStorage _population;
    private readonly PopulationColumnKey<T> _key;
    private SharedPopulationBuffer<T> _buffer = new([]);

    internal PopulationColumn(PopulationStorage population, PopulationColumnKey<T> key)
        : base(key.Id, key.Version, typeof(T), key.Codec.Id, key.Codec.Version, key.Codec.ElementSize,
            isDoubleBuffered: false)
    {
        _population = population;
        _key = key;
    }

    private PopulationColumn(PopulationStorage population, PopulationColumnKey<T> key, SharedPopulationBuffer<T> buffer)
        : this(population, key) => _buffer = buffer;

    public int Count
    {
        get
        {
            _population.EnsureActive();
            return _population.Count;
        }
    }

    /// <summary>Gets a borrowed read-only view of the live column rows.</summary>
    /// <remarks>Do not retain or use this span across world cloning, adoption, or population structural changes.</remarks>
    public ReadOnlySpan<T> ReadOnlySpan
    {
        get
        {
            _population.EnsureActive();
            return _buffer.Values.AsSpan(0, _population.Count);
        }
    }

    /// <summary>Gets a borrowed read-only memory view of the live column rows.</summary>
    /// <remarks>Do not retain or use this view across world cloning, adoption, or population structural changes.</remarks>
    public ReadOnlyMemory<T> ReadOnlyMemory
    {
        get
        {
            _population.EnsureActive();
            return _buffer.Values.AsMemory(0, _population.Count);
        }
    }

    /// <summary>Gets a borrowed mutable view of a live row range.</summary>
    /// <remarks>
    /// Do not retain or use this span across world cloning, adoption, or population structural changes. An already
    /// issued span cannot be revoked and writes through it after cloning can bypass copy-on-write.
    /// </remarks>
    public Span<T> EditRange(int start, int length)
    {
        _population.ValidateRange(start, length);
        _buffer = _buffer.EnsureWritable();
        CapturePages(0, start, length, _buffer.Values.Length);
        return _buffer.Values.AsSpan(start, length);
    }

    /// <summary>Gets a borrowed mutable memory view of a live row range.</summary>
    /// <remarks>
    /// Do not retain or use this view across world cloning, adoption, or population structural changes. An already
    /// issued view cannot be revoked and writes through it after cloning can bypass copy-on-write.
    /// </remarks>
    public Memory<T> EditMemory(int start, int length)
    {
        _population.ValidateRange(start, length);
        _buffer = _buffer.EnsureWritable();
        CapturePages(0, start, length, _buffer.Values.Length);
        return _buffer.Values.AsMemory(start, length);
    }

    internal PopulationColumnKey<T> Key => _key;

    internal override PopulationColumnSaveState CaptureSaveState(int count)
    {
        var data = new byte[_key.Codec.GetEncodedLength(count)];
        _key.Codec.Encode(_buffer.Values.AsSpan(0, count), data);
        return new PopulationColumnSaveState(Id, Version, CodecId, CodecVersion, ElementSize,
            IsDoubleBuffered, data);
    }

    internal override Action PrepareRestore(PopulationColumnSaveState state, int count)
    {
        ValidateSaveState(state, isDoubleBuffered: false, expectedDataLength: _key.Codec.GetEncodedLength(count));
        var values = new T[count];
        _key.Codec.Decode(state.Data, values);
        return () =>
        {
            _buffer = _buffer.EnsureCapacity(count);
            values.AsSpan().CopyTo(_buffer.Values);
        };
    }

    internal override void EnsureCapacity(int capacity)
    {
        _buffer = _buffer.EnsureCapacity(capacity);
    }

    internal override void ClearRange(int start, int length)
    {
        _buffer = _buffer.EnsureWritable();
        CapturePages(0, start, length, _buffer.Values.Length);
        Array.Clear(_buffer.Values, start, length);
    }

    internal override void MoveRow(int source, int destination)
    {
        _buffer = _buffer.EnsureWritable();
        CapturePages(0, destination, 1, _buffer.Values.Length);
        _buffer.Values[destination] = _buffer.Values[source];
    }

    internal override int GetPageSnapshotByteLength(int rowCount) => checked(rowCount * Unsafe.SizeOf<T>());

    internal override void CopyPageToSnapshot(int bufferIndex, int start, Span<byte> destination)
    {
        var rowCount = destination.Length / Unsafe.SizeOf<T>();
        MemoryMarshal.AsBytes(_buffer.Values.AsSpan(start, rowCount)).CopyTo(destination);
    }

    internal override void RestorePageFromSnapshot(int bufferIndex, int start, ReadOnlySpan<byte> source)
    {
        _buffer = _buffer.EnsureWritable();
        MemoryMarshal.Cast<byte, T>(source).CopyTo(_buffer.Values.AsSpan(start, source.Length / Unsafe.SizeOf<T>()));
    }

    private void CapturePages(int bufferIndex, int start, int length, int capacity)
    {
        if (length == 0 || !_population.HasActiveTransaction)
        {
            return;
        }

        var rowsPerPage = Math.Max(1, 4_096 / Unsafe.SizeOf<T>());
        if (start == 0 && length == _population.Count
            && _population.World.RecordPopulationBuffer(this, bufferIndex, capacity, rowsPerPage))
        {
            return;
        }

        var end = checked(start + length);
        var firstPage = start / rowsPerPage;
        var lastPage = (end - 1) / rowsPerPage;
        for (var page = firstPage; page <= lastPage; page++)
        {
            var pageStart = page * rowsPerPage;
            var pageLength = Math.Min(rowsPerPage, capacity - pageStart);
            _population.World.RecordPopulationPage(this, bufferIndex, page, pageStart, pageLength);
        }
    }

    internal override PopulationColumnBase CloneTo(PopulationStorage storage) =>
        new PopulationColumn<T>(storage, _key, _buffer.Share());
}

public sealed class DoubleBufferedPopulationColumn<T> : PopulationColumnBase where T : unmanaged
{
    private readonly PopulationStorage _population;
    private readonly PopulationColumnKey<T> _key;
    private SharedPopulationBuffer<T> _first = new([]);
    private SharedPopulationBuffer<T> _second = new([]);
    private bool _firstIsCurrent = true;
    private readonly Stack<BufferRoleUndoBatch> _freeRoleUndoBatches = new();

    internal DoubleBufferedPopulationColumn(PopulationStorage population, PopulationColumnKey<T> key)
        : base(key.Id, key.Version, typeof(T), key.Codec.Id, key.Codec.Version, key.Codec.ElementSize,
            isDoubleBuffered: true)
    {
        _population = population;
        _key = key;
    }

    private DoubleBufferedPopulationColumn(PopulationStorage population, PopulationColumnKey<T> key,
        SharedPopulationBuffer<T> first, SharedPopulationBuffer<T> second, bool firstIsCurrent)
        : this(population, key)
    {
        _first = first;
        _second = second;
        _firstIsCurrent = firstIsCurrent;
    }

    public int Count
    {
        get
        {
            _population.EnsureActive();
            return _population.Count;
        }
    }

    /// <summary>Gets a borrowed read-only view of the current buffer.</summary>
    /// <remarks>Do not retain or use this span across world cloning, adoption, or population structural changes.</remarks>
    public ReadOnlySpan<T> Current
    {
        get
        {
            _population.EnsureActive();
            return CurrentArray.Values.AsSpan(0, _population.Count);
        }
    }

    /// <summary>Gets a borrowed read-only memory view of the current buffer.</summary>
    /// <remarks>Do not retain or use this view across world cloning, adoption, or population structural changes.</remarks>
    public ReadOnlyMemory<T> CurrentMemory
    {
        get
        {
            _population.EnsureActive();
            return CurrentArray.Values.AsMemory(0, _population.Count);
        }
    }

    /// <summary>Gets a borrowed mutable view of a row range in the next buffer.</summary>
    /// <remarks>
    /// Do not retain or use this span across world cloning, adoption, or population structural changes. An already
    /// issued span cannot be revoked and writes through it after cloning can bypass copy-on-write.
    /// </remarks>
    public Span<T> EditNext(int start, int length)
    {
        _population.ValidateRange(start, length);
        EnsureNextWritable();
        CapturePages(_firstIsCurrent ? 1 : 0, start, length, NextArray.Values.Length);
        return NextArray.Values.AsSpan(start, length);
    }

    /// <summary>Gets writable views of both buffers for initializing newly spawned rows.</summary>
    /// <remarks>Use for structural initialization; simulation updates should write only to <see cref="EditNext"/>.</remarks>
    public DoubleBufferedPopulationEdit<T> EditBothBuffers(int start, int length)
    {
        _population.ValidateRange(start, length);
        _first = _first.EnsureWritable();
        _second = _second.EnsureWritable();
        CapturePages(0, start, length, _first.Values.Length);
        CapturePages(1, start, length, _second.Values.Length);
        return new(CurrentArray.Values.AsSpan(start, length), NextArray.Values.AsSpan(start, length));
    }

    /// <summary>Gets a borrowed mutable memory view of a row range in the next buffer.</summary>
    /// <remarks>
    /// Do not retain or use this view across world cloning, adoption, or population structural changes. An already
    /// issued view cannot be revoked and writes through it after cloning can bypass copy-on-write.
    /// </remarks>
    public Memory<T> EditNextMemory(int start, int length)
    {
        _population.ValidateRange(start, length);
        EnsureNextWritable();
        CapturePages(_firstIsCurrent ? 1 : 0, start, length, NextArray.Values.Length);
        return NextArray.Values.AsMemory(start, length);
    }

    public void Swap()
    {
        _population.EnsureActive();
        if (_population.World.TryCapturePopulationRole(this))
        {
            var batch = _freeRoleUndoBatches.Count > 0 ? _freeRoleUndoBatches.Pop() : new BufferRoleUndoBatch(this);
            try
            {
                batch.Capture(_firstIsCurrent);
                _population.World.RecordPopulationUndo(batch);
            }
            catch
            {
                batch.Release();
                _population.World.ReleasePopulationRoleCapture(this);
                throw;
            }
        }

        _firstIsCurrent = !_firstIsCurrent;
    }

    internal PopulationColumnKey<T> Key => _key;

    internal override PopulationColumnSaveState CaptureSaveState(int count)
    {
        var data = new byte[_key.Codec.GetEncodedLength(count)];
        _key.Codec.Encode(CurrentArray.Values.AsSpan(0, count), data);
        return new PopulationColumnSaveState(Id, Version, CodecId, CodecVersion, ElementSize,
            IsDoubleBuffered, data);
    }

    internal override Action PrepareRestore(PopulationColumnSaveState state, int count)
    {
        ValidateSaveState(state, isDoubleBuffered: true, expectedDataLength: _key.Codec.GetEncodedLength(count));
        var values = new T[count];
        _key.Codec.Decode(state.Data, values);
        return () =>
        {
            _first = _first.EnsureCapacity(count);
            _second = _second.EnsureCapacity(count);
            values.AsSpan().CopyTo(_first.Values);
            values.AsSpan().CopyTo(_second.Values);
            _firstIsCurrent = true;
        };
    }

    private SharedPopulationBuffer<T> CurrentArray => _firstIsCurrent ? _first : _second;

    private SharedPopulationBuffer<T> NextArray => _firstIsCurrent ? _second : _first;

    private void EnsureNextWritable()
    {
        if (_firstIsCurrent)
        {
            _second = _second.EnsureWritable();
        }
        else
        {
            _first = _first.EnsureWritable();
        }
    }

    internal override void EnsureCapacity(int capacity)
    {
        _first = _first.EnsureCapacity(capacity);
        _second = _second.EnsureCapacity(capacity);
    }

    internal override void ClearRange(int start, int length)
    {
        _first = _first.EnsureWritable();
        _second = _second.EnsureWritable();
        CapturePages(0, start, length, _first.Values.Length);
        CapturePages(1, start, length, _second.Values.Length);
        Array.Clear(_first.Values, start, length);
        Array.Clear(_second.Values, start, length);
    }

    internal override void MoveRow(int source, int destination)
    {
        _first = _first.EnsureWritable();
        _second = _second.EnsureWritable();
        CapturePages(0, destination, 1, _first.Values.Length);
        CapturePages(1, destination, 1, _second.Values.Length);
        _first.Values[destination] = _first.Values[source];
        _second.Values[destination] = _second.Values[source];
    }

    internal override int GetPageSnapshotByteLength(int rowCount) => checked(rowCount * Unsafe.SizeOf<T>());

    internal override void CopyPageToSnapshot(int bufferIndex, int start, Span<byte> destination)
    {
        var rowCount = destination.Length / Unsafe.SizeOf<T>();
        MemoryMarshal.AsBytes(BufferAt(bufferIndex).Values.AsSpan(start, rowCount)).CopyTo(destination);
    }

    internal override void RestorePageFromSnapshot(int bufferIndex, int start, ReadOnlySpan<byte> source)
    {
        EnsureBufferWritable(bufferIndex);
        MemoryMarshal.Cast<byte, T>(source).CopyTo(BufferAt(bufferIndex).Values.AsSpan(start, source.Length / Unsafe.SizeOf<T>()));
    }

    private void EnsureBufferWritable(int index)
    {
        switch (index)
        {
            case 0:
                _first = _first.EnsureWritable();
                break;
            case 1:
                _second = _second.EnsureWritable();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    private SharedPopulationBuffer<T> BufferAt(int index) => index switch
    {
        0 => _first,
        1 => _second,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    private void CapturePages(int bufferIndex, int start, int length, int capacity)
    {
        if (length == 0 || !_population.HasActiveTransaction)
        {
            return;
        }

        var rowsPerPage = Math.Max(1, 4_096 / Unsafe.SizeOf<T>());
        if (start == 0 && length == _population.Count
            && _population.World.RecordPopulationBuffer(this, bufferIndex, capacity, rowsPerPage))
        {
            return;
        }

        var end = checked(start + length);
        var firstPage = start / rowsPerPage;
        var lastPage = (end - 1) / rowsPerPage;
        for (var page = firstPage; page <= lastPage; page++)
        {
            var pageStart = page * rowsPerPage;
            var pageLength = Math.Min(rowsPerPage, capacity - pageStart);
            _population.World.RecordPopulationPage(this, bufferIndex, page, pageStart, pageLength);
        }
    }

    private void ReleaseRoleUndo(BufferRoleUndoBatch batch)
    {
        batch.Reset();
        _freeRoleUndoBatches.Push(batch);
    }

    private sealed class BufferRoleUndoBatch(DoubleBufferedPopulationColumn<T> column) : IColumnUndoBatch
    {
        private bool _firstIsCurrent;

        public void Capture(bool firstIsCurrent) => _firstIsCurrent = firstIsCurrent;

        public void Undo() => column._firstIsCurrent = _firstIsCurrent;

        public void Release() => column.ReleaseRoleUndo(this);

        public void Reset() => _firstIsCurrent = false;
    }

    internal override PopulationColumnBase CloneTo(PopulationStorage storage) =>
        new DoubleBufferedPopulationColumn<T>(storage, _key, _first.Share(), _second.Share(), _firstIsCurrent);
}
