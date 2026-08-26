using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HaCreator.MapSimulator.UI;
using HaCreator.WorldMap;
using HaSharedLibrary.Util;
using MapleLib.Converters;
using MapleLib.Img;
using MapleLib.WzLib;
using Microsoft.Xna.Framework.Graphics;

namespace HaCreator.MapSimulator;

public partial class MapSimulator
{
    private IDataSource _configuredWorldMapSource;
    private IReadOnlyList<WorldMapDocument> _configuredWorldMapDocuments = Array.Empty<WorldMapDocument>();

    /// <summary>
    /// Completes the existing WorldMapUI configuration hook with the same native
    /// parser used by the authoring workspace. This runs only when the client UI
    /// world map is explicitly opened; textures are retained by the simulator pool.
    /// </summary>
    private void ConfigureNativeWorldMapSurfaces(WorldMapUI worldMapWindow)
    {
        if (worldMapWindow == null || Program.DataSource == null || GraphicsDevice == null)
            return;

        if (!ReferenceEquals(_configuredWorldMapSource, Program.DataSource))
        {
            _configuredWorldMapSource = Program.DataSource;
            _configuredWorldMapDocuments = LoadNativeWorldMapDocuments(Program.DataSource);
        }

        worldMapWindow.ConfigureWorldMapDocuments(_configuredWorldMapDocuments, ResolveWorldMapTexture);
    }

    private static IReadOnlyList<WorldMapDocument> LoadNativeWorldMapDocuments(IDataSource source)
    {
        var documents = new List<WorldMapDocument>();
        IEnumerable<string> names = source.GetImageNamesInDirectory("Map", "WorldMap") ?? Enumerable.Empty<string>();
        foreach (string rawName in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string imageName = Path.GetFileNameWithoutExtension(rawName);
            if (WorldMapExclusionList.IsExclusionImage(imageName))
                continue;
            WzImage image = source.GetImage("Map", $"WorldMap/{imageName}.img")
                ?? source.GetImageByPath($"Map/WorldMap/{imageName}.img");
            if (image == null)
                continue;
            try { documents.Add(WorldMapCodec.Read(image)); }
            catch { /* A malformed surface stays unavailable without breaking the client UI. */ }
        }
        return documents;
    }

    private Texture2D ResolveWorldMapTexture(WorldMapCanvasRef canvas)
    {
        if (canvas?.RawProperty == null)
            return null;
        string key = canvas.RawProperty.FullPath ?? $"WorldMap/{canvas.RawProperty.Name}/{canvas.Width}x{canvas.Height}";
        Texture2D texture = _texturePool?.GetTexture(key);
        if (texture != null)
            return texture;
        using var bitmap = canvas.RawProperty.GetLinkedWzCanvasBitmap();
        texture = bitmap?.ToTexture2D(GraphicsDevice);
        if (texture != null)
            _texturePool?.AddTextureToPool(key, texture);
        return texture;
    }
}
