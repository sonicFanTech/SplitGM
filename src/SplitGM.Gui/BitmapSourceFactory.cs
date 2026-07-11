#nullable enable

using System.IO;
using System.Windows.Media.Imaging;

namespace SplitGM.Gui;

internal static class BitmapSourceFactory
{
    public static BitmapImage? FromBytes(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return null;

        using MemoryStream stream = new(bytes, writable: false);
        BitmapImage image = new();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
