using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;
using TinyEXR;
using ValveResourceFormat.IO.ContentFormats.ValveTexture;
using ValveResourceFormat.ResourceTypes;
using ChannelMapping = ValveResourceFormat.CompiledShader.ChannelMapping;

#nullable disable

namespace ValveResourceFormat.IO;

public class TextureContentFile : ContentFile
{
    public SKBitmap Bitmap { get; init; }

    public void AddImageSubFile(string fileName, Func<SKBitmap, byte[]> imageExtractFunction)
    {
        var image = new ImageSubFile()
        {
            FileName = fileName,
            Bitmap = Bitmap,
            ImageExtract = imageExtractFunction
        };

        SubFiles.Add(image);
    }

    protected override void Dispose(bool disposing)
    {
        if (!Disposed && disposing)
        {
            Bitmap.Dispose();
        }

        base.Dispose(disposing);
    }
}

public sealed class ImageSubFile : SubFile
{
    public SKBitmap Bitmap { get; init; }
    public Func<SKBitmap, byte[]> ImageExtract { get; init; }
    public override Func<byte[]> Extract => () => ImageExtract(Bitmap);
}

public sealed class TextureExtract
{
    private static readonly string[] CubemapNames =
    [
        "rt",
        "lf",
        "bk",
        "ft",
        "up",
        "dn",
    ];

    private readonly Texture texture;
    private readonly string fileName;
    private readonly bool isSpriteSheet;
    private readonly bool isCubeMap;
    private readonly bool isArray;

    // Options
    public TextureDecoders.TextureCodec DecodeFlags { get; set; } = TextureDecoders.TextureCodec.Auto;
    
    /// <summary>
    /// PNG compression level (0-9). Lower values = faster encoding, higher values = smaller files.
    /// Default is 1 for faster exports. Set to 4 for better compression.
    /// </summary>
    public int PngCompressionLevel { get; set; } = 1;

    /// <summary>
    /// Should the vtex file be ignored. Defaults to true for files flagged as child resources.
    /// </summary>
    public bool IgnoreVtexFile { get; set; }

    public bool ExportExr => texture.IsHighDynamicRange && !DecodeFlags.HasFlag(TextureDecoders.TextureCodec.ForceLDR);
    public string ImageOutputExtension => ExportExr ? ".exr" : ".png";

    public TextureExtract(Resource resource)
    {
        texture = (Texture)resource.DataBlock;
        fileName = resource.FileName;
        IgnoreVtexFile = FileExtract.IsChildResource(resource);
        isSpriteSheet = texture.ExtraData.ContainsKey(VTexExtraData.SHEET);
        isCubeMap = texture.Flags.HasFlag(VTexFlags.CUBE_TEXTURE);
        isArray = texture.Depth > 1;
    }

    /// <summary>
    /// The vtex content file. Input image(s) come as subfiles.
    /// </summary>
    public ContentFile ToContentFile()
    {
        var rawImage = texture.ReadRawImageData();
        if (rawImage != null)
        {
            return new ContentFile() { Data = rawImage };
        }

        Func<SKBitmap, byte[]> ImageEncode = ExportExr ? ToExrImage : (bitmap => ToPngImage(bitmap, PngCompressionLevel));

        //
        // Multiple images path - with parallel processing for better performance
        //
        if (isArray || isCubeMap)
        {
            var contentFile = new ContentFile()
            {
                FileName = fileName,
            };

            // Pre-generate all bitmap tasks for parallel execution
            var bitmapTasks = new List<(string fileName, Func<byte[]> generator)>();

            for (uint depth = 0; depth < texture.Depth; depth++)
            {
                var outTextureName = Path.GetFileNameWithoutExtension(fileName);

                if (isArray)
                {
                    outTextureName += isCubeMap ? $"_f{depth:D2}" : $"_z{depth:D3}";
                }

                if (!isCubeMap)
                {
                    var currentDepth = depth;
                    var fileName = outTextureName + ImageOutputExtension;
                    
                    bitmapTasks.Add((fileName, () =>
                    {
                        using var bitmap = texture.GenerateBitmap(depth: currentDepth, decodeFlags: DecodeFlags);
                        return ImageEncode(bitmap);
                    }));

                    continue;
                }

                for (var face = 0; face < 6; face++)
                {
                    var currentDepth = depth;
                    var currentFace = face;
                    var fileName = $"{outTextureName}_{CubemapNames[face]}{ImageOutputExtension}";

                    bitmapTasks.Add((fileName, () =>
                    {
                        using var bitmap = texture.GenerateBitmap(depth: currentDepth, face: (Texture.CubemapFace)currentFace, decodeFlags: DecodeFlags);
                        return ImageEncode(bitmap);
                    }));
                }
            }

            // Process bitmaps in parallel if we have multiple images
            if (bitmapTasks.Count > 1)
            {
                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, bitmapTasks.Count)
                };

                // Pre-compute all bitmaps in parallel and cache results
                var results = new ConcurrentDictionary<string, byte[]>();
                
                Parallel.ForEach(bitmapTasks, parallelOptions, task =>
                {
                    try
                    {
                        results[task.fileName] = task.generator();
                    }
                    catch (Exception ex)
                    {
                        // Log error but continue with other textures
                        Console.WriteLine($"Warning: Failed to process texture {task.fileName}: {ex.Message}");
                    }
                });

                // Add cached results as subfiles
                foreach (var result in results)
                {
                    var cachedData = result.Value;
                    contentFile.AddSubFile(result.Key, () => cachedData);
                }
            }
            else
            {
                // Single image, process normally
                foreach (var task in bitmapTasks)
                {
                    contentFile.AddSubFile(task.fileName, task.generator);
                }
            }

            return contentFile;
        }

        var bitmap = texture.GenerateBitmap(decodeFlags: DecodeFlags);

        var vtex = new TextureContentFile()
        {
            Data = IgnoreVtexFile ? null : Encoding.UTF8.GetBytes(ToValveTexture()),
            Bitmap = bitmap,
            FileName = fileName,
        };

        if (TryGetMksData(out var sprites, out var mks))
        {
            vtex.AddSubFile(Path.GetFileName(GetMksFileName()), () => Encoding.UTF8.GetBytes(mks));

            foreach (var (spriteRect, spriteFileName) in sprites)
            {
                vtex.AddImageSubFile(Path.GetFileName(spriteFileName), (bitmap) => SubsetToPngImage(bitmap, spriteRect, PngCompressionLevel));
            }

            return vtex;
        }

        vtex.AddImageSubFile(Path.GetFileName(GetImageFileName()), ImageEncode);
        return vtex;
    }

    public ContentFile ToMaterialMaps(IEnumerable<MaterialExtract.UnpackInfo> mapsToUnpack)
    {
        // unpacking not supported in these scenarios
        if (isCubeMap || isArray || ExportExr)
        {
            // TODO: for cubemaps we should export one image with 'equirecangular' or 'cube' projection
            return ToContentFile();
        }

        var bitmap = texture.GenerateBitmap(decodeFlags: DecodeFlags);
        bitmap.SetImmutable();

        var vtex = new TextureContentFile()
        {
            Bitmap = bitmap,
            FileName = fileName,
        };

        foreach (var unpackInfo in mapsToUnpack)
        {
            vtex.AddImageSubFile(Path.GetFileName(unpackInfo.FileName), (bitmap) => ToPngImageChannels(bitmap, unpackInfo.Channel));
        }

        return vtex;
    }

    public static string GetImageOutputExtension(Texture texture)
    {
        if (texture.IsHighDynamicRange) // todo: also check DecodeFlags for ForceLDR
        {
            return "exr";
        }

        if (texture.IsRawJpeg)
        {
            return "jpeg";
        }

        return "png";
    }

    private string GetImageFileName()
        => Path.ChangeExtension(fileName, ImageOutputExtension);

    private string GetMksFileName()
        => Path.ChangeExtension(fileName, "mks");

    public static byte[] ToPngImage(SKBitmap bitmap, int compressionLevel = 1)
    {
        return EncodePng(bitmap, compressionLevel);
    }

    public static byte[] SubsetToPngImage(SKBitmap bitmap, SKRectI spriteRect, int compressionLevel = 1)
    {
        using var subset = new SKBitmap();
        bitmap.ExtractSubset(subset, spriteRect);

        return EncodePng(subset, compressionLevel);
    }

    public static byte[] ToPngImageChannels(SKBitmap bitmap, ChannelMapping channel)
    {
        if (channel.Count == 1)
        {
            using var newBitmap = new SKBitmap(bitmap.Width, bitmap.Height, SKColorType.Gray8, SKAlphaType.Opaque);
            using var newPixelmap = newBitmap.PeekPixels();
            using var pixelmap = bitmap.PeekPixels();

            var newPixels = newPixelmap.GetPixelSpan<byte>();
            var pixels = pixelmap.GetPixelSpan<SKColor>();

            for (var i = 0; i < pixels.Length; i++)
            {
                newPixels[i] = channel.Channels[0] switch
                {
                    ChannelMapping.Channel.R => pixels[i].Red,
                    ChannelMapping.Channel.G => pixels[i].Green,
                    ChannelMapping.Channel.B => pixels[i].Blue,
                    ChannelMapping.Channel.A => pixels[i].Alpha,
                    _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null)
                };
            }

            return EncodePng(newPixelmap);
        }
        else if (channel == ChannelMapping.RG || channel == ChannelMapping.RGB)
        {
            // Wipe out the alpha channel
            using var newBitmap = new SKBitmap(bitmap.Info);
            using var newPixelmap = newBitmap.PeekPixels();
            using var pixelmap = bitmap.PeekPixels();
            pixelmap.GetPixelSpan<SKColor>().CopyTo(newPixelmap.GetPixelSpan<SKColor>());

            using var alphaPixelmap = newPixelmap.WithAlphaType(SKAlphaType.Opaque);

            return EncodePng(alphaPixelmap);
        }
        else if (channel == ChannelMapping.RGBA)
        {
            return EncodePng(bitmap);
        }
        else
        {
            // Swizzled channels, e.g. alpha-green DXT5nm
            var newBitmapType = bitmap.Info
                .WithAlphaType(channel.Count < 4 ? SKAlphaType.Opaque : SKAlphaType.Unpremul)
                .WithColorType(SKColorType.Rgba8888);
            using var newBitmap = new SKBitmap(newBitmapType);
            using var newPixelmap = newBitmap.PeekPixels();
            using var pixelmap = bitmap.PeekPixels();

            var newPixels = newPixelmap.GetPixelSpan<SKColor>();
            var pixels = pixelmap.GetPixelSpan<SKColor>();

            for (var i = 0; i < pixels.Length; i++)
            {
                var color = (uint)newPixels[i];
                for (var j = 0; j < channel.Count; j++)
                {
                    var c = channel.Channels[j] switch
                    {
                        ChannelMapping.Channel.R => pixels[i].Red,
                        ChannelMapping.Channel.G => pixels[i].Green,
                        ChannelMapping.Channel.B => pixels[i].Blue,
                        ChannelMapping.Channel.A => pixels[i].Alpha,
                        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null)
                    };

                    color |= ((uint)c) << (j * 8);
                }

                newPixels[i] = new SKColor(color);
            }

            return EncodePng(newPixelmap);
        }
    }

    public static void CopyChannel(SKPixmap srcPixels, ChannelMapping srcChannel, SKPixmap dstPixels, ChannelMapping dstChannel)
    {
        if (srcChannel.Count != 1 || dstChannel.Count != 1)
        {
            throw new InvalidOperationException($"Can only copy individual channels. {srcChannel} -> {dstChannel}");
        }

        var srcPixelSpan = srcPixels.GetPixelSpan<SKColor>();
        var pixelSpan = dstPixels.GetPixelSpan<SKColor>();

#pragma warning disable CS8509 // non exhaustive switch
        for (var i = 0; i < srcPixelSpan.Length; i++)
        {
            pixelSpan[i] = dstChannel.Channels[0] switch
            {
                ChannelMapping.Channel.R => srcChannel.Channels[0] switch
                {
                    ChannelMapping.Channel.R => pixelSpan[i].WithRed(srcPixelSpan[i].Red),
                    ChannelMapping.Channel.G => pixelSpan[i].WithRed(srcPixelSpan[i].Green),
                    ChannelMapping.Channel.B => pixelSpan[i].WithRed(srcPixelSpan[i].Blue),
                    ChannelMapping.Channel.A => pixelSpan[i].WithRed(srcPixelSpan[i].Alpha),
                },
                ChannelMapping.Channel.G => srcChannel.Channels[0] switch
                {
                    ChannelMapping.Channel.R => pixelSpan[i].WithGreen(srcPixelSpan[i].Red),
                    ChannelMapping.Channel.G => pixelSpan[i].WithGreen(srcPixelSpan[i].Green),
                    ChannelMapping.Channel.B => pixelSpan[i].WithGreen(srcPixelSpan[i].Blue),
                    ChannelMapping.Channel.A => pixelSpan[i].WithGreen(srcPixelSpan[i].Alpha),
                },
                ChannelMapping.Channel.B => srcChannel.Channels[0] switch
                {
                    ChannelMapping.Channel.R => pixelSpan[i].WithBlue(srcPixelSpan[i].Red),
                    ChannelMapping.Channel.G => pixelSpan[i].WithBlue(srcPixelSpan[i].Green),
                    ChannelMapping.Channel.B => pixelSpan[i].WithBlue(srcPixelSpan[i].Blue),
                    ChannelMapping.Channel.A => pixelSpan[i].WithBlue(srcPixelSpan[i].Alpha),
                },
                ChannelMapping.Channel.A => srcChannel.Channels[0] switch
                {
                    ChannelMapping.Channel.R => pixelSpan[i].WithAlpha(srcPixelSpan[i].Red),
                    ChannelMapping.Channel.G => pixelSpan[i].WithAlpha(srcPixelSpan[i].Green),
                    ChannelMapping.Channel.B => pixelSpan[i].WithAlpha(srcPixelSpan[i].Blue),
                    ChannelMapping.Channel.A => pixelSpan[i].WithAlpha(srcPixelSpan[i].Alpha),
                },
            };
        }
#pragma warning restore CS8509 // non exhaustive switch
    }

    /// <summary>
    /// Packs masks to a new texture.
    /// </summary>
    public class TexturePacker : IDisposable
    {
        private static readonly SKSamplingOptions SamplingOptions = new(SKFilterMode.Linear, SKMipmapMode.None);

        public SKColor DefaultColor { get; init; } = SKColors.Black;
        public SKBitmap Bitmap { get; private set; }
        private readonly HashSet<ChannelMapping> Packed = [];

        public void Collect(SKPixmap srcPixels, ChannelMapping srcChannel, ChannelMapping dstChannel, string fileName)
        {
            if (!Packed.Add(dstChannel))
            {
                Console.WriteLine($"{dstChannel} has already been packed in texture: {fileName}");
            }

            if (Bitmap is null)
            {
                Bitmap = new SKBitmap(srcPixels.Width, srcPixels.Height, true);
                if (DefaultColor != SKColors.Black)
                {
                    using var pixels = Bitmap.PeekPixels();
                    pixels.GetPixelSpan<SKColor>().Fill(DefaultColor);
                }
            }


            if (Bitmap.Width < srcPixels.Width || Bitmap.Height < srcPixels.Height)
            {
                var newBitmap = new SKBitmap(srcPixels.Width, srcPixels.Height, true);
                using (Bitmap)
                {
                    // Scale Bitmap up to srcPixels size
                    using var oldPixels = Bitmap.PeekPixels();
                    using var newPixels = newBitmap.PeekPixels();
                    if (!oldPixels.ScalePixels(newPixels, SamplingOptions))
                    {
                        throw new InvalidOperationException($"Failed to scale up pixels of {fileName}");
                    }
                }
                Bitmap = newBitmap;
            }
            else if (Bitmap.Width > srcPixels.Width || Bitmap.Height > srcPixels.Height)
            {
                // Scale srcPixels up to Bitmap size
                using var newSrcBitmap = new SKBitmap(Bitmap.Width, Bitmap.Height, true);
                using var newSrcPixels = newSrcBitmap.PeekPixels();
                if (!srcPixels.ScalePixels(newSrcPixels, SamplingOptions))
                {
                    throw new InvalidOperationException($"Failed to scale up incoming pixels for {fileName}");
                }
                using var dstPixels2 = Bitmap.PeekPixels();
                CopyChannel(newSrcPixels, srcChannel, dstPixels2, dstChannel);
                return;
            }

            using var dstPixels = Bitmap.PeekPixels();
            CopyChannel(srcPixels, srcChannel, dstPixels, dstChannel);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (Bitmap != null && disposing)
            {
                Bitmap.Dispose();
                Bitmap = null;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }

    private static byte[] EncodePng(SKBitmap bitmap)
    {
        using var pixels = bitmap.PeekPixels();
        return EncodePng(pixels);
    }

    private static byte[] EncodePng(SKPixmap pixels, int compressionLevel = 1)
    {
        var options = new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, zLibLevel: compressionLevel);

        using var png = pixels.Encode(options);
        return png.ToArray();
    }

    public static byte[] ToExrImage(SKBitmap bitmap)
    {
        using var pixels = bitmap.PeekPixels();
        return ToExrImage(pixels);
    }

    public static byte[] ToExrImage(SKPixmap pixels)
    {
        var pixelSpan = pixels.GetPixelSpan<SKColorF>();
        var floatSpan = MemoryMarshal.Cast<SKColorF, float>(pixelSpan);
        var result = Exr.SaveEXRToMemory(floatSpan, pixels.Width, pixels.Height, components: 4, asFp16: false, out var exrData);
        if (result != ResultCode.Success)
        {
            throw new InvalidOperationException($"Got result {result} while saving EXR image");
        }

        return exrData;
    }

    public bool TryGetMksData(out Dictionary<SKRectI, string> sprites, out string mks)
    {
        mks = string.Empty;
        sprites = [];

        if (!isSpriteSheet)
        {
            return false;
        }

        var spriteSheetData = texture.GetSpriteSheetData();

        var mksBuilder = new StringBuilder();
        var textureName = Path.GetFileNameWithoutExtension(fileName);
        var packmodeNonFlat = false;

        for (var s = 0; s < spriteSheetData.Sequences.Length; s++)
        {
            var sequence = spriteSheetData.Sequences[s];
            mksBuilder.AppendLine();

            switch (sequence.NoColor, sequence.NoAlpha)
            {
                case (false, false):
                    mksBuilder.AppendLine(CultureInfo.InvariantCulture, $"sequence {s}");
                    break;

                case (false, true):
                    mksBuilder.AppendLine(CultureInfo.InvariantCulture, $"sequence-rgb {s}");
                    packmodeNonFlat = true;
                    break;

                case (true, false):
                    mksBuilder.AppendLine(CultureInfo.InvariantCulture, $"sequence-a {s}");
                    packmodeNonFlat = true;
                    break;

                case (true, true):
                    throw new InvalidDataException($"Unexpected combination of {nameof(sequence.NoColor)} and {nameof(sequence.NoAlpha)}");
            }

            if (!sequence.Clamp)
            {
                mksBuilder.AppendLine("LOOP");
            }

            for (var f = 0; f < sequence.Frames.Length; f++)
            {
                var frame = sequence.Frames[f];

                var imageFileName = sequence.Frames.Length == 1
                    ? $"{textureName}_seq{s}.png"
                    : $"{textureName}_seq{s}_{f}.png";

                // These images seem to be duplicates. So only extract the first one.
                var image = frame.Images[0];
                var imageRect = image.GetCroppedRect(texture.ActualWidth, texture.ActualHeight);

                if (imageRect.Size.Width == 0 || imageRect.Size.Height == 0)
                {
                    continue;
                }

                var displayTime = frame.DisplayTime;
                if (sequence.Clamp && displayTime == 0)
                {
                    displayTime = 1;
                }

                sprites.TryAdd(imageRect, imageFileName);
                mksBuilder.AppendLine(CultureInfo.InvariantCulture, $"frame {sprites[imageRect]} {displayTime.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        if (packmodeNonFlat)
        {
            mksBuilder.Insert(0, "packmode rgb+a\n");
        }

        mksBuilder.Insert(0, $"// Reconstructed with {StringToken.VRF_GENERATOR}\n\n");
        mks = mksBuilder.ToString();
        return true;
    }

    private string GetInputFileNameForVtex()
    {
        if (isSpriteSheet)
        {
            return GetMksFileName().Replace(Path.DirectorySeparatorChar, '/');
        }

        return GetImageFileName().Replace(Path.DirectorySeparatorChar, '/');
    }

    public string ToValveTexture()
    {
        var inputTextureFileName = GetInputFileNameForVtex();
        var outputFormat = texture.Format.ToString();

        using var datamodel = new Datamodel.Datamodel("vtex", 1);
        datamodel.Root = CDmeVtex.CreateTexture2D([(inputTextureFileName, "rgba", "Box")], outputFormat);

        using var stream = new MemoryStream();
        datamodel.Save(stream, "keyvalues2_noids", 1);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

