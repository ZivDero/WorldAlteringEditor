using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Rampastring.Tools;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TSMapEditor.CCEngine;
using TSMapEditor.Models;
using TSMapEditor.Settings;

namespace TSMapEditor.Rendering
{
    /// <summary>
    /// An interface for an object that can be used to fetch 
    /// game logic related information about a theater.
    /// </summary>
    public interface ITheater
    {
        int GetTileSetId(int uniqueTileIndex);
        int TileCount { get; }
        ITileImage GetTile(int id);
        int GetOverlayFrameCount(OverlayType overlayType);
        Theater Theater { get; }
    }

    public class ObjectImage
    {
        public ObjectImage(GraphicsDevice graphicsDevice, ShpFile shp, byte[] shpFileData, Palette palette, List<int> framesToLoad = null, bool remapable = false, PositionedTexture pngTexture = null)
        {
            if (pngTexture != null && !remapable)
            {
                Frames = new PositionedTexture[] { pngTexture };
                return;
            }

            Frames = new PositionedTexture[shp.FrameCount];
            if (remapable && Constants.HQRemap)
                RemapFrames = new PositionedTexture[Frames.Length];

            for (int i = 0; i < shp.FrameCount; i++)
            {
                if (framesToLoad != null && !framesToLoad.Contains(i))
                    continue;

                var frameInfo = shp.GetShpFrameInfo(i);
                byte[] frameData = shp.GetUncompressedFrameData(i, shpFileData);
                if (frameData == null)
                    continue;

                var texture = new Texture2D(graphicsDevice, frameInfo.Width, frameInfo.Height, false, SurfaceFormat.Color);
                Color[] colorArray = frameData.Select(b => b == 0 ? Color.Transparent : palette.Data[b].ToXnaColor()).ToArray();
                texture.SetData<Color>(colorArray);
                Frames[i] = new PositionedTexture(shp.Width, shp.Height, frameInfo.XOffset, frameInfo.YOffset, texture);

                if (remapable && Constants.HQRemap)
                {
                    if (Constants.HQRemap)
                    {
                        // Fetch remap colors from the array

                        Color[] remapColorArray = frameData.Select(b =>
                        {
                            if (b >= 0x10 && b <= 0x1F)
                            {
                                // This is a remap color, convert to grayscale
                                Color xnaColor = palette.Data[b].ToXnaColor();
                                float value = Math.Max(xnaColor.R / 255.0f, Math.Max(xnaColor.G / 255.0f, xnaColor.B / 255.0f));

                                // Brighten it up a bit
                                value *= Constants.RemapBrightenFactor;
                                return new Color(value, value, value);
                            }

                            return Color.Transparent;
                        }).ToArray();

                        var remapTexture = new Texture2D(graphicsDevice, frameInfo.Width, frameInfo.Height, false, SurfaceFormat.Color);
                        remapTexture.SetData<Color>(remapColorArray);
                        RemapFrames[i] = new PositionedTexture(shp.Width, shp.Height, frameInfo.XOffset, frameInfo.YOffset, remapTexture);
                    }
                    else
                    {
                        // Convert colors to grayscale
                        // Get HSV value, change S = 0, convert back to RGB and assign
                        // With S = 0, the formula for converting HSV to RGB can be reduced to a quite simple form :)

                        System.Drawing.Color[] sdColorArray = colorArray.Select(c => System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B)).ToArray();
                        for (int j = 0; j < sdColorArray.Length; j++)
                        {
                            if (colorArray[j] == Color.Transparent)
                                continue;

                            float value = sdColorArray[j].GetBrightness() * Constants.RemapBrightenFactor;
                            if (value > 1.0f)
                                value = 1.0f;
                            colorArray[j] = new Color(value, value, value);
                        }

                        var remapTexture = new Texture2D(graphicsDevice, frameInfo.Width, frameInfo.Height, false, SurfaceFormat.Color);
                        remapTexture.SetData<Color>(colorArray);
                        Frames[i] = new PositionedTexture(shp.Width, shp.Height, frameInfo.XOffset, frameInfo.YOffset, remapTexture);
                    }
                }
            }
        }

        public void Dispose()
        {
            Array.ForEach(Frames, f =>
            {
                if (f != null)
                    f.Dispose();
            });

            if (RemapFrames != null)
            {
                Array.ForEach(RemapFrames, f =>
                {
                    if (f != null)
                        f.Dispose();
                });
            }
        }

        public PositionedTexture[] Frames { get; set; }
        public PositionedTexture[] RemapFrames { get; set; }
    }

    public class PositionedTexture
    {
        public int ShapeWidth;
        public int ShapeHeight;
        public int OffsetX;
        public int OffsetY;
        public Texture2D Texture;

        public PositionedTexture(int shapeWidth, int shapeHeight, int offsetX, int offsetY, Texture2D texture)
        {
            ShapeWidth = shapeWidth;
            ShapeHeight = shapeHeight;
            OffsetX = offsetX;
            OffsetY = offsetY;
            Texture = texture;
        }

        public void Dispose()
        {
            if (Texture != null)
                Texture.Dispose();
        }
    }

    /// <summary>
    /// Graphical layer for the theater.
    /// </summary>
    public class TheaterGraphics : ITheater
    {
        private const string SHP_FILE_EXTENSION = ".SHP";
        private const string PNG_FILE_EXTENSION = ".PNG";

        public TheaterGraphics(GraphicsDevice graphicsDevice, Theater theater, CCFileManager fileManager, Rules rules)
        {
            this.graphicsDevice = graphicsDevice;
            Theater = theater;
            this.fileManager = fileManager;

            theaterPalette = GetPaletteOrFail(theater.TerrainPaletteName);
            unitPalette = GetPaletteOrFail(Theater.UnitPaletteName);
            animPalette = GetPaletteOrFail("anim.pal");
            if (!string.IsNullOrEmpty(Theater.TiberiumPaletteName))
                tiberiumPalette = GetPaletteOrFail(Theater.TiberiumPaletteName);

            if (UserSettings.Instance.MultithreadedTextureLoading)
            {
                var task1 = Task.Factory.StartNew(() => ReadTileTextures());
                var task2 = Task.Factory.StartNew(() => ReadTerrainObjectTextures(rules.TerrainTypes));
                var task3 = Task.Factory.StartNew(() => ReadBuildingTextures(rules.BuildingTypes));
                var task4 = Task.Factory.StartNew(() => ReadUnitTextures(rules.UnitTypes));
                var task5 = Task.Factory.StartNew(() => ReadInfantryTextures(rules.InfantryTypes));
                var task6 = Task.Factory.StartNew(() => ReadOverlayTextures(rules.OverlayTypes));
                var task7 = Task.Factory.StartNew(() => ReadSmudgeTextures(rules.SmudgeTypes));
                var task8 = Task.Factory.StartNew(() => ReadAnimTextures(rules.AnimTypes));
                Task.WaitAll(task1, task2, task3, task4, task5, task6, task7, task8);
            }
            else
            {
                ReadTileTextures();
                ReadTerrainObjectTextures(rules.TerrainTypes);
                ReadBuildingTextures(rules.BuildingTypes);
                ReadUnitTextures(rules.UnitTypes);
                ReadInfantryTextures(rules.InfantryTypes);
                ReadOverlayTextures(rules.OverlayTypes);
                ReadSmudgeTextures(rules.SmudgeTypes);
                ReadAnimTextures(rules.AnimTypes);
            }

            LoadBuildingZData();
        }

        private readonly GraphicsDevice graphicsDevice;


        private static string[] NewTheaterHardcodedPrefixes = new string[] { "CA", "CT", "GA", "GT", "NA", "NT" };

        private void LoadBuildingZData()
        {
            return;

            var buildingZData = fileManager.LoadFile("BUILDNGZ.SHP");

            byte[] rgbBuffer = new byte[256 * 3];
            for (int i = 0; i < 256; i++)
            {
                rgbBuffer[i * 3] = (byte)(i / 4);
                rgbBuffer[(i * 3) + 1] = (byte)(i / 4);
                rgbBuffer[(i * 3) + 2] = (byte)(i / 4);
            }

            // for (int i = 16; i < 108; i++)
            // {
            //     byte color = (byte)((i - 16) * (256 / 92.0));
            //     rgbBuffer[i * 3] = (byte)(color / 4);
            //     rgbBuffer[(i * 3) + 1] = (byte)(color / 4);
            //     rgbBuffer[(i * 3) + 2] = (byte)(color / 4);
            // }

            var palette = new Palette(rgbBuffer);

            var shpFile = new ShpFile();
            shpFile.ParseFromBuffer(buildingZData);
            BuildingZ = new ObjectImage(graphicsDevice, shpFile, buildingZData, palette);
        }

        private void ReadTileTextures()
        {
            Logger.Log("Loading tile textures.");

            int currentTileIndex = 0; // Used for setting the starting tile ID of a tileset

            for (int tsId = 0; tsId < Theater.TileSets.Count; tsId++)
            {
                TileSet tileSet = Theater.TileSets[tsId];
                tileSet.StartTileIndex = currentTileIndex;
                tileSet.LoadedTileCount = 0;

                Console.WriteLine("Loading " + tileSet.SetName);

                for (int i = 0; i < tileSet.TilesInSet; i++)
                {
                    // Console.WriteLine("#" + i);

                    var tileGraphics = new List<TileImage>();

                    // Handle graphics variation (clear00.tem, clear00a.tem, clear00b.tem etc.)
                    for (int v = 0; v < 'g' - 'a'; v++)
                    {
                        string baseName = tileSet.FileName + (i + 1).ToString("D2", CultureInfo.InvariantCulture);

                        if (v > 0)
                        {
                            baseName = baseName + ((char)('a' + (v - 1)));
                        }

                        byte[] data = fileManager.LoadFile(baseName + Theater.FileExtension);

                        if (data == null)
                        {
                            if (v == 0)
                            {
                                tileGraphics.Add(new TileImage(0, 0, tsId, i, currentTileIndex, new MGTMPImage[0]));
                                break;
                            }
                            else
                            {
                                break;
                            }
                        }

                        var tmpFile = new TmpFile();
                        tmpFile.ParseFromBuffer(data);

                        var tmpImages = new List<MGTMPImage>();
                        for (int img = 0; img < tmpFile.ImageCount; img++)
                        {
                            tmpImages.Add(new MGTMPImage(graphicsDevice, tmpFile.GetImage(img), theaterPalette, tsId));
                        }
                        tileGraphics.Add(new TileImage(tmpFile.CellsX, tmpFile.CellsY, tsId, i, currentTileIndex, tmpImages.ToArray()));
                    }

                    tileSet.LoadedTileCount++;
                    currentTileIndex++;
                    terrainGraphicsList.Add(tileGraphics.ToArray());
                }
            }

            Logger.Log("Assigning marble madness mode tile textures.");

            // Assign marble-madness (MM) mode tile graphics
            int tileIndex = 0;
            for (int tsId = 0; tsId < Theater.TileSets.Count; tsId++)
            {
                TileSet tileSet = Theater.TileSets[tsId];
                if (tileSet.NonMarbleMadness > -1 || tileSet.MarbleMadness < 0 || tileSet.MarbleMadness >= Theater.TileSets.Count)
                {
                    // This is a MM tileset or a tileset with no MM graphics
                    for (int i = 0; i < tileSet.LoadedTileCount; i++)
                    {
                        mmTerrainGraphicsList.Add(terrainGraphicsList[tileIndex + i]);
                        hasMMGraphics.Add(tileSet.NonMarbleMadness > -1);
                    }

                    tileIndex += tileSet.LoadedTileCount;
                    continue;
                }

                // For non-MM tilesets with MM graphics, fetch the MM tileset
                TileSet mmTileSet = Theater.TileSets[tileSet.MarbleMadness];
                for (int i = 0; i < tileSet.LoadedTileCount; i++)
                {
                    mmTerrainGraphicsList.Add(terrainGraphicsList[mmTileSet.StartTileIndex + i]);
                    hasMMGraphics.Add(true);
                }
                tileIndex += tileSet.LoadedTileCount;
            }

            Logger.Log("Finished loading tile textures.");
        }

        public void ReadTerrainObjectTextures(List<TerrainType> terrainTypes)
        {
            Logger.Log("Loading terrain object textures.");

            var unitPalette = GetPaletteOrFail(Theater.UnitPaletteName);

            TerrainObjectTextures = new ObjectImage[terrainTypes.Count];
            for (int i = 0; i < terrainTypes.Count; i++)
            {
                string shpFileName = terrainTypes[i].Image != null ? terrainTypes[i].Image : terrainTypes[i].ININame;
                string pngFileName = shpFileName + PNG_FILE_EXTENSION;

                if (terrainTypes[i].Theater)
                    shpFileName += Theater.FileExtension;
                else
                    shpFileName += SHP_FILE_EXTENSION;

                byte[] data = fileManager.LoadFile(pngFileName);

                if (data != null)
                {
                    // Load graphics as PNG

                    TerrainObjectTextures[i] = new ObjectImage(graphicsDevice, null, null, null, null, false, PositionedTextureFromBytes(data));
                }
                else
                {
                    // Try to load graphics as SHP

                    data = fileManager.LoadFile(shpFileName);

                    if (data == null)
                        continue;

                    var shpFile = new ShpFile(shpFileName);
                    shpFile.ParseFromBuffer(data);
                    TerrainObjectTextures[i] = new ObjectImage(graphicsDevice, shpFile, data,
                        terrainTypes[i].SpawnsTiberium ? unitPalette : theaterPalette);
                }
            }

            Logger.Log("Finished loading terrain object textures.");
        }



        public void ReadBuildingTextures(List<BuildingType> buildingTypes)
        {
            Logger.Log("Loading building textures.");

            BuildingTextures = new ObjectImage[buildingTypes.Count];
            BuildingBibTextures = new ObjectImage[buildingTypes.Count];

            for (int i = 0; i < buildingTypes.Count; i++)
            {
                var buildingType = buildingTypes[i];

                string shpFileName = string.IsNullOrWhiteSpace(buildingType.Image) ? buildingType.ArtConfig.Image : buildingType.Image;

                if (string.IsNullOrEmpty(shpFileName))
                    shpFileName = buildingType.ININame;

                if (buildingType.ArtConfig.Theater)
                    shpFileName += Theater.FileExtension;
                else
                    shpFileName += SHP_FILE_EXTENSION;

                // The game has hardcoded NewTheater=yes behaviour for buildings that start with a specific prefix
                bool hardcodedNewTheater = Array.Exists(NewTheaterHardcodedPrefixes, prefix => buildingType.ININame.ToUpperInvariant().StartsWith(prefix));

                string loadedShpName = "";

                byte[] shpData = null;
                if (buildingType.ArtConfig.NewTheater || hardcodedNewTheater)
                {
                    string newTheaterShpName = shpFileName.Substring(0, 1) + Theater.NewTheaterBuildingLetter + shpFileName.Substring(2);

                    shpData = fileManager.LoadFile(newTheaterShpName);
                    loadedShpName = newTheaterShpName;
                }

                // Support generic building letter
                if (Constants.NewTheaterGenericBuilding && shpData == null)
                {
                    string newTheaterShpName = shpFileName.Substring(0, 1) + Constants.NewTheaterGenericLetter + shpFileName.Substring(2);

                    shpData = fileManager.LoadFile(newTheaterShpName);
                    loadedShpName = newTheaterShpName;
                }

                // The game can apparently fall back to the non-theater-specific SHP file name
                // if the theater-specific SHP is not found
                if (shpData == null)
                {
                    shpData = fileManager.LoadFile(shpFileName);
                    loadedShpName = shpFileName;

                    if (shpData == null)
                    {
                        continue;
                    }
                }

                // Palette override in RA2/YR
                Palette palette = buildingType.ArtConfig.TerrainPalette ? theaterPalette : unitPalette;
                if (!string.IsNullOrWhiteSpace(buildingType.ArtConfig.Palette))
                    palette = GetPaletteOrFail(buildingType.ArtConfig.Palette + Theater.FileExtension[1..] + ".pal");

                var shpFile = new ShpFile(loadedShpName);
                shpFile.ParseFromBuffer(shpData);
                BuildingTextures[i] = new ObjectImage(graphicsDevice, shpFile, shpData, palette, null, buildingType.ArtConfig.Remapable);

                // If this building has a bib, attempt to load it
                if (!string.IsNullOrWhiteSpace(buildingType.ArtConfig.BibShape))
                {
                    string bibShpFileName = buildingType.ArtConfig.BibShape;

                    if (buildingType.ArtConfig.Theater)
                        bibShpFileName += Theater.FileExtension;
                    else
                        bibShpFileName += SHP_FILE_EXTENSION;

                    shpData = null;
                    if (buildingType.ArtConfig.NewTheater)
                    {
                        string newTheaterBibShpName = bibShpFileName.Substring(0, 1) + Theater.NewTheaterBuildingLetter + bibShpFileName.Substring(2);

                        shpData = fileManager.LoadFile(newTheaterBibShpName);
                        loadedShpName = newTheaterBibShpName;
                    }

                    if (Constants.NewTheaterGenericBuilding && shpData == null)
                    {
                        string newTheaterBibShpName = bibShpFileName.Substring(0, 1) + Constants.NewTheaterGenericLetter + bibShpFileName.Substring(2);

                        shpData = fileManager.LoadFile(newTheaterBibShpName);
                    }

                    if (shpData == null)
                    {
                        shpData = fileManager.LoadFile(bibShpFileName);
                        loadedShpName = bibShpFileName;
                    }
                        
                    if (shpData == null)
                    {
                        continue;
                    }

                    var bibShpFile = new ShpFile(loadedShpName);
                    bibShpFile.ParseFromBuffer(shpData);
                    BuildingBibTextures[i] = new ObjectImage(graphicsDevice, bibShpFile, shpData, palette, null, buildingType.ArtConfig.Remapable);
                }
            }

            Logger.Log("Finished loading building textures.");
        }

        public void ReadAnimTextures(List<AnimType> animTypes)
        {
            Logger.Log("Loading animation textures.");

            AnimTextures = new ObjectImage[animTypes.Count];

            for (int i = 0; i < animTypes.Count; i++)
            {
                var animType = animTypes[i];

                string shpFileName = string.IsNullOrWhiteSpace(animType.ArtConfig.Image) ? animType.ININame : animType.ArtConfig.Image;
                string loadedShpName = "";

                if (animType.ArtConfig.Theater)
                    shpFileName += Theater.FileExtension;
                else
                    shpFileName += SHP_FILE_EXTENSION;

                byte[] shpData = null;
                if (animType.ArtConfig.NewTheater)
                {
                    string newTheaterShpName = shpFileName.Substring(0, 1) + Theater.NewTheaterBuildingLetter + shpFileName.Substring(2);

                    shpData = fileManager.LoadFile(newTheaterShpName);
                    loadedShpName = newTheaterShpName;
                }

                // Support generic theater letter
                if (Constants.NewTheaterGenericBuilding && shpData == null)
                {
                    string newTheaterShpName = shpFileName.Substring(0, 1) + Constants.NewTheaterGenericLetter + shpFileName.Substring(2);

                    shpData = fileManager.LoadFile(newTheaterShpName);
                    loadedShpName = newTheaterShpName;
                }

                // The game can apparently fall back to the non-theater-specific SHP file name
                // if the theater-specific SHP is not found
                if (shpData == null)
                {
                    shpData = fileManager.LoadFile(shpFileName);
                    loadedShpName = shpFileName;

                    if (shpData == null)
                    {
                        continue;
                    }
                }

                // Palette override in RA2/YR
                // NOTE: Until we use indexed color rendering, we have to assume that a building
                // anim will only be used as a building anim (because it forces unit palette).
                Palette palette = animType.ArtConfig.IsBuildingAnim || animType.ArtConfig.AltPalette ? unitPalette : animPalette;
                if (!string.IsNullOrWhiteSpace(animType.ArtConfig.CustomPalette))
                {
                    palette = GetPaletteOrDefault(
                        animType.ArtConfig.CustomPalette.Replace("~~~", Theater.FileExtension.Substring(1)),
                        palette);
                }

                var shpFile = new ShpFile(loadedShpName);
                shpFile.ParseFromBuffer(shpData);
                AnimTextures[i] = new ObjectImage(graphicsDevice, shpFile, shpData, palette, null,
                    animType.ArtConfig.Remapable || animType.ArtConfig.IsBuildingAnim);
            }

            Logger.Log("Finished loading animation textures.");
        }

        public void ReadUnitTextures(List<UnitType> unitTypes)
        {
            Logger.Log("Loading unit textures.");

            var loadedTextures = new Dictionary<string, ObjectImage>();
            UnitTextures = new ObjectImage[unitTypes.Count];

            for (int i = 0; i < unitTypes.Count; i++)
            {
                var unitType = unitTypes[i];

                string shpFileName = string.IsNullOrWhiteSpace(unitType.Image) ? unitType.ININame : unitType.Image;
                shpFileName += SHP_FILE_EXTENSION;
                if (loadedTextures.TryGetValue(shpFileName, out ObjectImage loadedImage))
                {
                    UnitTextures[i] = loadedImage;
                    continue;
                }

                byte[] shpData = fileManager.LoadFile(shpFileName);

                if (shpData == null)
                    continue;

                // We don't need firing frames and some other stuff,
                // so we build a list of frames to load to save VRAM
                var framesToLoad = unitType.GetIdleFrameIndexes();
                if (unitType.Turret)
                {
                    int turretStartFrame = unitType.GetTurretStartFrame();
                    for (int t = turretStartFrame; t < turretStartFrame + Constants.TurretFrameCount; t++)
                        framesToLoad.Add(t);
                }

                var shpFile = new ShpFile(shpFileName);
                shpFile.ParseFromBuffer(shpData);

                // Load shadow frames
                int regularFrameCount = framesToLoad.Count;
                for (int j = 0; j < regularFrameCount; j++)
                    framesToLoad.Add(framesToLoad[j] + (shpFile.FrameCount / 2));

                UnitTextures[i] = new ObjectImage(graphicsDevice, shpFile, shpData, unitPalette, framesToLoad, unitType.ArtConfig.Remapable);
                loadedTextures[shpFileName] = UnitTextures[i];
            }

            Logger.Log("Finished loading unit textures.");
        }

        public void ReadInfantryTextures(List<InfantryType> infantryTypes)
        {
            Logger.Log("Loading infantry textures.");

            var loadedTextures = new Dictionary<string, ObjectImage>();
            InfantryTextures = new ObjectImage[infantryTypes.Count];

            for (int i = 0; i < infantryTypes.Count; i++)
            {
                var infantryType = infantryTypes[i];

                string image = string.IsNullOrWhiteSpace(infantryType.Image) ? infantryType.ININame : infantryType.Image;
                string shpFileName = string.IsNullOrWhiteSpace(infantryType.ArtConfig.Image) ? image : infantryType.ArtConfig.Image;
                shpFileName += SHP_FILE_EXTENSION;
                if (loadedTextures.TryGetValue(shpFileName, out ObjectImage loadedImage))
                {
                    InfantryTextures[i] = loadedImage;
                    continue;
                }

                if (infantryType.ArtConfig.Sequence == null)
                {
                    continue;
                }

                byte[] shpData = fileManager.LoadFile(shpFileName);

                if (shpData == null)
                    continue;

                var framesToLoad = new List<int>();
                const int FACING_COUNT = 8;
                var readySequence = infantryType.ArtConfig.Sequence.Ready;
                for (int j = 0; j < FACING_COUNT; j++)
                {
                    framesToLoad.Add(readySequence.StartFrame + (readySequence.FrameCount * readySequence.FacingMultiplier * j));
                }

                var shpFile = new ShpFile(shpFileName);
                shpFile.ParseFromBuffer(shpData);

                // Load shadow frames
                int regularFrameCount = framesToLoad.Count;
                for (int j = 0; j < regularFrameCount; j++)
                    framesToLoad.Add(framesToLoad[j] + (shpFile.FrameCount / 2));

                InfantryTextures[i] = new ObjectImage(graphicsDevice, shpFile, shpData, unitPalette, null, infantryType.ArtConfig.Remapable);
                loadedTextures[shpFileName] = InfantryTextures[i];
            }

            Logger.Log("Finished loading infantry textures.");
        }

        public void ReadOverlayTextures(List<OverlayType> overlayTypes)
        {
            Logger.Log("Loading overlay textures.");

            OverlayTextures = new ObjectImage[overlayTypes.Count];
            for (int i = 0; i < overlayTypes.Count; i++)
            {
                var overlayType = overlayTypes[i];

                string imageName = overlayType.ININame;
                if (overlayType.ArtConfig.Image != null)
                    imageName = overlayType.ArtConfig.Image;
                else if (overlayType.Image != null)
                    imageName = overlayType.Image;

                string pngFileName = imageName + PNG_FILE_EXTENSION;

                byte[] pngData = fileManager.LoadFile(pngFileName);

                if (pngData != null)
                {
                    // Load graphics as PNG

                    OverlayTextures[i] = new ObjectImage(graphicsDevice, null, null, null, null, false, PositionedTextureFromBytes(pngData));
                }
                else
                {
                    // Load graphics as SHP

                    string loadedShpName = "";

                    byte[] shpData;

                    if (overlayType.ArtConfig.NewTheater)
                    {
                        string shpFileName = imageName + SHP_FILE_EXTENSION;
                        string newTheaterImageName = shpFileName.Substring(0, 1) + Theater.NewTheaterBuildingLetter + shpFileName.Substring(2);
                        
                        shpData = fileManager.LoadFile(newTheaterImageName);
                        loadedShpName = newTheaterImageName;

                        if (shpData == null)
                        {
                            newTheaterImageName = shpFileName.Substring(0, 1) + Constants.NewTheaterGenericLetter + shpFileName.Substring(2);
                            shpData = fileManager.LoadFile(newTheaterImageName);
                            loadedShpName = newTheaterImageName;
                        }
                    }
                    else
                    {
                        string fileExtension = overlayType.ArtConfig.Theater ? Theater.FileExtension : SHP_FILE_EXTENSION;
                        shpData = fileManager.LoadFile(imageName + fileExtension);
                        loadedShpName = imageName + fileExtension;
                    }

                    if (shpData == null)
                        continue;

                    var shpFile = new ShpFile(loadedShpName);
                    shpFile.ParseFromBuffer(shpData);
                    Palette palette = theaterPalette;

                    if (overlayType.Tiberium)
                    {
                        palette = unitPalette;

                        if (Constants.TheaterPaletteForTiberium)
                            palette = tiberiumPalette ?? theaterPalette;
                    }

                    if (overlayType.Wall || overlayType.IsVeins)
                        palette = unitPalette;

                    bool isRemapable = overlayType.Tiberium && !Constants.TheaterPaletteForTiberium;

                    OverlayTextures[i] = new ObjectImage(graphicsDevice, shpFile, shpData, palette, null, isRemapable, null);
                }
            }

            Logger.Log("Finished loading overlay textures.");
        }

        public void ReadSmudgeTextures(List<SmudgeType> smudgeTypes)
        {
            Logger.Log("Loading smudge textures.");

            SmudgeTextures = new ObjectImage[smudgeTypes.Count];
            for (int i = 0; i < smudgeTypes.Count; i++)
            {
                var smudgeType = smudgeTypes[i];

                string imageName = smudgeType.ININame;
                string fileExtension = smudgeType.Theater ? Theater.FileExtension : SHP_FILE_EXTENSION;
                string finalShpName = imageName + fileExtension;
                byte[] shpData = fileManager.LoadFile(finalShpName);

                if (shpData == null)
                    continue;

                var shpFile = new ShpFile(finalShpName);
                shpFile.ParseFromBuffer(shpData);
                Palette palette = theaterPalette;
                SmudgeTextures[i] = new ObjectImage(graphicsDevice, shpFile, shpData, palette);
            }

            Logger.Log("Finished loading smudge textures.");
        }

        private Random random = new Random();

        public Theater Theater { get; }

        private CCFileManager fileManager;

        private readonly Palette theaterPalette;
        private readonly Palette unitPalette;
        private readonly Palette tiberiumPalette;
        private readonly Palette animPalette;

        private List<TileImage[]> terrainGraphicsList = new List<TileImage[]>();
        private List<TileImage[]> mmTerrainGraphicsList = new List<TileImage[]>();
        private List<bool> hasMMGraphics = new List<bool>();

        public int TileCount => terrainGraphicsList.Count;

        public TileImage GetTileGraphics(int id) => terrainGraphicsList[id][random.Next(terrainGraphicsList[id].Length)];
        public TileImage GetTileGraphics(int id, int randomId) => terrainGraphicsList[id][randomId];
        public TileImage GetMarbleMadnessTileGraphics(int id) => mmTerrainGraphicsList[id][0];
        public bool HasSeparateMarbleMadnessTileGraphics(int id) => hasMMGraphics[id];

        public ITileImage GetTile(int id) => GetTileGraphics(id);

        public int GetOverlayFrameCount(OverlayType overlayType)
        {
            PositionedTexture[] overlayFrames = OverlayTextures[overlayType.Index].Frames;

            // We only consider non-blank frames as valid frames, so we need to look up
            // the first blank frame to get the proper frame count
            // According to Bittah, when we find an empty overlay frame,
            // we can assume the rest of the overlay frames to be empty too
            for (int i = 0; i < overlayFrames.Length; i++)
            {
                if (overlayFrames[i] == null || overlayFrames[i].Texture == null)
                    return i;
            }

            // No blank overlay frame existed - return the full frame count divided by two (the rest are used up by shadows)
            return OverlayTextures[overlayType.Index].Frames.Length / 2;
        }

        public ObjectImage[] TerrainObjectTextures { get; set; }
        public ObjectImage[] BuildingTextures { get; set; }
        public ObjectImage[] BuildingBibTextures { get; set; }
        public ObjectImage[] UnitTextures { get; set; }
        public ObjectImage[] InfantryTextures { get; set; }
        public ObjectImage[] OverlayTextures { get; set; }
        public ObjectImage[] SmudgeTextures { get; set; }
        public ObjectImage[] AnimTextures { get; set; }


        public ObjectImage BuildingZ { get; set; }

        /// <summary>
        /// Frees up all memory used by the theater graphics textures
        /// (or more precisely, diposes them so the garbage collector can free them).
        /// Make sure no rendering is attempted afterwards!
        /// </summary>
        public void DisposeAll()
        {
            var task1 = Task.Factory.StartNew(() => DisposeObjectImagesFromArray(TerrainObjectTextures));
            var task2 = Task.Factory.StartNew(() => DisposeObjectImagesFromArray(BuildingTextures));
            var task3 = Task.Factory.StartNew(() => DisposeObjectImagesFromArray(UnitTextures));
            var task4 = Task.Factory.StartNew(() => DisposeObjectImagesFromArray(InfantryTextures));
            var task5 = Task.Factory.StartNew(() => DisposeObjectImagesFromArray(OverlayTextures));
            var task6 = Task.Factory.StartNew(() => DisposeObjectImagesFromArray(SmudgeTextures));
            var task7 = Task.Factory.StartNew(() => { terrainGraphicsList.ForEach(tileImageArray => Array.ForEach(tileImageArray, tileImage => tileImage.Dispose())); terrainGraphicsList.Clear(); });
            var task8 = Task.Factory.StartNew(() => { mmTerrainGraphicsList.ForEach(tileImageArray => Array.ForEach(tileImageArray, tileImage => tileImage.Dispose())); mmTerrainGraphicsList.Clear(); });
            var task9 = Task.Factory.StartNew(() => DisposeObjectImagesFromArray(AnimTextures));
            Task.WaitAll(task1, task2, task3, task4, task5, task6, task7, task8, task9);

            TerrainObjectTextures = null;
            BuildingTextures = null;
            UnitTextures = null;
            InfantryTextures = null;
            OverlayTextures = null;
            SmudgeTextures = null;
            AnimTextures = null;
        }

        private void DisposeObjectImagesFromArray(ObjectImage[] objImageArray)
        {
            Array.ForEach(objImageArray, objectImage => { if (objectImage != null) objectImage.Dispose(); });
            Array.Clear(objImageArray);
        }

        private Palette GetPaletteOrFail(string paletteFileName)
        {
            byte[] paletteData = fileManager.LoadFile(paletteFileName);
            if (paletteData == null)
                throw new KeyNotFoundException(paletteFileName + " not found from loaded MIX files!");
            return new Palette(paletteData);
        }

        private Palette GetPaletteOrDefault(string paletteFileName, Palette palette)
        {
            byte[] paletteData = fileManager.LoadFile(paletteFileName);
            if (paletteData == null)
                return palette;
            return new Palette(paletteData);
        }

        private PositionedTexture PositionedTextureFromBytes(byte[] data)
        {
            using (var memstream = new MemoryStream(data))
            {
                var tex2d = Texture2D.FromStream(graphicsDevice, memstream);

                // premultiply alpha
                Color[] colorData = new Color[tex2d.Width * tex2d.Height];
                tex2d.GetData(colorData);
                for (int i = 0; i < colorData.Length; i++)
                {
                    var color = colorData[i];
                    color.R = (byte)((color.R * color.A) / byte.MaxValue);
                    color.G = (byte)((color.G * color.A) / byte.MaxValue);
                    color.B = (byte)((color.B * color.A) / byte.MaxValue);
                    colorData[i] = color;
                }

                tex2d.SetData(colorData);

                return new PositionedTexture(tex2d.Width, tex2d.Height, 0, 0, tex2d);
            }
        }

        public int GetTileSetId(int uniqueTileIndex)
        {
            return GetTileGraphics(uniqueTileIndex).TileSetId;
        }
    }
}