using System.Text;

namespace Valafind.Core.Embedding;

/// <summary>
/// Reads node names out of an ONNX <c>ModelProto</c> (protobuf wire format) without an ONNX library. Only the graph
/// file is read; external weights (<c>.onnx_data</c>) are not touched.
/// </summary>
internal static class OnnxGraph
{
    // Field numbers from onnx.proto3.
    private const int ModelGraphField = 7;   // ModelProto.graph
    private const int GraphNodeField = 1;    // GraphProto.node
    private const int NodeNameField = 3;     // NodeProto.name
    private const int NodeOpTypeField = 4;   // NodeProto.op_type

    private const int WireVarint = 0, Wire64 = 1, WireLen = 2, Wire32 = 5;

    /// <summary>Names of the top-level nodes of type <paramref name="opType"/>, in graph order (unnamed nodes are skipped).</summary>
    /// <exception cref="InvalidDataException">The bytes are not a well-formed protobuf message.</exception>
    public static List<string> NodeNames(byte[] model, string opType)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(opType);
        var wanted = Encoding.UTF8.GetBytes(opType);
        var names = new List<string>();
        foreach (var graph in Fields(model, 0, model.Length))
        {
            if (graph.Number != ModelGraphField || graph.Wire != WireLen)
                continue;
            foreach (var node in Fields(model, graph.Start, graph.End))
            {
                if (node.Number != GraphNodeField || node.Wire != WireLen)
                    continue;
                string? name = null;
                var matches = false;
                foreach (var field in Fields(model, node.Start, node.End))
                {
                    if (field.Wire != WireLen)
                        continue;
                    if (field.Number == NodeNameField)
                        name = Encoding.UTF8.GetString(model, field.Start, field.End - field.Start);
                    else if (field.Number == NodeOpTypeField)
                        matches = model.AsSpan(field.Start, field.End - field.Start).SequenceEqual(wanted);
                }
                if (matches && !string.IsNullOrEmpty(name))
                    names.Add(name);
            }
        }
        return names;
    }

    /// <summary>One field of a message: its number, wire type and (for length-delimited fields) payload bounds.</summary>
    private readonly record struct Field(int Number, int Wire, int Start, int End);

    /// <summary>The fields of the message in <c>bytes[start..end]</c>.</summary>
    private static List<Field> Fields(byte[] bytes, int start, int end)
    {
        var fields = new List<Field>();
        var i = start;
        while (i < end)
        {
            var key = ReadVarint(bytes, ref i, end);
            var number = (int)(key >> 3);
            var wire = (int)(key & 7);
            var payloadStart = i;
            switch (wire)
            {
                case WireVarint:
                    ReadVarint(bytes, ref i, end);
                    break;
                case Wire64:
                    i += 8;
                    break;
                case Wire32:
                    i += 4;
                    break;
                case WireLen:
                    var length = ReadVarint(bytes, ref i, end);
                    if (length > (ulong)(end - i))
                        throw new InvalidDataException("Truncated ONNX model.");
                    payloadStart = i;
                    i += (int)length;
                    break;
                default:
                    throw new InvalidDataException($"Unsupported protobuf wire type {wire} in ONNX model.");
            }
            if (number <= 0 || i > end)
                throw new InvalidDataException("Malformed ONNX model.");
            fields.Add(new Field(number, wire, payloadStart, i));
        }
        return fields;
    }

    private static ulong ReadVarint(byte[] bytes, ref int i, int end)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (i >= end)
                throw new InvalidDataException("Truncated ONNX model.");
            var b = bytes[i++];
            result |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80)
                return result;
        }
        throw new InvalidDataException("Malformed varint in ONNX model.");
    }
}
