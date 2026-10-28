using System.Buffers.Binary;
using System.Text;

namespace AmuleModern.Amule;

public sealed record EcTag(ushort Name, byte Type, byte[] Data, params EcTag[] Children)
{
    public static EcTag Text(ushort name, string value) => new(name, 6, Encoding.UTF8.GetBytes(value + "\0"));
    public static EcTag Integer(ushort name, ulong value, int width = 0)
    {
        width = width == 0 ? value <= byte.MaxValue ? 1 : value <= ushort.MaxValue ? 2 : value <= uint.MaxValue ? 4 : 8 : width;
        if (width is not (1 or 2 or 4 or 8)) throw new ArgumentOutOfRangeException(nameof(width));
        var bytes = new byte[width];
        for (int i = width - 1; i >= 0; i--) { bytes[i] = (byte)value; value >>= 8; }
        if (value != 0) throw new ArgumentOutOfRangeException(nameof(value));
        return new(name, (byte)(width == 1 ? 2 : width == 2 ? 3 : width == 4 ? 4 : 5), bytes);
    }
    public ulong Number => Type switch
    {
        2 when Data.Length == 1 => Data[0],
        3 when Data.Length == 2 => BinaryPrimitives.ReadUInt16BigEndian(Data),
        4 when Data.Length == 4 => BinaryPrimitives.ReadUInt32BigEndian(Data),
        5 when Data.Length == 8 => BinaryPrimitives.ReadUInt64BigEndian(Data),
        _ => throw new InvalidDataException($"Etiqueta 0x{Name:X4}: entero inválido.")
    };
    public string String => Type == 6 && Data.Length > 0 && Data[^1] == 0
        ? Encoding.UTF8.GetString(Data.AsSpan(0, Data.Length - 1))
        : throw new InvalidDataException($"Etiqueta 0x{Name:X4}: texto inválido.");
    public EcTag? Find(ushort name) => Children.FirstOrDefault(t => t.Name == name);
}

public sealed record EcPacket(byte Operation, params EcTag[] Tags)
{
    public EcTag? Find(ushort name) => Tags.FirstOrDefault(t => t.Name == name);
}

// Wire format checked against aMule 3.0.1 ECSocket.cpp and ECTag.cpp.
// A tag's declared length EXCLUDES its own child-count field.
public static class EcProtocol
{
    public const int MaxPacketBytes = 16 * 1024 * 1024;
    public static byte[] Encode(EcPacket packet)
    {
        using var body = new MemoryStream();
        body.WriteByte(packet.Operation);
        WriteCount(body, packet.Tags.Length);
        foreach (var tag in packet.Tags) WriteTag(body, tag, 0);
        if (body.Length > MaxPacketBytes) throw new InvalidDataException("Paquete demasiado grande.");
        var result = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result, 0x20);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), (uint)body.Length);
        body.GetBuffer().AsSpan(0, (int)body.Length).CopyTo(result.AsSpan(8));
        return result;
    }
    public static EcPacket DecodeBody(ReadOnlySpan<byte> body)
    {
        if (body.Length < 3 || body.Length > MaxPacketBytes) throw new InvalidDataException("Longitud EC inválida.");
        int offset = 1;
        var tags = ReadTags(body, ref offset, ReadCount(body, ref offset), 0);
        if (offset != body.Length) throw new InvalidDataException("Datos sobrantes en paquete EC.");
        return new(body[0], tags);
    }
    private static int WireSize(EcTag tag, int depth)
    {
        if (depth > 24) throw new InvalidDataException("Demasiados niveles EC.");
        return checked(7 + (tag.Children.Length > 0 ? 2 : 0) + tag.Data.Length + tag.Children.Sum(t => WireSize(t, depth + 1)));
    }
    private static void WriteTag(Stream stream, EcTag tag, int depth)
    {
        if (tag.Name > 0x7fff || tag.Type is < 1 or > 10) throw new InvalidDataException("Etiqueta EC inválida.");
        int size = WireSize(tag, depth);
        Span<byte> header = stackalloc byte[7];
        BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)((tag.Name << 1) | (tag.Children.Length > 0 ? 1 : 0)));
        header[2] = tag.Type;
        BinaryPrimitives.WriteUInt32BigEndian(header[3..], (uint)(size - 7 - (tag.Children.Length > 0 ? 2 : 0)));
        stream.Write(header);
        if (tag.Children.Length > 0) WriteCount(stream, tag.Children.Length);
        foreach (var child in tag.Children) WriteTag(stream, child, depth + 1);
        stream.Write(tag.Data);
    }
    private static void WriteCount(Stream stream, int count)
    {
        if (count >= ushort.MaxValue) throw new InvalidDataException("Demasiadas etiquetas EC.");
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)count);
        stream.Write(bytes);
    }
    private static int ReadCount(ReadOnlySpan<byte> bytes, ref int offset)
    {
        Need(bytes, offset, 2);
        int value = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]); offset += 2;
        if (value == ushort.MaxValue) throw new InvalidDataException("Extensión EC no negociada.");
        return value;
    }
    private static EcTag[] ReadTags(ReadOnlySpan<byte> bytes, ref int offset, int count, int depth)
    {
        if (depth > 24 || count > (bytes.Length - offset) / 7) throw new InvalidDataException("Estructura EC inválida.");
        var tags = new EcTag[count];
        for (int i = 0; i < count; i++)
        {
            Need(bytes, offset, 7);
            ushort raw = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
            byte type = bytes[offset + 2];
            uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes[(offset + 3)..]);
            offset += 7;
            if (type is < 1 or > 10 || length > MaxPacketBytes) throw new InvalidDataException("Etiqueta EC inválida.");
            EcTag[] children = [];
            int childBytes = 0;
            if ((raw & 1) != 0)
            {
                int childCount = ReadCount(bytes, ref offset), start = offset;
                children = ReadTags(bytes, ref offset, childCount, depth + 1);
                childBytes = offset - start;
            }
            int ownBytes = checked((int)length - childBytes);
            Need(bytes, offset, ownBytes);
            tags[i] = new((ushort)(raw >> 1), type, bytes.Slice(offset, ownBytes).ToArray(), children);
            offset += ownBytes;
        }
        return tags;
    }
    private static void Need(ReadOnlySpan<byte> bytes, int offset, int count)
    {
        if (count < 0 || offset < 0 || offset > bytes.Length - count) throw new InvalidDataException("Paquete EC truncado.");
    }
}
