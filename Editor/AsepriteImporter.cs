// Copyright 2022 Takanori Shibasaki
//  
// Use of this source code is governed by an MIT-style
// license that can be found in the LICENSE file or at
// https://opensource.org/licenses/MIT.
//
using System.Linq;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.AssetImporters;

namespace Aseprite
{
    public enum ImportPalette
    {
        None,
        HorizontalTexture,
        VerticalTexture,
    }

    [System.Serializable]
    internal struct LayerPositionSetting
    {
        public string LayerName;
        public Vector2 Pivot;
    }

    [ScriptedImporter(2, "ase")]
    unsafe class AsepriteImporter : ScriptedImporter
    {
        const string AtlasName = "Atlas 0";
        const string PaletteName = "Palette 0";

        [SerializeField, HideInInspector]
        internal int m_SerializedVersion = 1;

        public bool ImportAnimation = true;
        public bool ImportSlices = true;
        public bool ImportIndex = false;
        public ImportPalette ImportPalette;

        [Space(8)]
        public bool Trim = false;
        public bool OptimizeFrames = false;
        public bool Readable;

        [Min(1)]
        public float PixelsPerUnit = 100;

        [Min(0)]
        public int PackingMargin = 1;

        public Vector2 Pivot = new Vector2(0.5f, 0.5f);

        [Space(8)]
        public FilterMode FilterMode;
        public SpriteMeshType SpriteMeshType;
        public TextureWrapMode WrapMode = TextureWrapMode.Clamp;

        [Header("Tilemap")]
        public bool GenerateTiles = false;
        public UnityEngine.Tilemaps.Tile.ColliderType TileColliderType;

        public LayerPositionSetting[] GenerateLayerPositionCurves = new LayerPositionSetting[0];

        public bool GeneratePrefab;

        struct CropKey : System.IEquatable<CropKey>
        {
            public int Frame;
            public RectInt Rect;

            public CropKey(int frame, int x, int y, int w, int h)
            {
                Frame = frame;
                Rect = new RectInt(x, y, w, h);
            }

            public CropKey(SliceKey slice) : this(slice.Frame, slice.X, slice.Y, slice.Width, slice.Height)
            {
            }

            public bool Equals(CropKey other)
            {
                return Frame == other.Frame && Rect.Equals(other.Rect);
            }

            public override int GetHashCode()
            {
                return Frame.GetHashCode() ^ Rect.GetHashCode();
            }
        }

        struct SpriteKey : System.IEquatable<SpriteKey>
        {
            public int ImageIndex;
            public Vector2 Pivot;
            public Vector4 Border;

            public SpriteKey(int imageIndex, SliceFlags flags, SliceKey sliceKey)
            {
                ImageIndex = imageIndex;
                Pivot.x = sliceKey.PivotX + sliceKey.X;
                Pivot.y = sliceKey.PivotY + sliceKey.Y;

                if ((flags & SliceFlags.NineSlices) != 0)
                {
                    Border.x = sliceKey.CenterX;
                    Border.w = sliceKey.CenterY;
                    Border.z = (sliceKey.X + sliceKey.Width) - (sliceKey.CenterX + sliceKey.CenterWidth);
                    Border.y = (sliceKey.Y + sliceKey.Height) - (sliceKey.CenterY + sliceKey.CenterHeight);
                }
                else
                {
                    Border = default;
                }
            }

            public bool Equals(SpriteKey other)
            {
                return ImageIndex == other.ImageIndex && Pivot == other.Pivot && Border == other.Border;
            }

            public override int GetHashCode()
            {
                return ImageIndex.GetHashCode() ^ Pivot.GetHashCode() ^ Border.GetHashCode();
            }
        }

        public override void OnImportAsset(AssetImportContext ctx)
        {
            var file = new AseFile(ctx.assetPath);

            if (ImportPalette != ImportPalette.None && file.Palette != null)
            {
                var tex = GeneratePaletteTexture(PaletteName, file.Palette, ImportPalette == ImportPalette.VerticalTexture, Readable);
                ctx.AddObjectToAsset(tex.name, tex);
            }

            var cropToImageIndex = new Dictionary<CropKey, int>();
            var images = new List<Image>();

            var slices = file.Slices.Count <= 0 || !ImportSlices ?
                new Slice[] { new Slice(0, 0, file.Header.Width, file.Header.Height, Pivot) } :
                file.Slices.ToArray();

            var totalFrames = ImportAnimation ? file.Frames.Length : 1;

            foreach (var slice in slices)
            {
                var cropKeys = new CropKey[totalFrames];

                if ((slice.Flags & SliceFlags.HasPivot) == 0)
                {
                    slice.Flags |= SliceFlags.HasPivot;

                    for (var i = 0; i < slice.Keys.Length; ++i)
                    {
                        slice.Keys[i].PivotX = (int)(Pivot.x * slice.Keys[i].Width);
                        slice.Keys[i].PivotY = (int)(Pivot.y * slice.Keys[i].Height);
                    }
                }

                slice.Keys = BakeSliceKeys(slice.Keys, totalFrames);

                foreach (var sliceKey in slice.Keys)
                {
                    var cropKey = new CropKey(sliceKey);
                    cropToImageIndex[cropKey] = images.Count;
                    images.Add(null);
                }
            }

            foreach (var cropKey in cropToImageIndex.Keys.ToArray())
            {
                var frame = file.Frames[cropKey.Frame];
                var img = file.GetImage(frame, ImportIndex);

                img.Crop(cropKey.Rect);

                if (Trim)
                {
                    img.Trim();
                }

                img.FlipV();

                var index = cropToImageIndex[cropKey];
                images[index] = img;
            }

            var imageRemap = new List<int>(Enumerable.Range(0, images.Count));

            if (OptimizeFrames)
            {
                StripFrames(images, imageRemap);
            }

            var atlasRects = new List<Rect>();
            int atlasWidth;
            int atlasHeight;

            if (2 <= images.Count)
            {
                if ((ImportAnimation && 2 <= totalFrames) || Trim)
                {
                    GenerateAtlas(atlasRects, images, PackingMargin, out var size);
                    atlasWidth = size.x;
                    atlasHeight = size.y;
                }
                else
                {
                    atlasWidth = file.Header.Width;
                    atlasHeight = file.Header.Height;

                    foreach (var img in images)
                    {
                        var rect = new Rect(img.XOffset, file.Header.Height - img.YOffset - img.Height, img.Width, img.Height);
                        atlasRects.Add(rect);
                    }
                }
            }
            else
            {
                if (Trim)
                {
                    atlasRects.Add(new Rect(0, 0, images[0].Width, images[0].Height));
                    atlasWidth = images[0].Width;
                    atlasHeight = images[0].Height;
                }
                else
                {
                    atlasRects.Add(new Rect(images[0].XOffset, images[0].YOffset, images[0].Width, images[0].Height));
                    atlasWidth = file.Header.Width;
                    atlasHeight = file.Header.Height;
                }
            }

            Texture2D atlasTex;

            if (!Trim && file.Frames.Length == 1)
            {
                atlasWidth = file.Header.Width;
                atlasHeight = file.Header.Height;

                var img = file.GetImage(file.Frames[0], ImportIndex);
                img.FlipV();

                atlasTex = PackImages(AtlasName, 
                    new List<Image>(new Image[] { img }), 
                    new List<Rect>( new Rect[] { new Rect(0, 0, file.Header.Width, file.Header.Height) } ), 
                    atlasWidth, atlasHeight, Readable, file.Header.BitsPerPixel == 8 && ImportIndex);
            }
            else
            {
                atlasTex = PackImages(AtlasName, images, atlasRects, atlasWidth, atlasHeight, Readable, file.Header.BitsPerPixel == 8 && ImportIndex);
            }

            ctx.AddObjectToAsset("ATLAS:0", atlasTex);
            ctx.SetMainObject(atlasTex);

            var spriteMap = new Dictionary<SpriteKey, Sprite>[slices.Length];
            var spriteList = new List<Sprite>();
            var tileList = new List<UnityEngine.Tilemaps.Tile>();

            for (var sliceIndex = 0; sliceIndex < slices.Length; ++sliceIndex)
            {
                var sprites = new Dictionary<SpriteKey, Sprite>();
                spriteMap[sliceIndex] = sprites;

                var slice = slices[sliceIndex];
                var spriteGroup = slice.Name;

                if (string.IsNullOrEmpty(spriteGroup))
                {
                    if (slices.Length == 1 && slice.Keys.Length == 1)
                    {
                        spriteGroup = "Sprite";
                    }
                    else
                    {
                        spriteGroup = $"Slice{sliceIndex}";
                    }
                }

                spriteList.Clear();
                tileList.Clear();

                foreach (var sliceKey in slice.Keys)
                {
                    var cropKey = new CropKey(sliceKey);
                    var imageIndex = cropToImageIndex[cropKey];
                    imageIndex = imageRemap[imageIndex];

                    var spriteKey = new SpriteKey(imageIndex, slice.Flags, sliceKey);

                    if (!sprites.ContainsKey(spriteKey))
                    {
                        var img = images[spriteKey.ImageIndex];

                        Vector2 pivot;
                        pivot.x = (spriteKey.Pivot.x - Mathf.Max(sliceKey.X, img.XOffset)) / img.Width;
                        pivot.y = 1.0f - (spriteKey.Pivot.y - Mathf.Max(sliceKey.Y, img.YOffset)) / img.Height;

                        var sprite = Sprite.Create(atlasTex, atlasRects[imageIndex], pivot, PixelsPerUnit, 0, SpriteMeshType, spriteKey.Border);
                        sprite.name = $"{spriteGroup}";
                        ctx.AddObjectToAsset($"SLICE:{spriteGroup}:{spriteList.Count}", sprite);

                        if (GenerateTiles)
                        {
                            var tileAsset = ScriptableObject.CreateInstance<UnityEngine.Tilemaps.Tile>();
                            tileAsset.sprite = sprite;
                            tileAsset.name = $"{spriteGroup}";
                            tileAsset.colliderType = TileColliderType;
                            ctx.AddObjectToAsset($"TILE:{spriteGroup}:{spriteList.Count}", tileAsset);
                            tileList.Add(tileAsset);
                        }

                        spriteList.Add(sprite);
                        sprites[spriteKey] = sprite;
                    }
                }

                if (2 <= spriteList.Count)
                {
                    AppendIndex(spriteList, 1);
                }

                if (GenerateTiles && 2 <= tileList.Count)
                {
                    AppendIndex(spriteList, 1);
                }
            }

            if (ImportAnimation && file.TagSet == null && 2 <= file.Frames.Length)
            {
                file.TagSet = new TagSet();
                file.TagSet.Tags.Add(new Tag(null, 0, (ushort)(file.Frames.Length - 1), LoopAnimationDirection.Forward));
            }

            if (ImportAnimation && file.TagSet != null)
            {
                var animationIndex = 0;

                var spriteAnimationBinding = new EditorCurveBinding();
                spriteAnimationBinding.type = typeof(SpriteRenderer);
                spriteAnimationBinding.propertyName = "m_Sprite";

                var objRefKeys = new List<ObjectReferenceKeyframe>();

                List<EditorCurveBinding> genericBindings = null;
                List<AnimationCurve> genericCurves = null;
                Dictionary<string, int> genericCurveMap = null;

                if (0 < GenerateLayerPositionCurves.Length)
                {
                    genericBindings = new List<EditorCurveBinding>();
                    genericCurves = new List<AnimationCurve>();
                    genericCurveMap = new Dictionary<string, int>();

                    foreach (var importLayer in GenerateLayerPositionCurves)
                    {
                        var curveIndex = genericBindings.Count;

                        genericBindings.Add(EditorCurveBinding.FloatCurve(importLayer.LayerName, typeof(Transform), "m_LocalPosition.x"));
                        genericBindings.Add(EditorCurveBinding.FloatCurve(importLayer.LayerName, typeof(Transform), "m_LocalPosition.y"));

                        genericCurves.Add(new AnimationCurve());
                        genericCurves.Add(new AnimationCurve());

                        genericCurveMap[importLayer.LayerName] = curveIndex;
                    }
                }

                for (var sliceIndex = 0; sliceIndex < slices.Length; ++sliceIndex)
                {
                    var slice = slices[sliceIndex];
                    var animationPrefix = string.IsNullOrEmpty(slice.Name) ? "" : $"{slice.Name}:";
                    var sliceKeys = BakeSliceKeys(slice.Keys, totalFrames);
                    var sprites = spriteMap[sliceIndex];

                    for (var tagIndex = 0; tagIndex < file.TagSet.Tags.Count; ++tagIndex)
                    {
                        var tag = file.TagSet.Tags[tagIndex];
                        var tagName = tag.Name;

                        if (string.IsNullOrEmpty(tagName))
                        {
                            tagName = $"Animation {1 + tagIndex}";
                        }

                        var newClip = new AnimationClip();
                        newClip.name = animationPrefix + tagName;

                        var time = 0.0f;
                        var len = tag.To - tag.From + 1;
                        Sprite prevSprite = null;

                        objRefKeys.Clear();

                        if (genericCurves != null)
                        {
                            for (var i = 0; i < genericCurves.Count; ++i)
                            {
                                genericCurves[i] = new AnimationCurve();
                            }
                        }

                        for (var frameIndex = 0; frameIndex < len; ++frameIndex)
                        {
                            var frame = file.Frames[tag.From + frameIndex];
                            var sliceKey = sliceKeys[tag.From + frameIndex];

                            var cropKey = new CropKey(sliceKey);
                            var imageIndex = cropToImageIndex[cropKey];
                            imageIndex = imageRemap[imageIndex];

                            var image = images[imageIndex];

                            var spriteKey = new SpriteKey(imageIndex, slice.Flags, sliceKey);
                            var sprite = sprites[spriteKey];

                            if (sprite != prevSprite)
                            {
                                objRefKeys.Add(new ObjectReferenceKeyframe() { time = time, value = sprite });
                                prevSprite = sprite;
                            }

                            if (0 < GenerateLayerPositionCurves.Length)
                            {
                                foreach (var importLayer in GenerateLayerPositionCurves)
                                {
                                    var cel = frame.FindCel(importLayer.LayerName);

                                    if (cel == null)
                                    {
                                        continue;
                                    }

                                    var pos = CalculateLayerPosition(cel, importLayer.Pivot, sliceKey);
                                    var curveIndex = genericCurveMap[importLayer.LayerName];
                                    genericCurves[curveIndex].AddKey(new Keyframe(time, pos.x, float.PositiveInfinity, float.PositiveInfinity));
                                    genericCurves[curveIndex + 1].AddKey(new Keyframe(time, pos.y, float.PositiveInfinity, float.PositiveInfinity));
                                }
                            }

                            time += frame.FrameDuration / 1000.0f;
                        }

                        AnimationUtility.SetObjectReferenceCurve(newClip, spriteAnimationBinding, objRefKeys.ToArray());

                        if (genericCurves != null)
                        {
                            AnimationUtility.SetEditorCurves(newClip, genericBindings.ToArray(), genericCurves.ToArray());
                        }

                        using (var obj = new SerializedObject(newClip))
                        {
                            obj.FindProperty("m_AnimationClipSettings.m_StopTime").floatValue = time;
                            obj.FindProperty("m_AnimationClipSettings.m_LoopTime").boolValue = true;
                            obj.ApplyModifiedPropertiesWithoutUndo();
                        }

                        ctx.AddObjectToAsset($"ANIMATION:{slice.Name}:{tagName}", newClip);
                        ++animationIndex;
                    }
                }
            }

            if (GeneratePrefab)
            {
                for (var i = 0; i < slices.Length; ++i)
                {
                    var slice = slices[i];
                    var go = new GameObject(slice.Name);

                    var sliceKey = slice.Keys[0];
                    var cropKey = new CropKey(sliceKey);
                    var imageIndex = cropToImageIndex[cropKey];
                    imageIndex = imageRemap[imageIndex];

                    var spriteKey = new SpriteKey(imageIndex, slice.Flags, sliceKey);
                    var spriteRenderer = go.AddComponent<SpriteRenderer>();
                    spriteRenderer.sprite = spriteMap[i][spriteKey];

                    foreach (var importLayer in GenerateLayerPositionCurves)
                    {
                        var cel = file.Frames[sliceKey.Frame].FindCel(importLayer.LayerName);

                        if (cel == null)
                        {
                            continue;
                        }

                        var child = new GameObject(importLayer.LayerName);
                        child.transform.localPosition = CalculateLayerPosition(cel, importLayer.Pivot, sliceKey);
                        child.transform.SetParent(go.transform, false);
                    }

                    ctx.AddObjectToAsset($"PREFAB:{slice.Name}", go);
                }
            }
        }

        Vector2 CalculateLayerPosition(Cel cel, Vector2 pivot, SliceKey sliceKey)
        {
            Rect layerBounds;

            if ((cel.ExtraFlags & CelExtraFlags.PreciseBounds) != 0)
            {
                layerBounds = new Rect(cel.PreciseXPosition, cel.PreciseYPosition, cel.ScaledWidth, cel.ScaledHeight);
            }
            else
            {
                var size = cel.Data.Size;
                layerBounds = new Rect(cel.XPosition, cel.YPosition, size.x, size.y);
            }

            var xPos = Mathf.Lerp(layerBounds.xMin, layerBounds.xMax, pivot.x) - (sliceKey.X + sliceKey.PivotX);
            var yPos = Mathf.Lerp(layerBounds.yMin, layerBounds.yMax, 1 - pivot.y) - (sliceKey.Y + sliceKey.PivotY);

            xPos /= PixelsPerUnit;
            yPos /= -PixelsPerUnit;

            return new Vector2(xPos, yPos);
        }

        static void AppendIndex<T>(List<T> objects, int baseIndex) where T: Object
        {
            for (var i = 0; i < objects.Count; ++i)
            {
                objects[i].name += $" ({baseIndex + i})";
            }
        }

        static SliceKey[] BakeSliceKeys(SliceKey[] keys, int totalFrames)
        {
            var result = new SliceKey[totalFrames];

            for (var i = 0; i < keys.Length; ++i)
            {
                ref var key = ref keys[i];

                for (var j = key.Frame; j < totalFrames; ++j)
                {
                    result[j] = key;
                    result[j].Frame = j;
                }
            }

            return result;
        }

        static Texture2D GeneratePaletteTexture(string name, Palette palette, bool vertical, bool readable)
        {
            var paletteSize = vertical ? new Vector2Int(1, palette.Colors.Length) : new Vector2Int(palette.Colors.Length, 1);

            var colors = new Color[palette.Colors.Length];
            for (var i = 0; i < palette.Colors.Length; ++i)
            {
                colors[i].r = palette.Colors[i].R / 255f;
                colors[i].g = palette.Colors[i].G / 255f;
                colors[i].b = palette.Colors[i].B / 255f;
                colors[i].a = palette.Colors[i].A / 255f;
            }

            var paletteTexture = new Texture2D(paletteSize.x, paletteSize.y, TextureFormat.ARGB32, false);
            paletteTexture.name = name;
            paletteTexture.filterMode = FilterMode.Point;
            paletteTexture.wrapMode = TextureWrapMode.Clamp;
            paletteTexture.SetPixels(colors);
            paletteTexture.Apply(false, !readable);

            return paletteTexture;
        }

        static void StripFrames(List<Image> images, List<int> indices)
        {
            indices.Clear();

            for (var i = 0; i < images.Count; ++i)
            {
                var index = i;

                for (var j = 0; j < i; ++j)
                {
                    if (images[i].Equals(images[j]))
                    {
                        index = j;
                        images.RemoveAt(i);
                        --i;
                        break;
                    }
                }

                indices.Add(index);
            }
        }

        static void GenerateAtlas(List<Rect> rects, List<Image> images, int margin, out Vector2Int atlasSize)
        {
            atlasSize = GenerateAtlas(rects, images, margin);
        }

        static Vector2Int GenerateAtlas(List<Rect> rects, List<Image> images, int margin)
        {
            var sizes = new List<Vector2>();
            Vector2Int totalSize = default;
            
            foreach (var img in images)
            {
                sizes.Add(new Vector2(img.Width, img.Height));
                totalSize.x += img.Width + margin;
                totalSize.y += img.Height + margin;
            }

            var sizeArray = sizes.ToArray();
            var maxSize = Mathf.Max(totalSize.x, totalSize.y);
            var minSize = 0;
            var tempRects = new List<Rect>();

            while (1 < maxSize - minSize)
            {
                var midSize = (maxSize - minSize) / 2 + minSize;
                var fit = Texture2D.GenerateAtlas(sizeArray, margin, midSize, tempRects);

                if (0 < tempRects.Count && (tempRects[0].width <= 0 || tempRects[0].height <= 0))
                {
                    fit = false;
                }

                if (fit)
                {
                    maxSize = midSize;

                    rects.Clear();
                    foreach (var rect in tempRects)
                    {
                        rects.Add(rect);
                    }
                }
                else
                {
                    minSize = midSize;
                }
            }

            if (2 <= margin)
            {
                OffsetRects(rects, margin / 2);
            }

            Vector2Int result = default;

            foreach (var rect in rects)
            {
                result = Vector2Int.Max(result, new Vector2Int((int)rect.max.x, (int)rect.max.y));
            }

            result.x += margin;
            result.y += margin;

            return result;
        }

        static void OffsetRects(List<Rect> rects, float offset)
        {
            for (var i = 0; i < rects.Count; ++i)
            {
                var rect = rects[i];
                rect.x += offset;
                rect.y += offset;
                rects[i] = rect;
            }
        }

        static Texture2D PackImages(string name, List<Image> images, List<Rect> rects, int atlasWidth, int atlasHeight, bool readable, bool indexed)
        {
            var pixels = new Color32[atlasWidth * atlasHeight];

            for (var i = 0; i < rects.Count; ++i)
            {
                var img = images[i];
                var rect = rects[i];

                for (var y = 0; y < img.Height; ++y)
                {
                    for (var x = 0; x < img.Width; ++x)
                    {
                        pixels[(x + (int)rect.x) + (y + (int)rect.y) * atlasWidth] = img.Pixels[x + y * img.Width];
                    }
                }
            }

            var atlasTex = new Texture2D(atlasWidth, atlasHeight, TextureFormat.ARGB32, false, indexed);
            atlasTex.name = name;
            atlasTex.filterMode = FilterMode.Point;
            atlasTex.alphaIsTransparency = true;
            atlasTex.SetPixels32(pixels);
            atlasTex.Apply(false, !readable);

            return atlasTex;
        }
    }

    public class InvalidAseFileException : System.Exception
    {
    }

    internal enum ColorProfileType
    {
        None = 0,
        sRGB = 1,
        ICC = 2,
    }

    [System.Flags]
    internal enum ColorProfileFlags
    {
        SpecialFixedGamma = 1,
    }

    [System.Flags]
    internal enum LayerFlags
    {
        Visible = 1,
        Editable = 2,
        LockMovement = 4,
        Background = 8,
        PreferLinkedCels = 16,
        Collapse = 32,
        Reference = 64,
    }

    internal enum LayerType
    {
        Normal = 0,
        Group = 1,
        Tilemap = 2,
    }

    internal enum BlendMode
    {
        Normal = 0,
        Multiply = 1,
        Screen = 2,
        Overlay = 3,
        Darken = 4,
        Lighten = 5,
        ColorDodge = 6,
        ColorBurn = 7,
        HardLight = 8,
        SoftLight = 9,
        Difference = 10,
        Exclusion = 11,
        Hue = 12,
        Saturation = 13,
        Color = 14,
        Luminosity = 15,
        Addition = 16,
        Subtract = 17,
        Divide = 18,
    }

    [System.Flags]
    internal enum ColorEntryFlags
    {
        HasName = 1,
    }

    [System.Flags]
    internal enum FileHeaderFlags
    {
        UseLayerOpacity = 1,
    }

    internal struct FileHeader
    {
        public uint FileSize;
        public ushort MagicNumber;
        public ushort NumberOfFrames;
        public ushort Width;
        public ushort Height;
        public ushort BitsPerPixel;
        public FileHeaderFlags Flags;
        public ushort Speed;
        public byte TransparentColorIndex;
        public ushort NumberOfColors;
        public byte PixelWidth;
        public byte PixelHeight;
        public short GridX;
        public short GridY;
        public ushort GridWidth;
        public ushort GridHeight;

        public FileHeader(BinaryReader r)
        {
            r.Read(out FileSize);
            r.Read(out MagicNumber);

            if (MagicNumber != 0xA5E0)
            {
                throw new InvalidAseFileException();
            }

            r.Read(out NumberOfFrames);
            r.Read(out Width);
            r.Read(out Height);
            r.Read(out BitsPerPixel);
            Flags = (FileHeaderFlags)r.ReadUInt32();
            r.Read(out Speed);
            r.Skip(8);
            r.Read(out TransparentColorIndex);
            r.Skip(3);
            r.Read(out NumberOfColors);
            r.Read(out PixelWidth);
            r.Read(out PixelHeight);
            r.Read(out GridX);
            r.Read(out GridY);
            r.Read(out GridWidth);
            r.Read(out GridHeight);
            r.Skip(84);
        }
    }

    internal class Frame
    {
        public uint Size;
        public ushort MagicNumber;
        public ushort OldNumberOfChunks;
        public ushort FrameDuration;
        public uint NumberOfChunks;

        public List<Cel> Cels = new List<Cel>();

        public Frame(BinaryReader r, AseFile file)
        {
            r.Read(out Size);
            r.Read(out MagicNumber);

            if (MagicNumber != 0xF1FA)
            {
                throw new InvalidAseFileException();
            }

            r.Read(out OldNumberOfChunks);
            r.Read(out FrameDuration);
            r.Skip(2);
            r.Read(out NumberOfChunks);

            Cel lastCel = null;

            for (var i = 0; i < NumberOfChunks; ++i)
            {
                var chunkStart = r.BaseStream.Position;
                var chunkSize = r.ReadUInt32();
                var chunkType = (ChunkType)r.ReadUInt16();

                //Debug.Log(chunkType + " = " + chunkSize);

                switch (chunkType)
                {
                    case ChunkType.ColorProfileChunk:
                        file.ColorProfile = new ColorProfile(r);
                        break;
                    case ChunkType.OldPaletteChunk0:
                        break;
                    case ChunkType.OldPaletteChunk1:
                        break;
                    case ChunkType.LayerChunk:
                        file.Layers.Add(new Layer(r));
                        break;
                    case ChunkType.CelChunk:
                        lastCel = new Cel(r, chunkStart + chunkSize, file.Header.BitsPerPixel);
                        Cels.Add(lastCel);
                        break;
                    case ChunkType.CelExtraChunk:
                        if (lastCel != null)
                        {
                            lastCel.ReadExtraData(r);
                        }
                        break;
                    case ChunkType.TagsChunk:
                        file.TagSet = new TagSet(r);
                        break;
                    case ChunkType.PaletteChunk:
                        file.Palette = new Palette(r);
                        break;
                    case ChunkType.SliceChunk:
                        file.Slices.Add(new Slice(r));
                        break;
                    case ChunkType.TilesetChunk:
                        file.Tilesets.Add(new Tileset(r, file.Header.BitsPerPixel));
                        break;
                    case ChunkType.ExternalFileChunk:
                        file.ExternalFiles = new ExternalFiles(r);
                        break;
                }

                r.BaseStream.Seek(chunkStart + chunkSize, SeekOrigin.Begin);
            }
        }

        public Cel FindCel(string layerName)
        {
            foreach (var cel in Cels)
            {
                if (cel.Layer.Name == layerName)
                {
                    return cel;
                }
            }
            return null;
        }
    }

    enum ChunkType : ushort
    {
        OldPaletteChunk0 = 0x0004,
        OldPaletteChunk1 = 0x0011,
        LayerChunk = 0x2004,
        CelChunk = 0x2005,
        CelExtraChunk = 0x2006,
        ColorProfileChunk = 0x2007,
        ExternalFileChunk = 0x2008,
        MaskChunk = 0x2016,
        PathChunk = 0x2017,
        TagsChunk = 0x2018,
        PaletteChunk = 0x2019,
        UserDataChunk = 0x2020,
        SliceChunk = 0x2022,
        TilesetChunk = 0x2023,
    }

    internal class ExternalFileEntry
    {
        public uint EntryID;
        public string Filename;
    }

    internal class ExternalFiles
    {
        public uint NumberOfFiles;
        public ExternalFileEntry[] Entries;

        public ExternalFiles(BinaryReader r)
        {
            r.Read(out NumberOfFiles);
            r.Skip(8);

            Entries = new ExternalFileEntry[NumberOfFiles];

            for (var i = 0; i < NumberOfFiles; ++i)
            {
                ref var entry = ref Entries[i];
                r.Read(out entry.EntryID);
                r.Skip(8);
                entry.Filename = r.ReadAseString();
            }
        }
    }

    internal class ColorProfile
    {
        internal ColorProfileType Type;
        internal ColorProfileFlags Flags;
        internal float Gamma;
        internal byte[] ICCProfileData;

        public ColorProfile(BinaryReader r)
        {
            Type = (ColorProfileType)r.ReadUInt16();
            Flags = (ColorProfileFlags)r.ReadUInt16();
            Gamma = r.ReadFixed();

            if (Type == ColorProfileType.ICC)
            {
                uint len;
                r.Read(out len);
                ICCProfileData = r.ReadBytes((int)len);
            }
        }
    }

    internal class Layer
    {
        public LayerFlags Flags;
        public LayerType Type;
        public ushort ChildLevel;
        public BlendMode BlendMode;
        public byte Opacity;
        public string Name;
        public uint TilesetIndex;

        public Layer Parent;
        public List<Layer> Children = new List<Layer>();

        public Layer(BinaryReader r)
        {
            Flags = (LayerFlags)r.ReadUInt16();
            Type = (LayerType)r.ReadUInt16();

            r.Read(out ChildLevel);
            r.Skip(2);
            r.Skip(2);

            BlendMode = (BlendMode)r.ReadUInt16();

            r.Read(out Opacity);
            r.Skip(3);

            Name = r.ReadAseString();

            if (Type == LayerType.Tilemap)
            {
                r.Read(out TilesetIndex);
            }
        }

        public bool IsVisible()
        {
            for (var layer = this; layer != null; layer = layer.Parent)
            {
                if ((layer.Flags & LayerFlags.Visible) == 0)
                {
                    return false;
                }
            }
            return true;
        }
    }

    internal struct ColorData
    {
        public ColorEntryFlags Flags;
        public byte R;
        public byte G;
        public byte B;
        public byte A;
        public string Name;

        public Color32 ToColor32()
        {
            return new Color32(R, G, B, A);
        }
    }

    internal class Palette
    {
        public List<string> Names = new List<string>();
        public uint FirstColorIndex;
        public uint LastColorIndex;
        public ColorData[] Colors;

        public Palette(BinaryReader r)
        {
            var size = r.ReadUInt32();

            r.Read(out FirstColorIndex);
            r.Read(out LastColorIndex);
            r.Skip(8);
            Colors = new ColorData[size];

            for (var i = FirstColorIndex; i <= LastColorIndex; ++i)
            {
                Colors[i].Flags = (ColorEntryFlags)r.ReadUInt16();
                r.Read(out Colors[i].R);
                r.Read(out Colors[i].G);
                r.Read(out Colors[i].B);
                r.Read(out Colors[i].A);

                if ((Colors[i].Flags & ColorEntryFlags.HasName) != 0)
                {
                    Colors[i].Name = r.ReadAseString();
                }
            }
        }
    }

    [System.Flags]
    internal enum SliceFlags
    {
        NineSlices = 1,
        HasPivot = 2,
    }

    internal struct SliceKey
    {
        public int Frame;
        public int X;
        public int Y;
        public int Width;
        public int Height;

        public int CenterX;
        public int CenterY;
        public int CenterWidth;
        public int CenterHeight;

        public int PivotX;
        public int PivotY;
    }

    internal class SliceSet
    {
        public Slice[] Slices;

        public SliceSet(BinaryReader r)
        {
            var numSlices = r.ReadUInt32();
            r.Skip(4);
            r.Skip(4);

            Slices = new Slice[numSlices];

            for (var i = 0; i < numSlices; ++i)
            {
                Slices[i] = new Slice(r);
            }
        }
    }

    internal class Slice
    {
        public SliceFlags Flags;
        public string Name;
        public SliceKey[] Keys;

        public Slice(int x, int y, int width, int height, Vector2 pivot)
        {
            Flags = SliceFlags.HasPivot;

            Keys = new SliceKey[1]
            {
                new SliceKey()
                {
                    X = x,
                    Y = y,
                    Width = width,
                    Height = height,
                    PivotX = (int)(width * pivot.x),
                    PivotY = (int)(height * pivot.y),
                }
            };
        }

        public Slice(BinaryReader r)
        {
            var numKeys = r.ReadUInt32();
            Keys = new SliceKey[numKeys];

            Flags = (SliceFlags)r.ReadUInt32();
            r.Skip(4);
            Name = r.ReadAseString();

            for (var i = 0; i < numKeys; ++i)
            {
                ref var key = ref Keys[i];

                r.Read(out key.Frame);
                r.Read(out key.X);
                r.Read(out key.Y);
                r.Read(out key.Width);
                r.Read(out key.Height);

                if ((Flags & SliceFlags.NineSlices) != 0)
                {
                    r.Read(out key.CenterX);
                    r.Read(out key.CenterY);
                    r.Read(out key.CenterWidth);
                    r.Read(out key.CenterHeight);
                    key.CenterX >>= 16;
                    key.CenterY >>= 16;
                    key.CenterWidth >>= 16;
                    key.CenterHeight >>= 16;
                }

                if ((Flags & SliceFlags.HasPivot) != 0)
                {
                    r.Read(out key.PivotX);
                    r.Read(out key.PivotY);
                }
            }
        }
    }

    [System.Flags]
    internal enum TilesetFlags
    {
        IncludeLinkToExternalFile = 1,
        IncludeTilesInsideThisFile = 2,
        TileIDZeroAsEmptyTile = 4,
    }

    internal class Tileset
    {
        public uint TilesetID;
        public TilesetFlags Flags;
        public uint NumberOfTiles;
        public ushort TileWidth;
        public ushort TileHeight;
        public short BaseIndex;
        public string Name;
        public uint ExternalFileID;
        public uint ExternalTilesetID;
        public byte[][] Tiles;

        public Tileset(BinaryReader r, int bitsPerPixel)
        {
            r.Read(out TilesetID);
            Flags = (TilesetFlags)r.ReadUInt32();
            r.Read(out NumberOfTiles);
            r.Read(out TileWidth);
            r.Read(out TileHeight);
            r.Read(out BaseIndex);
            r.Skip(14);
            Name = r.ReadAseString();

            if ((Flags & TilesetFlags.IncludeLinkToExternalFile) != 0)
            {
                r.Read(out ExternalFileID);
                r.Read(out ExternalTilesetID);
            }

            if ((Flags & TilesetFlags.IncludeTilesInsideThisFile) != 0)
            {
                var compressedDataLen = r.ReadUInt32();
                r.Skip(2);
                var compressedData = r.ReadBytes((int)compressedDataLen);

                using (var stream = new MemoryStream(compressedData))
                using (var deflate = new DeflateStream(stream, CompressionMode.Decompress))
                {
                    var bytesPerTile = bitsPerPixel / 8 * TileWidth * TileHeight;
                    var bytes = new byte[bytesPerTile * NumberOfTiles];
                    var len = deflate.Read(bytes, 0, bytes.Length);

                    if (len != bytes.Length)
                    {
                        throw new InvalidAseFileException();
                    }

                    Tiles = new byte[NumberOfTiles][];

                    for (var i = 0; i < NumberOfTiles; ++i)
                    {
                        Tiles[i] = new byte[bytesPerTile];
                        System.Array.Copy(bytes, bytesPerTile * i, Tiles[i], 0, bytesPerTile);
                    }
                }
            }
        }
    }

    internal enum CelType : ushort
    {
        RawImageData = 0,
        LinkedCel = 1,
        CompressedImage = 2,
        CompressedTilemap = 3,
    }

    [System.Flags]
    internal enum CelExtraFlags : ushort
    {
        PreciseBounds = 1
    }

    internal class Cel
    {
        public ushort LayerIndex;
        public short XPosition;
        public short YPosition;
        public byte Opacity;
        public CelType Type;
        public CelData Data;
        public CelExtraFlags ExtraFlags;
        public float PreciseXPosition;
        public float PreciseYPosition;
        public float ScaledWidth;
        public float ScaledHeight;

        public Layer Layer;

        public Cel(BinaryReader r, long endOfChunk, int bpp)
        {
            r.Read(out LayerIndex);
            r.Read(out XPosition);
            r.Read(out YPosition);
            r.Read(out Opacity);
            Type = (CelType)r.ReadUInt16();
            r.Skip(7);

            if (Type == CelType.RawImageData)
            {
                Data = new RawImageData(r, endOfChunk, bpp, false);
            }
            else if (Type == CelType.LinkedCel)
            {
                Data = new LinkedCelData() { FramePosition = r.ReadUInt16() };
            }
            else if (Type == CelType.CompressedImage)
            {
                Data = new RawImageData(r, endOfChunk, bpp, true);
            }
            else if (Type == CelType.CompressedTilemap)
            {
                Data = new TilemapData(r, endOfChunk);
            }
        }

        public void ReadExtraData(BinaryReader r)
        {
            ExtraFlags = (CelExtraFlags)r.ReadUInt32();

            if ((ExtraFlags & CelExtraFlags.PreciseBounds) != 0)
            {
                PreciseXPosition = r.ReadFixed();
                PreciseYPosition = r.ReadFixed();
                ScaledWidth = r.ReadFixed();
                ScaledHeight = r.ReadFixed();
            }

            r.Skip(16);
        }
    }

    internal abstract class CelData
    {
        public abstract Vector2Int Size { get; }
    }

    internal class RawImageData : CelData
    {
        public ushort Width;
        public ushort Height;
        public byte[] Pixels;

        public RawImageData(BinaryReader r, long endOfChunk, int bpp, bool compressed)
        {
            r.Read(out Width);
            r.Read(out Height);

            if (compressed)
            {
                r.Skip(2);

                Pixels = new byte[Width * Height * bpp / 8];

                var compressedData = r.ReadBytes((int)(endOfChunk - r.BaseStream.Position));

                using (var stream = new MemoryStream(compressedData))
                using (var deflate = new DeflateStream(stream, CompressionMode.Decompress))
                {
                    var len = deflate.Read(Pixels, 0, Pixels.Length);

                    if (len != Pixels.Length)
                    {
                        throw new InvalidAseFileException();
                    }
                }
            }
            else
            {
                Pixels = r.ReadBytes((int)(Width * Height * bpp / 8));
            }
        }

        public override Vector2Int Size => new Vector2Int(Width, Height);
    }

    internal class LinkedCelData : CelData
    {
        public ushort FramePosition;
        public CelData Data;

        public override Vector2Int Size => Data.Size;
    }

    internal class TilemapData : CelData
    {
        public ushort Width;
        public ushort Height;
        public ushort BitsPerTile;
        public uint TileIDMask;
        public uint XFlipMask;
        public uint YFlipMask;
        public uint RotationMask;
        public byte[] Tiles;

        public TilemapData(BinaryReader r, long endOfChunk)
        {
            r.Read(out Width);
            r.Read(out Height);
            r.Read(out BitsPerTile);
            r.Read(out TileIDMask);
            r.Read(out XFlipMask);
            r.Read(out YFlipMask);
            r.Read(out RotationMask);
            r.Skip(10);

            Tiles = new byte[Width * Height * BitsPerTile / 8];

            r.Skip(2);
            var compressedData = r.ReadBytes((int)(endOfChunk - r.BaseStream.Position));

            using (var stream = new MemoryStream(compressedData))
            using (var deflate = new DeflateStream(stream, CompressionMode.Decompress))
            {
                var len = deflate.Read(Tiles, 0, Tiles.Length);

                if (len != Tiles.Length)
                {
                    throw new InvalidAseFileException();
                }
            }
        }

        public override Vector2Int Size => new Vector2Int(Width, Height);
    }

    internal class TagSet
    {
        public List<Tag> Tags = new List<Tag>();

        public TagSet()
        {
        }

        public TagSet(BinaryReader r)
        {
            var numTags = r.ReadUInt16();
            r.Skip(8);

            for (var i = 0; i < numTags; ++i)
            {
                Tags.Add(new Tag(r));
            }
        }
    }

    internal enum LoopAnimationDirection : byte
    {
        Forward,
        Reverse,
        PingPong,
    }

    internal class Tag
    {
        public ushort From;
        public ushort To;
        public LoopAnimationDirection Direction;
        public string Name;

        public Tag(string name, ushort from, ushort to, LoopAnimationDirection direction)
        {
            Name = name;
            From = from;
            To = to;
            Direction = direction;
        }

        public Tag(BinaryReader r)
        {
            r.Read(out From);
            r.Read(out To);
            Direction = (LoopAnimationDirection)r.ReadByte();
            r.Skip(8);
            r.Skip(3);
            r.Skip(1);
            Name = r.ReadAseString();
        }
    }

    internal class AseFile
    {
        public  FileHeader Header;
        public Frame[] Frames;
        public List<Layer> Layers = new List<Layer>();
        public List<Layer> RootLayers = new List<Layer>();
        public Palette Palette;
        public TagSet TagSet;
        public List<Slice> Slices = new List<Slice>();
        public List<Tileset> Tilesets = new List<Tileset>();
        public ColorProfile ColorProfile;
        public ExternalFiles ExternalFiles;

        Dictionary<uint, Tileset> m_TilesetMap;

        public AseFile(string path)
        {
            // Reference; https://github.com/aseprite/aseprite/blob/main/docs/ase-file-specs.md
            using (var stream = File.OpenRead(path))
            using (var r = new BinaryReader(stream))
            {
                Header = new FileHeader(r);

                Frames = new Frame[Header.NumberOfFrames];

                for (var frameIndex = 0; frameIndex < Header.NumberOfFrames; ++frameIndex)
                {
                    ref var frame = ref Frames[frameIndex];
                    frame = new Frame(r, this);
                }

                if (0 < Tilesets.Count)
                {
                    m_TilesetMap = new Dictionary<uint, Tileset>(Tilesets.Count);
                    foreach (var tileset in Tilesets)
                    {
                        m_TilesetMap[tileset.TilesetID] = tileset;
                    }
                }
            }

            SetupReferences();
            SetupLayerHierarchy();
        }

        void SetupReferences()
        {
            foreach (var frame in Frames)
            {
                foreach (var cel in frame.Cels)
                {
                    cel.Layer = Layers[cel.LayerIndex];

                    if (cel.Data is LinkedCelData linkedCel)
                    {
                        foreach (var otherCel in Frames[linkedCel.FramePosition].Cels)
                        {
                            if (otherCel.LayerIndex == cel.LayerIndex)
                            {
                                linkedCel.Data = otherCel.Data;
                                break;
                            }
                        }
                    }
                }
            }
        }

        void SetupLayerHierarchy()
        {
            var parent = Layers[0];
            RootLayers.Add(parent);

            for (var i = 1; i < Layers.Count; ++i)
            {
                while (parent != null && Layers[i].ChildLevel <= parent.ChildLevel)
                {
                    parent = parent.Parent;
                }

                if (parent != null)
                {
                    parent.Children.Add(Layers[i]);
                    Layers[i].Parent = parent;
                }
                else
                {
                    RootLayers.Add(Layers[i]);
                }

                parent = Layers[i];
            }
        }

        internal Image GetImage(Frame frame, bool indexed = false)
        {
            var img = new Image(Header.Width, Header.Height);
            var palette = (!indexed && Header.BitsPerPixel == 8) ? Palette : null;

            for (var i = 0; i < frame.Cels.Count; ++i)
            {
                var cel = frame.Cels[i];
                var layer = Layers[cel.LayerIndex];

                if (!layer.IsVisible())
                {
                    continue;
                }

                var opacity = (Header.Flags & FileHeaderFlags.UseLayerOpacity) != 0 ? layer.Opacity : cel.Opacity;

                if (opacity <= 0)
                {
                    continue;
                }

                var blend = layer.BlendMode;

                if (Header.BitsPerPixel == 8)
                {
                    blend = BlendMode.Normal;
                }

                if (cel.Data is LinkedCelData linkedCel)
                {
                    foreach (var otherCel in Frames[linkedCel.FramePosition].Cels)
                    {
                        if (otherCel.LayerIndex == cel.LayerIndex)
                        {
                            cel = otherCel;
                            break;
                        }
                    }
                }

                if (cel.Data is RawImageData raw)
                {
                    CopyRawImageData(img, cel, raw, blend, opacity, palette);
                }
                else if (cel.Data is TilemapData tilemap)
                {
                    CopyTilemapImageData(img, cel, tilemap, m_TilesetMap[layer.TilesetIndex], blend, opacity, palette);
                }
            }

            return img;
        }

        Color32 GetPixel(int x, int y, byte[] pixels, int width, int height, Palette palette)
        {
            if (Header.BitsPerPixel == 8)
            {
                var index = pixels[x + y * width];
                Color32 col;

                if (palette == null)
                {
                    col = new Color32(index, 0, 0, 255);
                }
                else
                {
                    col = (0 <= index && index < palette.Colors.Length) ? palette.Colors[index].ToColor32() : default;
                }

                if (index == Header.TransparentColorIndex)
                {
                    col.a = 0;
                }

                return col;
            }
            else if (Header.BitsPerPixel == 16)
            {
                int offset = (x + y * width) * 2;
                var v = pixels[offset++];
                var a = pixels[offset++];
                var col = new Color32(v, v, v, a);
                return col;
            }
            else if (Header.BitsPerPixel == 32)
            {
                int offset = (x + y * width) * 4;
                var col = new Color32(pixels[offset++], pixels[offset++], pixels[offset++], pixels[offset++]);
                return col;
            }
            return default;
        }

        void CopyPixels(Image img, int dx, int dy, byte[] pixels, int width, int height, BlendMode blend, byte opacity, Palette palette)
        {
            for (int y = 0; y < height; ++y)
            {
                for (int x = 0; x < width; ++x)
                {
                    var col = GetPixel(x, y, pixels, width, height, palette);
                    img.Set(dx + x, dy + y, col, opacity, blend);
                }
            }
        }

        void CopyScaledPixels(Image img, float dx, float dy, float scaledWidth, float scaledHeight, byte[] pixels, int width, int height, BlendMode blend, byte opacity, Palette palette)
        {
            var sx = scaledWidth / width;
            var sy = scaledHeight / height;

            var destWidth = Mathf.CeilToInt(scaledWidth);
            var destHeight = Mathf.CeilToInt(scaledHeight);

            double xDelta = 1.0 / sx;

            for (int y = 0; y < destHeight; ++y)
            {
                int srcY = (int)(((double)y) / sy);

                if (srcY >= height)
                {
                    break;
                }

                for (int x = 0; x < destWidth;)
                {
                    var srcX = xDelta * x;

                    var col = GetPixel((int)srcX, srcY, pixels, width, height, palette);
                    img.Set((int)dx + x, (int)dy + y, col, opacity, blend);

                    ++x;

                    if (srcX >= width)
                    {
                        break;
                    }
                }
            }
        }

        void CopyRawImageData(Image img, Cel cel, RawImageData raw, BlendMode blend, byte opacity, Palette palette)
        {
            if ((cel.ExtraFlags & CelExtraFlags.PreciseBounds) == 0)
            {
                CopyPixels(img, cel.XPosition, cel.YPosition, raw.Pixels, raw.Width, raw.Height, blend, opacity, palette);
            }
            else
            {
                CopyScaledPixels(img, cel.PreciseXPosition, cel.PreciseXPosition, cel.ScaledWidth, cel.ScaledHeight, raw.Pixels, raw.Width, raw.Height, blend, opacity, palette);
            }
        }

        void CopyTilemapImageData(Image img, Cel cel, TilemapData tilemap, Tileset tileset, BlendMode blend, byte opacity, Palette palette)
        {
            int offset = 0;

            for (int y = 0; y < tilemap.Height; ++y)
            {
                for (int x = 0; x < tilemap.Width; ++x)
                {
                    var flags = 0u;

                    for (var i = 0; i < tilemap.BitsPerTile; i += 8)
                    {
                        flags |= (uint)tilemap.Tiles[offset] << i;
                        offset++;
                    }

                    var tileID = flags & tilemap.TileIDMask;
                   
                    if (((tileset.Flags & TilesetFlags.TileIDZeroAsEmptyTile) != 0 && tileID == 0) || tileID == 0xFFFFFFFF)
                    {
                        continue;
                    }

                    var xFlip = (flags & tilemap.XFlipMask) != 0;
                    var yFlip = (flags & tilemap.YFlipMask) != 0;
                    var rotate = (flags & tilemap.RotationMask) != 0;

                    CopyTileData(img, cel, tileset, tileID, blend, opacity, x * tileset.TileWidth, y * tileset.TileHeight, xFlip, yFlip, rotate, palette);
                }
            }
        }

        void CopyTileData(Image img, Cel cel, Tileset tileset, uint tileID, BlendMode blend, byte opacity, int destX, int destY, bool xFlip, bool yFlip, bool rotate, Palette palette)
        {
            var tile = tileset.Tiles[tileID];
            CopyPixels(img, cel.XPosition + destX, cel.YPosition + destY, tile, tileset.TileWidth, tileset.TileHeight, blend, opacity, palette);
        }
    }

    // Reference: https://github.com/aseprite/aseprite/blob/main/src/doc/blend_funcs.cpp
    static class BlendFuncs
    {
        static byte MUL_UN8(int a, int b)
        {
            return (byte)(a * b / 255);
        }

        static byte DIV_UN8(int a, int b)
        {
            return (byte)(a * 255 / b);
        }

        public static Color32 BlendNormal(Color32 dest, Color32 src, int opacity)
        {
            if (dest.a <= 0)
            {
                src.a = MUL_UN8(src.a, opacity);
                return src;
            }
            else if (src.a <= 0)
            {
                return dest;
            }

            int Br = dest.r;
            int Bg = dest.g;
            int Bb = dest.b;
            int Ba = dest.a;

            int Sr = src.r;
            int Sg = src.g;
            int Sb = src.b;
            int Sa = src.a;
            Sa = MUL_UN8(Sa, opacity);

            int Ra = Sa + Ba - MUL_UN8(Ba, Sa);
            int Rr = Br + (Sr - Br) * Sa / Ra;
            int Rg = Bg + (Sg - Bg) * Sa / Ra;
            int Rb = Bb + (Sb - Bb) * Sa / Ra;

            return new Color32((byte)Rr, (byte)Rg, (byte)Rb, (byte)Ra);
        }

        public static byte BlendMultiply(int a, int b)
        {
            return MUL_UN8(a, b);
        }

        public static byte BlendOverlay(int a, int b)
        {
            return BlendHardLight(b, a);
        }

        public static byte BlendScreen(int a, int b)
        {
            return (byte)(a + b - BlendMultiply(a, b));
        }

        public static byte BlendHardLight(int a, int b)
        {
            return b < 128 ? BlendMultiply(a, b << 1) : BlendScreen((a << 1) - 255, b);
        }

        public static byte BlendExclusion(int a, int b)
        {
            return (byte)(a + b - (2 * MUL_UN8(a, b)));
        }

        public static byte BlendDifference(int a, int b)
        {
            return (byte)Mathf.Abs(a - b);
        }

        public static byte BlendColorBurn(int a, int b)
        {
            if (a == 255)
            {
                return 255;
            }

            a = (255 - a);

            if (a >= b)
            {
                return 0;
            }
            else
            {
                return (byte)(255 - DIV_UN8(a, b));
            }
        }

        public static byte BlendSoftLight(byte a, byte b)
        {
            var na = a / 255.0;
            var nb = b / 255.0;
            double r, d;

            if (na <= 0.25)
            {
                d = ((16 * na - 12) * na + 4) * na;
            }
            else
            {
                d = System.Math.Sqrt(na);
            }

            if (nb <= 0.5)
            {
                r = na - (1.0 - 2.0 * nb) * na * (1.0 - na);
            }
            else
            {
                r = na + (2.0 * nb - 1.0) * (d - na);
            }

            return (byte)(r * 255 + 0.5);
        }

        public static byte BlendDivide(int a, int b)
        {
            if (a == 0)
            {
                return 0;
            }
            else if (a >= b)
            {
                return 255;
            }
            else
            {
                return DIV_UN8(a, b);
            }
        }

        public static byte BlendColorDodge(int a, int b)
        {
            if (a == 0)
            {
                return 0;
            }

            b = (255 - b);

            if (a >= b)
            {
                return 255;
            }
            else
            {
                return DIV_UN8(a, b);
            }
        }

        static double lum(double r, double g, double b)
        {
            return 0.3 * r + 0.59 * g + 0.11 * b;
        }

        static double sat(double r, double g, double b)
        {
            return System.Math.Max(r, System.Math.Max(g, b)) - System.Math.Min(r, System.Math.Min(g, b));
        }

        static void clip_color(ref double r, ref double g, ref double b)
        {
            var l = lum(r, g, b);
            var n = System.Math.Min(r, System.Math.Min(g, b));
            var x = System.Math.Max(r, System.Math.Max(g, b));

            if (n < 0)
            {
                r = l + (((r - l) * l) / (l - n));
                g = l + (((g - l) * l) / (l - n));
                b = l + (((b - l) * l) / (l - n));
            }

            if (x > 1)
            {
                r = l + (((r - l) * (1 - l)) / (x - l));
                g = l + (((g - l) * (1 - l)) / (x - l));
                b = l + (((b - l) * (1 - l)) / (x - l));
            }
        }

        static void set_lum(ref double r, ref double g, ref double b, double l)
        {
            var d = l - lum(r, g, b);
            r += d;
            g += d;
            b += d;
            clip_color(ref r, ref g, ref b);
        }

        static void set_sat(ref double r, ref double g, ref double b, double s)
        {
            static ref double MIN(ref double x, ref double y)
            {
                if (x < y)
                {
                    return ref x;
                }
                else
                {
                    return ref y;
                }
            }

            static ref double MAX(ref double x, ref double y)
            {
                if (x > y)
                {
                    return ref x;
                }
                else
                {
                    return ref y;
                }
            }

            static ref double MID(ref double x, ref double y, ref double z)
            {
                if (x > y)
                {
                    if (y > z)
                    {
                        return ref y;
                    }
                    else
                    {
                        if (x > z)
                        {
                            return ref z;
                        }
                        else
                        {
                            return ref x;
                        }
                    }
                }
                else
                {
                    if (y > z)
                    {
                        if (z > x)
                        {
                            return ref z;
                        }
                        else
                        {
                            return ref x;
                        }
                    }
                    else
                    {
                        return ref y;
                    }
                }
            }

            ref double min = ref MIN(ref r, ref MIN(ref g, ref b));
            ref double mid = ref MID(ref r, ref g, ref b);
            ref double max = ref MAX(ref r, ref MAX(ref g, ref b));

            if (max > min)
            {
                mid = ((mid - min) * s) / (max - min);
                max = s;
            }
            else
                mid = max = 0;

            min = 0;
        }

        public static Color32 BlendHSLHue(Color32 dest, Color32 src)
        {
            var r = dest.r / 255.0;
            var g = dest.g / 255.0;
            var b = dest.b / 255.0;
            var s = sat(r, g, b);
            var l = lum(r, g, b);

            r = src.r / 255.0;
            g = src.g / 255.0;
            b = src.b / 255.0;

            set_sat(ref r, ref g, ref b, s);
            set_lum(ref r, ref g, ref b, l);

            return new Color32((byte)(r * 255), (byte)(g * 255), (byte)(b * 255), src.a);
        }

        public static Color32 BlendHSLSaturation(Color32 dest, Color32 src)
        {
            var r = src.r / 255.0;
            var g = src.g / 255.0;
            var b = src.b / 255.0;
            var s = sat(r, g, b);

            r = dest.r / 255.0;
            g = dest.g / 255.0;
            b = dest.b / 255.0;
            var l = lum(r, g, b);

            set_sat(ref r, ref g, ref b, s);
            set_lum(ref r, ref g, ref b, l);

            return new Color32((byte)(r * 255), (byte)(g * 255), (byte)(b * 255), src.a);
        }

        public static Color32 BlendHSLColor(Color32 dest, Color32 src)
        {
            var r = dest.r / 255.0;
            var g = dest.g / 255.0;
            var b = dest.b / 255.0;
            var l = lum(r, g, b);

            r = src.r / 255.0;
            g = src.g / 255.0;
            b = src.b / 255.0;

            set_lum(ref r, ref g, ref b, l);

            return new Color32((byte)(r * 255), (byte)(g * 255), (byte)(b * 255), src.a);
        }

        public static Color32 BlendHSLLuminosity(Color32 dest, Color32 src)
        {
            var r = src.r / 255.0;
            var g = src.g / 255.0;
            var b = src.b / 255.0;
            var l = lum(r, g, b);

            r = dest.r / 255.0;
            g = dest.g / 255.0;
            b = dest.b / 255.0;

            set_lum(ref r, ref g, ref b, l);

            return new Color32((byte)(r * 255), (byte)(g * 255), (byte)(b * 255), src.a);
        }
    }

    internal class Image : System.IEquatable<Image>
    {
        public int XOffset;
        public int YOffset;
        public int Width;
        public int Height;
        public Color32[] Pixels;

        public Image(int w, int h)
        {
            Width = w;
            Height = h;
            Pixels = new Color32[w * h];
        }

        public Color32 this[int x, int y]
        {
            set { Pixels[x + y * Width] = value; }
            get { return Pixels[x + y * Width]; }
        }

        public void FlipV()
        {
            var y0 = 0;
            var y1 = Height - 1;

            while (y0 < y1)
            {
                for (var x = 0; x < Width; ++x)
                {
                    var tmp = Pixels[x + y0 * Width];
                    Pixels[x + y0 * Width] = Pixels[x + y1 * Width];
                    Pixels[x + y1 * Width] = tmp;
                }
                ++y0;
                --y1;
            }
        }

        public void Crop(RectInt rect)
        {
            if (rect.x == XOffset && rect.y == YOffset && rect.width == Width && rect.height == Height)
            {
                return;
            }

            var newWidth = rect.max.x - rect.min.x;
            var newHeight = rect.max.y - rect.min.y;
            var newPixels = new Color32[newWidth * newHeight];

            for (var y = 0; y < newHeight; ++y)
            {
                for (var x = 0; x < newWidth; ++x)
                {
                    newPixels[x + y * newWidth] = Pixels[rect.min.x + x + (rect.min.y + y) * Width];
                }
            }

            Pixels = newPixels;
            Width = newWidth;
            Height = newHeight;
            XOffset = rect.min.x;
            YOffset = rect.min.y;
        }

        public void Trim()
        {
            int minX;
            int maxX;
            int minY;
            int maxY;

            for (minX = 0; minX < Width; ++minX)
            {
                var hit = false;

                for (var y = 0; y < Height; ++y)
                {
                    if (this[minX, y].a > 0)
                    {
                        hit = true;
                        break;
                    }
                }

                if (hit)
                {
                    break;
                }
            }

            for (minY = 0; minY < Height; ++minY)
            {
                var hit = false;

                for (var x = 0; x < Width; ++x)
                {
                    if (this[x, minY].a > 0)
                    {
                        hit = true;
                        break;
                    }
                }

                if (hit)
                {
                    break;
                }
            }

            for (maxX = Width - 1; 0 <= maxX; --maxX)
            {
                var hit = false;

                for (var y = 0; y < Height; ++y)
                {
                    if (this[maxX, y].a > 0)
                    {
                        hit = true;
                        break;
                    }
                }

                if (hit)
                {
                    break;
                }
            }

            for (maxY = Height - 1; 0 <= maxY; --maxY)
            {
                var hit = false;

                for (var x = 0; x < Width; ++x)
                {
                    if (this[x, maxY].a > 0)
                    {
                        hit = true;
                        break;
                    }
                }

                if (hit)
                {
                    break;
                }
            }

            Crop(new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1));
        }

        static byte Clamp(int val)
        {
            return (byte)Mathf.Clamp(val, 0, 255);
        }

        public void Set(int x, int y, Color32 col, int opacity, BlendMode blend)
        {
            if (x < 0 || y < 0 || Width <= x || Height <= y)
            {
                return;
            }

            var dest = this[x, y];
            var src = col;

            switch (blend)
            {
                case BlendMode.Multiply:
                    src.r = BlendFuncs.BlendMultiply(dest.r, src.r);
                    src.g = BlendFuncs.BlendMultiply(dest.g, src.g);
                    src.b = BlendFuncs.BlendMultiply(dest.b, src.b);
                    break;
                case BlendMode.Screen:
                    src.r = BlendFuncs.BlendScreen(dest.r, src.r);
                    src.g = BlendFuncs.BlendScreen(dest.g, src.g);
                    src.b = BlendFuncs.BlendScreen(dest.b, src.b);
                    break;
                case BlendMode.Overlay:
                    src.r = BlendFuncs.BlendOverlay(dest.r, src.r);
                    src.g = BlendFuncs.BlendOverlay(dest.g, src.g);
                    src.b = BlendFuncs.BlendOverlay(dest.b, src.b);
                    break;
                case BlendMode.Darken:
                    src.r = (byte)Mathf.Min(src.r, dest.r);
                    src.g = (byte)Mathf.Min(src.g, dest.g);
                    src.b = (byte)Mathf.Min(src.b, dest.b);
                    break;
                case BlendMode.Lighten:
                    src.r = (byte)Mathf.Max(src.r, dest.r);
                    src.g = (byte)Mathf.Max(src.g, dest.g);
                    src.b = (byte)Mathf.Max(src.b, dest.b);
                    break;
                case BlendMode.ColorDodge:
                    src.r = BlendFuncs.BlendColorDodge(dest.r, src.r);
                    src.g = BlendFuncs.BlendColorDodge(dest.g, src.g);
                    src.b = BlendFuncs.BlendColorDodge(dest.b, src.b);
                    break;
                case BlendMode.ColorBurn:
                    src.r = BlendFuncs.BlendColorBurn(dest.r, src.r);
                    src.g = BlendFuncs.BlendColorBurn(dest.g, src.g);
                    src.b = BlendFuncs.BlendColorBurn(dest.b, src.b);
                    break;
                case BlendMode.HardLight:
                    src.r = BlendFuncs.BlendHardLight(dest.r, src.r);
                    src.g = BlendFuncs.BlendHardLight(dest.g, src.g);
                    src.b = BlendFuncs.BlendHardLight(dest.b, src.b);
                    break;
                case BlendMode.SoftLight:
                    src.r = BlendFuncs.BlendSoftLight(dest.r, src.r);
                    src.g = BlendFuncs.BlendSoftLight(dest.g, src.g);
                    src.b = BlendFuncs.BlendSoftLight(dest.b, src.b);
                    break;
                case BlendMode.Difference:
                    src.r = BlendFuncs.BlendDifference(dest.r, src.r);
                    src.g = BlendFuncs.BlendDifference(dest.g, src.g);
                    src.b = BlendFuncs.BlendDifference(dest.b, src.b);
                    break;
                case BlendMode.Exclusion:
                    src.r = BlendFuncs.BlendExclusion(dest.r, src.r);
                    src.g = BlendFuncs.BlendExclusion(dest.g, src.g);
                    src.b = BlendFuncs.BlendExclusion(dest.b, src.b);
                    break;
                case BlendMode.Hue:
                    src = BlendFuncs.BlendHSLHue(dest, src);
                    break;
                case BlendMode.Saturation:
                    src = BlendFuncs.BlendHSLSaturation(dest, src);
                    break;
                case BlendMode.Color:
                    src = BlendFuncs.BlendHSLColor(dest, src);
                    break;
                case BlendMode.Luminosity:
                    src = BlendFuncs.BlendHSLLuminosity(dest, src);
                    break;
                case BlendMode.Addition:
                    src.r = Clamp(dest.r + src.r);
                    src.g = Clamp(dest.g + src.g);
                    src.b = Clamp(dest.b + src.b);
                    break;
                case BlendMode.Subtract:
                    src.r = Clamp(dest.r - src.r);
                    src.g = Clamp(dest.g - src.g);
                    src.b = Clamp(dest.b - src.b);
                    break;
                case BlendMode.Divide:
                    src.r = BlendFuncs.BlendDivide(dest.r, src.r);
                    src.g = BlendFuncs.BlendDivide(dest.g, src.g);
                    src.b = BlendFuncs.BlendDivide(dest.b, src.b);
                    break;
            }

            this[x, y] = BlendFuncs.BlendNormal(dest, src, opacity);
        }

        public bool Equals(Image other)
        {
            return XOffset == other.XOffset &&
                YOffset == other.YOffset &&
                Width == other.Width &&
                Height == other.Height &&
                Pixels.SequenceEqual(other.Pixels);
        }
    }

    static class BinaryReaderExt
    {
        public static void Skip(this BinaryReader reader, int bytes)
        {
            reader.BaseStream.Seek(bytes, SeekOrigin.Current);
        }
        public static void Read(this BinaryReader reader, out byte result)
        {
            result = reader.ReadByte();
        }
        public static void Read(this BinaryReader reader, out short result)
        {
            result = reader.ReadInt16();
        }
        public static void Read(this BinaryReader reader, out ushort result)
        {
            result = reader.ReadUInt16();
        }
        public static void Read(this BinaryReader reader, out int result)
        {
            result = reader.ReadInt32();
        }
        public static void Read(this BinaryReader reader, out uint result)
        {
            result = reader.ReadUInt32();
        }
        public static string ReadAseString(this BinaryReader reader)
        {
            var len = reader.ReadUInt16();
            var bytes = reader.ReadBytes(len);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        public static float ReadFixed(this BinaryReader reader)
        {
            var val = reader.ReadInt32();
            return val / 65536.0f;
        }
    }

}
