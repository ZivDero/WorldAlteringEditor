using Microsoft.Xna.Framework;
using SharpDX.MediaFoundation;
using System;
using System.Collections.Generic;
using System.Linq;
using TSMapEditor.GameMath;

namespace TSMapEditor.Models
{
    public interface IMovable : IPositioned
    {
        RTTIType WhatAmI();

        bool IsTechno();
    }

    /// <summary>
    /// A base class for game objects.
    /// Represents ObjectClass in the original game's class hierarchy.
    /// </summary>
    public abstract class GameObject : AbstractObject, IMovable
    {
        public virtual Point2D Position { get; set; }

        public ulong LastRefreshIndex;
        public List<MapTile> LitTiles { get; set; } = new();

        public abstract GameObjectType GetObjectType();

        public virtual int GetYDrawOffset()
        {
            return 0;
        }

        public virtual int GetXDrawOffset()
        {
            return 0;
        }

        public virtual int GetFrameIndex(int frameCount)
        {
            return 0;
        }

        public virtual int GetShadowFrameIndex(int frameCount)
        {
            return frameCount / 2;
        }

        public override int GetHashCode()
        {
            return (int)WhatAmI() * 10000000 + Position.Y * 512 + Position.X;
        }

        public virtual bool Remapable() => false;

        public virtual bool IsInvisibleInGame() => false;

        public virtual bool HasShadow() => false;

        public virtual bool IsOnBridge() => false;

        public virtual Color GetRemapColor() => Color.White;

        public virtual void LightTilesAt(Point2D center, int radius, MapTile[][] tiles, Dictionary<MapTile, double> litTiles)
        {
            Point2D centerPosition = Position + center;
            int startX = Math.Max(centerPosition.X - radius, 0);
            int endX = Math.Min(centerPosition.X + radius, tiles[0].Length - 1);
            int startY = Math.Max(centerPosition.Y - radius, 0);
            int endY = Math.Min(centerPosition.Y + radius, tiles.Length - 1);

            for (int y = startY; y <= endY; y++)
            {
                for (int x = startX; x <= endX; x++)
                {
                    MapTile tile = tiles[y][x];

                    if (tile == null)
                        continue;

                    int xDifference = centerPosition.X - x;
                    int yDifference = centerPosition.Y - y;

                    double distanceInCells = Math.Sqrt(xDifference * xDifference + yDifference * yDifference);
                    double distanceInLeptons = distanceInCells * Constants.CellSizeInLeptons;

                    if (distanceInLeptons > GetObjectType().LightVisibility)
                        continue;

                    if (!litTiles.ContainsKey(tile) ||
                        (litTiles.ContainsKey(tile) && litTiles[tile] > distanceInLeptons))
                    {
                        litTiles[tile] = distanceInLeptons;
                    }
                }
            }
        }

        public virtual void LightTiles(MapTile[][] tiles)
        {
            ClearLitTiles();

            Dictionary<MapTile, double> litTiles = new();

            int radius = (int)Math.Ceiling((double)GetObjectType().LightVisibility / Constants.CellSizeInLeptons) - 1;

            LightTilesAt(new Point2D(0, 0), radius, tiles, litTiles);

            foreach (var kvp in litTiles)
            {
                kvp.Key.LightSources.Add((this, kvp.Value));
            }

            LitTiles = litTiles.Keys.ToList();
        }

        public void ClearLitTiles()
        {
            foreach (var tile in LitTiles)
            {
                tile.LightSources.RemoveAll(source => source.Source == this);
            }

            LitTiles.Clear();
        }
    }
}
