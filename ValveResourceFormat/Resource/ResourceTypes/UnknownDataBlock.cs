using System;
using System.IO;
using System.Text;

namespace ValveResourceFormat.ResourceTypes
{
    /// <summary>
    /// Unknown resource data.
    /// </summary>
    public class UnknownDataBlock(ResourceType ResourceType) : Block
    {
        public override BlockType Type => BlockType.DATA;

        public byte[] Data { get; private set; } = Array.Empty<byte>();

        public override void Read(BinaryReader reader)
        {
            reader.BaseStream.Position = Offset;
            Data = reader.ReadBytes((int)Size);
        }

        public override void WriteText(IndentedTextWriter writer)
        {
            writer.WriteLine($"Unknown DATA block for resource type {ResourceType} (total size: {Size} bytes)");

            if (Data.Length == 0)
            {
                return;
            }

            const int bytesPerLine = 16;
            var maxPreview = Math.Min(Data.Length, 1024);

            for (var i = 0; i < maxPreview; i += bytesPerLine)
            {
                var lineCount = Math.Min(bytesPerLine, maxPreview - i);

                var offsetText = i.ToString("X8");

                var hexBuilder = new StringBuilder(bytesPerLine * 3);
                for (var b = 0; b < lineCount; b++)
                {
                    hexBuilder.Append(Data[i + b].ToString("X2"));
                    if (b != lineCount - 1)
                    {
                        hexBuilder.Append(' ');
                    }
                }

                if (lineCount < bytesPerLine)
                {
                    hexBuilder.Append(' ');
                    var missing = (bytesPerLine - lineCount) * 3 - 1;
                    hexBuilder.Append(' ', missing);
                }

                var asciiBuilder = new StringBuilder(bytesPerLine);
                for (var b = 0; b < lineCount; b++)
                {
                    var c = Data[i + b];
                    asciiBuilder.Append(c is >= 32 and <= 126 ? (char)c : '.');
                }

                writer.WriteLine($"{offsetText}  {hexBuilder}  |{asciiBuilder}|");
            }

            if (Data.Length > maxPreview)
            {
                var remaining = Data.Length - maxPreview;
                writer.WriteLine($"... ({remaining} more bytes not shown)");
            }
        }
    }
}
