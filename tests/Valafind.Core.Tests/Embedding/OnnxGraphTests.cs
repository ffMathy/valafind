#pragma warning disable CA1707 // xunit test names use underscores for readability

using System.Text;
using Valafind.Core.Embedding;
using Xunit;

namespace Valafind.Core.Tests.Embedding;

public sealed class OnnxGraphTests
{
    [Fact]
    public void Lists_the_names_of_nodes_of_one_type_in_graph_order()
    {
        var model = Model(
            Node("GatherBlockQuantized", "/model/embed_tokens/Gather_Quant"),
            Node("Unsqueeze", "node_unsqueeze"),
            Node("Unsqueeze", "node_unsqueeze_1"),
            Node("Unsqueeze", name: null), // unnamed: skipped
            Node("GatherBlockQuantizedX", "not_this_one"));

        Assert.Equal(["/model/embed_tokens/Gather_Quant"], OnnxGraph.NodeNames(model, "GatherBlockQuantized"));
        Assert.Equal(["node_unsqueeze", "node_unsqueeze_1"], OnnxGraph.NodeNames(model, "Unsqueeze"));
        Assert.Empty(OnnxGraph.NodeNames(model, "Conv"));
    }

    [Fact]
    public void Picks_the_nodes_the_webgpu_provider_must_leave_on_the_cpu()
    {
        var text = Model(Node("GatherBlockQuantized", "/model/embed_tokens/Gather_Quant"), Node("Unsqueeze", "/model/x/Unsqueeze"));
        var audio = Model(Node("Unsqueeze", "node_unsqueeze"), Node("Unsqueeze", "node_unsqueeze_1"), Node("Conv", "node_Conv_1"));

        Assert.Equal(["/model/embed_tokens/Gather_Quant"], WebGpuDevice.CpuNodes(text));
        Assert.Equal(["node_unsqueeze_1"], WebGpuDevice.CpuNodes(audio));
    }

    [Theory]
    [InlineData(new byte[] { 0x3A, 0x10, 0x01 })]   // graph field claims 16 bytes, has 1
    [InlineData(new byte[] { 0x3B })]               // wire type 3 (groups) is not used by ONNX
    [InlineData(new byte[] { 0x08, 0xFF })]          // varint never ends
    public void Rejects_malformed_bytes(byte[] model) =>
        Assert.Throws<InvalidDataException>(() => OnnxGraph.NodeNames(model, "Conv"));

    // ------------------------------------------------------------------ tiny protobuf writer

    private static byte[] Model(params byte[][] nodes)
    {
        var graph = new MemoryStream();
        foreach (var node in nodes)
            Len(graph, 1, node);
        Len(graph, 2, Encoding.UTF8.GetBytes("main")); // GraphProto.name
        var model = new MemoryStream();
        Varint(model, 1 << 3); // ir_version
        Varint(model, 10);
        Len(model, 7, graph.ToArray());
        return model.ToArray();
    }

    private static byte[] Node(string opType, string? name)
    {
        var node = new MemoryStream();
        Len(node, 1, Encoding.UTF8.GetBytes("x")); // input
        Len(node, 2, Encoding.UTF8.GetBytes("y")); // output
        if (name is not null)
            Len(node, 3, Encoding.UTF8.GetBytes(name));
        Len(node, 4, Encoding.UTF8.GetBytes(opType));
        var attribute = new MemoryStream(); // an int attribute, as real nodes have
        Len(attribute, 1, Encoding.UTF8.GetBytes("axis"));
        Varint(attribute, 3 << 3);
        Varint(attribute, 1);
        Len(node, 5, attribute.ToArray());
        return node.ToArray();
    }

    private static void Varint(Stream s, ulong v)
    {
        while (v >= 0x80)
        {
            s.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }
        s.WriteByte((byte)v);
    }

    private static void Len(Stream s, int field, byte[] payload)
    {
        Varint(s, (ulong)(field << 3 | 2));
        Varint(s, (ulong)payload.Length);
        s.Write(payload);
    }
}
