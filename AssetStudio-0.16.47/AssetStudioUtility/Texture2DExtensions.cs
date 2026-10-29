using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.IO;

namespace AssetStudio
{
    public static class Texture2DExtensions
    {
        public static Image<Bgra32> ConvertToImage(this Texture2D m_Texture2D, bool flip)
        {
            var converter = new Texture2DConverter(m_Texture2D);
            if (!converter.CanDecode || converter.Width <= 0 || converter.Height <= 0)
            {
                return null;
            }
            var pixelCount = (long)converter.Width * converter.Height;
            if (pixelCount <= 0 || pixelCount > int.MaxValue / 4)
            {
                return null;
            }
            var buff = BigArrayPool<byte>.Shared.Rent((int)(pixelCount * 4));
            try
            {
                if (converter.DecodeTexture2D(buff))
                {
                    try
                    {
                        var image = Image.LoadPixelData<Bgra32>(buff, converter.Width, converter.Height);
                        if (flip)
                        {
                            image.Mutate(x => x.Flip(FlipMode.Vertical));
                        }
                        return image;
                    }
                    catch
                    {
                        return null;
                    }
                }
                return null;
            }
            finally
            {
                BigArrayPool<byte>.Shared.Return(buff);
            }
        }

        public static MemoryStream ConvertToStream(this Texture2D m_Texture2D, ImageFormat imageFormat, bool flip)
        {
            var image = ConvertToImage(m_Texture2D, flip);
            if (image != null)
            {
                using (image)
                {
                    return image.ConvertToStream(imageFormat);
                }
            }
            return null;
        }
    }
}
