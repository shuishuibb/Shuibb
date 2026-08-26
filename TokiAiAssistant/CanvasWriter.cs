using System;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using MapleLib.Helpers;
using MapleLib.WzLib.WzProperties;
using Microsoft.Xna.Framework.Graphics;

namespace TokiAi
{
    /// <summary>
    /// Writes a bitmap into a WZ canvas while keeping the canvas's existing surface format.
    ///
    /// WzPngProperty.PNG runs ImageFormatDetector over the new pixels and overwrites Format with
    /// whatever it guesses. Replacing a BGRA4444 icon with artwork whose alpha happens to look
    /// 1-bit therefore rewrote it as ARGB1555 - the file still parses and the editor still
    /// previews it correctly, but the game reads the canvas with the format it expects and draws
    /// garbage. An icon replacement must not silently change the format the client will use.
    /// </summary>
    public static class CanvasWriter
    {
        /// <summary>
        /// Replaces the artwork on an existing canvas. Returns the format actually written, and
        /// a note when it could not be kept identical to the original.
        /// </summary>
        public static void SetBitmapPreservingFormat(WzCanvasProperty canvas, Bitmap bitmap,
            out WzPngFormat written, out string note)
        {
            note = null;

            if (canvas.PngProperty == null)
            {
                // Brand new canvas: there is no original format to honour, so let MapleLib pick.
                canvas.PngProperty = new WzPngProperty();
                canvas.PngProperty.PNG = bitmap;
                written = canvas.PngProperty.Format;
                return;
            }

            WzPngFormat original = canvas.PngProperty.Format;

            SurfaceFormat surface;
            bool grayscale;
            if (!TryMapFormat(original, out surface, out grayscale))
            {
                // An exotic format this build cannot re-encode. Falling back to the detector is
                // still better than refusing, but say so - the caller surfaces it to the user.
                canvas.PngProperty.PNG = bitmap;
                written = canvas.PngProperty.Format;
                if (written != original)
                    note = "原格式 " + Describe(original) + " 無法重新編碼,已改用 " + Describe(written);
                return;
            }

            (WzPngFormat produced, byte[] pixels) = PngUtility.CompressImageToPngFormat(bitmap, surface, grayscale);
            canvas.PngProperty.SetCompressedBytes(Deflate(pixels), bitmap.Width, bitmap.Height, produced);
            written = produced;

            if (produced != original)
            {
                // 513 and 517 share a SurfaceFormat and are chosen by dimensions, so a 513 canvas
                // whose replacement is a multiple of 16 comes back as 517.
                note = "格式從 " + Describe(original) + " 變成 " + Describe(produced)
                     + "(這兩種共用同一個編碼路徑,由圖片尺寸決定)";
            }
        }

        /// <summary>
        /// Writes a bitmap in an explicitly chosen format. Used by undo, so restoring the old
        /// artwork also restores the format it was stored in rather than re-detecting it.
        /// </summary>
        public static bool SetBitmapWithFormat(WzCanvasProperty canvas, Bitmap bitmap, WzPngFormat format)
        {
            if (canvas == null || bitmap == null)
                return false;
            if (canvas.PngProperty == null)
                canvas.PngProperty = new WzPngProperty();

            SurfaceFormat surface;
            bool grayscale;
            if (!TryMapFormat(format, out surface, out grayscale))
            {
                canvas.PngProperty.PNG = bitmap;
                return false;
            }
            (WzPngFormat produced, byte[] pixels) = PngUtility.CompressImageToPngFormat(bitmap, surface, grayscale);
            canvas.PngProperty.SetCompressedBytes(Deflate(pixels), bitmap.Width, bitmap.Height, produced);
            return produced == format;
        }

        static bool TryMapFormat(WzPngFormat format, out SurfaceFormat surface, out bool grayscale)
        {
            grayscale = false;
            switch (format)
            {
                case WzPngFormat.Format1: surface = SurfaceFormat.Bgra4444; return true;
                case WzPngFormat.Format2: surface = SurfaceFormat.Bgra32; return true;
                case WzPngFormat.Format257: surface = SurfaceFormat.Bgra5551; return true;
                case WzPngFormat.Format513:
                case WzPngFormat.Format517: surface = SurfaceFormat.Bgr565; return true;
                case WzPngFormat.Format3: surface = SurfaceFormat.Dxt3; grayscale = true; return true;
                case WzPngFormat.Format1026: surface = SurfaceFormat.Dxt3; return true;
                case WzPngFormat.Format2050: surface = SurfaceFormat.Dxt5; return true;
                default:
                    surface = SurfaceFormat.Bgra32;
                    return false;
            }
        }

        public static string Describe(WzPngFormat format)
        {
            switch (format)
            {
                case WzPngFormat.Format1: return "BGRA4444";
                case WzPngFormat.Format2: return "BGRA8888";
                case WzPngFormat.Format3: return "DXT3(灰階)";
                case WzPngFormat.Format257: return "ARGB1555";
                case WzPngFormat.Format513: return "RGB565";
                case WzPngFormat.Format517: return "RGB565(16x16)";
                case WzPngFormat.Format1026: return "DXT3";
                case WzPngFormat.Format2050: return "DXT5";
                default: return "格式" + (int)format;
            }
        }

        /// <summary>
        /// Byte-for-byte the shape WzPngProperty.Compress writes: the zlib header followed by a
        /// raw deflate stream and no Adler-32 trailer.
        /// </summary>
        static byte[] Deflate(byte[] raw)
        {
            using (MemoryStream output = new MemoryStream())
            {
                output.WriteByte(0x78);
                output.WriteByte(0x9C);
                using (DeflateStream zip = new DeflateStream(output, CompressionMode.Compress, true))
                    zip.Write(raw, 0, raw.Length);
                return output.ToArray();
            }
        }
    }
}
