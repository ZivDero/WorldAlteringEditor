﻿using Microsoft.Xna.Framework;
using Rampastring.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TSMapEditor.GameMath;
using TSMapEditor.UI;

namespace TSMapEditor.Models
{
    public enum CliffSide
    {
        Front,
        Back
    }

    public struct CliffConnectionPoint
    {
        /// <summary>
        /// Index of the connection point, 0 or 1
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// Offset of this connection point relative to the tile's (0,0) point
        /// </summary>
        public Vector2 CoordinateOffset { get; set; }

        /// <summary>
        /// Mask of bits determining which way the connection point "faces".
        /// Ordered in the same way as the Directions enum
        /// </summary>
        public byte ConnectionMask { get; set; }

        /// <summary>
        /// Connection mask with its first and last half swapped to bitwise and it with the opposing cliff's mask
        /// </summary>
        public byte ReversedConnectionMask => (byte)((ConnectionMask >> 4) + (0b11110000 & (ConnectionMask << 4)));

        /// <summary>
        /// Whether the connection point faces "backwards" or "forwards"
        /// </summary>
        public CliffSide Side { get; set; }
    }

    public class CliffAStarNode
    {
        private CliffAStarNode() {}

        public CliffAStarNode(CliffAStarNode parent, CliffConnectionPoint exit, Vector2 location, CliffTile tile)
        {
            Location = location;
            Tile = tile;

            Parent = parent;
            Exit = exit;
            Destination = Parent.Destination;

            OccupiedCells = new HashSet<Vector2>(parent.OccupiedCells);
            OccupiedCells.UnionWith(tile.Foundation.Select(coordinate => coordinate + Location));
        }

        public static CliffAStarNode MakeStartNode(Vector2 location, Vector2 destination, CliffSide startingSide)
        {
            CliffConnectionPoint connectionPoint = new CliffConnectionPoint
            {
                ConnectionMask = 0b11111111,
                CoordinateOffset = new Vector2(0, 0),
                Side = startingSide
            };

            var startNode = new CliffAStarNode()
            {
                Location = location,
                Tile = null,

                Parent = null,
                Exit = connectionPoint,
                Destination = destination
            };

            return startNode;
        }

        public List<CliffAStarNode> GetNextNodes(CliffTile tile)
        {
            List<(CliffConnectionPoint, List<Direction>)> possibleNeighbors = new();

            foreach (CliffConnectionPoint cp in tile.ConnectionPoints)
            {
                var possibleDirections = GetDirectionsInMask((byte)(cp.ReversedConnectionMask & Exit.ConnectionMask));
                if (possibleDirections.Count == 0)
                    continue;

                possibleNeighbors.Add((cp, possibleDirections));
            }

            var neighbors = new List<CliffAStarNode>();
            foreach (var (connectionPoint, directions) in possibleNeighbors)
            {
                if (connectionPoint.Side != Exit.Side)
                    continue;

                foreach (Direction dir in directions)
                {
                    Vector2 placementOffset = Helpers.VisualDirectionToPoint(dir).ToXNAVector() - connectionPoint.CoordinateOffset;
                    Vector2 placementCoords = ExitCoords + placementOffset;

                    var exit = tile.GetExit(connectionPoint.Index);
                    var newNode = new CliffAStarNode(this, exit, placementCoords, tile);

                    // Make sure that the new node doesn't overlap anything
                    if (newNode.OccupiedCells.Count - OccupiedCells.Count == newNode.Tile.Foundation.Count)
                        neighbors.Add(newNode);
                }
            }
            return neighbors;
        }

        public List<CliffAStarNode> GetNextNodes(List<CliffTile> tiles)
        {
            return tiles.SelectMany(GetNextNodes).ToList();
        }

        private List<Direction> GetDirectionsInMask(byte mask)
        {
            List<Direction> directions = new List<Direction>();

            for (int direction = 0; direction < (int)Direction.Count; direction++)
            {
                if ((mask & (byte)(0b10000000 >> direction)) > 0)
                    directions.Add((Direction)direction);
            }

            return directions;
        }

        /// <summary>
        /// Absolute world coordinates of the node's tile
        /// </summary>
        public Vector2 Location;

        /// <summary>
        /// Absolute world coordinates of the node's tile's exit
        /// </summary>
        public Vector2 ExitCoords => Location + Exit.CoordinateOffset;

        /// <summary>
        /// Tile data
        /// </summary>
        public CliffTile Tile;

        ///// A* Stuff

        /// <summary>
        /// A* end point
        /// </summary>
        public Vector2 Destination;

        /// <summary>
        /// Where this node connects to the next node
        /// </summary>
        public CliffConnectionPoint Exit;

        /// <summary>
        /// Distance from starting node
        /// </summary>
        public float GScore => Parent == null ? 0 : Parent.GScore + Vector2.Distance(Parent.ExitCoords, ExitCoords);

        /// <summary>
        /// Distance to end node
        /// </summary>
        public float HScore => Vector2.Distance(Destination, ExitCoords);
        public float FScore => GScore * 0.8f + HScore;

        /// <summary>
        /// Previous node
        /// </summary>
        public CliffAStarNode Parent;

        /// <summary>
        /// Accumulated set of all cell coordinates occupied up to this node
        /// </summary>
        public HashSet<Vector2> OccupiedCells = new HashSet<Vector2>();
    }

    public class CliffTile
    {
        public CliffTile(IniSection iniSection, int index)
        {
            Index = index;

            string indicesString = iniSection.GetStringValue("TileIndices", null);
            if (indicesString == null || !Regex.IsMatch(indicesString, "^((?:\\d+?,)*(?:\\d+?))$"))
                throw new INIConfigException($"Cliff {iniSection.SectionName} has invalid TileIndices list: {indicesString}!");


            string tileSet = iniSection.GetStringValue("TileSet", null);
            if (string.IsNullOrWhiteSpace(tileSet))
                throw new INIConfigException($"Cliff {iniSection.SectionName} has no TileSet!");

            TileSet = tileSet;

            IndicesInTileSet = indicesString.Split(',').Select(int.Parse).ToList();

            ConnectionPoints = new CliffConnectionPoint[2];

            for (int i = 0; i < 2; i++)
            {
                string coordsString = iniSection.GetStringValue($"ConnectionPoint{i}", null);
                if (coordsString == null || !Regex.IsMatch(coordsString, "^\\d+?,\\d+?$"))
                    throw new INIConfigException($"Cliff {iniSection.SectionName} has invalid ConnectionPoint{i} value: {coordsString}!");

                var coordParts = coordsString.Split(',').Select(int.Parse).ToList();
                Vector2 coords = new Vector2(coordParts[0], coordParts[1]);

                string directionsString = iniSection.GetStringValue($"ConnectionPoint{i}.Directions", null);
                if (directionsString == null || directionsString.Length != (int)Direction.Count || Regex.IsMatch(directionsString, "[^01]"))
                    throw new INIConfigException($"Cliff {iniSection.SectionName} has invalid ConnectionPoint{i}.Directions value: {directionsString}!");

                byte directions = Convert.ToByte(directionsString, 2);

                string sideString = iniSection.GetStringValue($"ConnectionPoint{i}.Side", string.Empty);
                CliffSide side = sideString.ToLower() switch
                {
                    "front" => CliffSide.Front,
                    "back" => CliffSide.Back,
                    _ => throw new INIConfigException($"Cliff {iniSection.SectionName} has an invalid ConnectionPoint{i}.Side value: {sideString}!")
                };

                ConnectionPoints[i] = new CliffConnectionPoint
                {
                    Index = i,
                    ConnectionMask = directions,
                    CoordinateOffset = coords,
                    Side = side
                };
            }

            string foundationString = iniSection.GetStringValue("Foundation", string.Empty);
            if (!Regex.IsMatch(foundationString, "^((?:\\d+?,\\d+?\\|)*(?:\\d+?,\\d+?))$"))
                throw new INIConfigException($"Cliff {iniSection.SectionName} has an invalid Foundation: {foundationString}!");

            Foundation = foundationString.Split("|").Select(coordinateString =>
            {
                var coordinateParts = coordinateString.Split(",");
                return new Vector2(int.Parse(coordinateParts[0]), int.Parse(coordinateParts[1]));
            }).ToHashSet();
        }

        /// <summary>
        /// Tile's in-editor index
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// Tile's Tile Set
        /// </summary>
        public string TileSet { get; set; }

        /// <summary>
        /// Indices of tiles relative to the Tile Set
        /// </summary>
        public List<int> IndicesInTileSet { get; set; }

        /// <summary>
        /// Places this tile connects to other tiles
        /// </summary>
        public CliffConnectionPoint[] ConnectionPoints { get; set; }

        /// <summary>
        /// Set of all relative cell coordinates this tile occupies
        /// </summary>
        public HashSet<Vector2> Foundation { get; set; }

        public CliffConnectionPoint GetExit(int entryIndex)
        {
            return ConnectionPoints[0].Index == entryIndex ? ConnectionPoints[1] : ConnectionPoints[0];
        }
    }

    public class CliffType
    {
        public static CliffType FromIniSection(IniFile iniFile, string sectionName)
        {
            IniSection cliffSection = iniFile.GetSection(sectionName);
            if (cliffSection == null)
                return null;

            string cliffName = cliffSection.GetStringValue("Name", null);

            if (string.IsNullOrEmpty(cliffName))
                return null;

            var allowedTheaters = cliffSection.GetListValue("AllowedTheaters", ',', s => s);

            return new CliffType(iniFile, sectionName, cliffName, allowedTheaters);
        }

        private CliffType(IniFile iniFile, string iniName, string name, List<string> allowedTheaters)
        {
            IniName = iniName;
            Name = name;
            AllowedTheaters = allowedTheaters;

            Tiles = new List<CliffTile>();

            foreach (var sectionName in iniFile.GetSections())
            {
                var parts = sectionName.Split('.');
                if (parts.Length != 2 || parts[0] != IniName || !int.TryParse(parts[1], out int index))
                    continue;

                Tiles.Add(new CliffTile(iniFile.GetSection(sectionName), index));
            }
        }

        public string IniName { get; set; }
        public string Name { get; set; }
        public List<string> AllowedTheaters { get; set; }
        public List<CliffTile> Tiles { get; set; }
    }
}
