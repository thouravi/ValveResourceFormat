using NUnit.Framework;
using System.IO;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace Tests
{
    public class UnknownDataBlockTest
    {
        [Test]
        public void UnknownDataBlock_WritesHexPreview()
        {
            var bytes = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x41, 0x42, 0x43, 0x7E, 0x7F, 0x80, 0xFF };

            using var ms = new MemoryStream(bytes);
            using var reader = new BinaryReader(ms);

            var block = new UnknownDataBlock(ResourceType.Unknown)
            {
                Offset = 0,
                Size = (uint)bytes.Length,
            };

            block.Read(reader);

            using var writer = new IndentedTextWriter();
            block.WriteText(writer);
            var output = writer.ToString();

            StringAssert.Contains("Unknown DATA block for resource type Unknown", output);
            StringAssert.Contains("00000000", output);
            StringAssert.Contains("00 11 22 33 41 42 43 7E 7F 80 FF", output);
            StringAssert.Contains("|.." , output); // contains printable mapping
        }
    }
}