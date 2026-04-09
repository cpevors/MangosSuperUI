using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace MangosSuperUI.Services;

/// <summary>
/// Reads pre-extracted VMaNGOS .map files to resolve terrain height (Z) for any world (X, Y) coordinate.
/// Direct C# port of the GridMap height interpolation from VMaNGOS GridMap.cpp.
/// Registered as a singleton — tiles are lazy-loaded and cached in memory.
/// </summary>
public class HeightMapService
{
    private const float SIZE_OF_GRIDS = 533.33333f;
    private const int MAP_RESOLUTION = 128;
    private const float INVALID_HEIGHT = -200000.0f;

    // .map file header magic
    private static readonly byte[] MAP_MAGIC = "MAPS"u8.ToArray();
    private static readonly byte[] MAP_VERSION = "z1.4"u8.ToArray();
    private static readonly byte[] HEIGHT_MAGIC = "MHGT"u8.ToArray();

    // Flags
    private const uint MAP_HEIGHT_NO_HEIGHT = 0x0001;
    private const uint MAP_HEIGHT_AS_INT16 = 0x0002;
    private const uint MAP_HEIGHT_AS_INT8 = 0x0004;

    private readonly string _mapsPath;
    private readonly ConcurrentDictionary<(int mapId, int tileX, int tileY), GridTile?> _cache = new();
    private readonly ILogger<HeightMapService> _logger;

    public HeightMapService(IOptions<VmangosSettings> settings, ILogger<HeightMapService> logger)
    {
        _mapsPath = settings.Value.MapsDataPath ?? "";
        _logger = logger;

        if (string.IsNullOrEmpty(_mapsPath) || !Directory.Exists(_mapsPath))
            _logger.LogWarning("HeightMapService: MapsDataPath '{Path}' not found — Z lookups will return null", _mapsPath);
        else
            _logger.LogInformation("HeightMapService: using maps at {Path}", _mapsPath);
    }

    /// <summary>
    /// Get terrain height at the given world coordinates.
    /// Returns null if the tile doesn't exist or the point is in a terrain hole.
    /// </summary>
    public float? GetHeight(int mapId, float worldX, float worldY)
    {
        if (string.IsNullOrEmpty(_mapsPath)) return null;

        // Determine which tile file covers this point
        // VMaNGOS: gx = (int)(32 - y / SIZE_OF_GRIDS), gy = (int)(32 - x / SIZE_OF_GRIDS)
        // File naming: maps/{mapId:03d}{tileY:02d}{tileX:02d}.map  where tileY=gy, tileX=gx
        int gx = (int)(32 - worldY / SIZE_OF_GRIDS);
        int gy = (int)(32 - worldX / SIZE_OF_GRIDS);

        if (gx < 0 || gx > 63 || gy < 0 || gy > 63) return null;

        var tile = GetOrLoadTile(mapId, gx, gy);
        if (tile == null) return null;

        return tile.GetHeight(worldX, worldY);
    }

    /// <summary>
    /// Check whether the maps data path is configured and valid.
    /// </summary>
    public bool IsAvailable => !string.IsNullOrEmpty(_mapsPath) && Directory.Exists(_mapsPath);

    private GridTile? GetOrLoadTile(int mapId, int tileX, int tileY)
    {
        var key = (mapId, tileX, tileY);
        return _cache.GetOrAdd(key, k =>
        {
            try
            {
                return LoadTile(k.mapId, k.tileX, k.tileY);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load map tile {MapId}/{TileX},{TileY}", k.mapId, k.tileX, k.tileY);
                return null;
            }
        });
    }

    private GridTile? LoadTile(int mapId, int tileX, int tileY)
    {
        // VMaNGOS naming: maps/%03u%02u%02u.map  →  maps/000YYXX.map
        // where the snprintf args are (mapId, tileY, tileX) — note: file name is mapId, Y, X
        var filename = $"{mapId:D3}{tileY:D2}{tileX:D2}.map";
        var path = Path.Combine(_mapsPath, filename);

        if (!File.Exists(path)) return null;

        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);

        // ── File header (40 bytes) ──
        var mapMagic = br.ReadUInt32();
        var versionMagic = br.ReadUInt32();

        if (mapMagic != BitConverter.ToUInt32(MAP_MAGIC) ||
            versionMagic != BitConverter.ToUInt32(MAP_VERSION))
        {
            _logger.LogWarning("Map file {File} has wrong magic/version", filename);
            return null;
        }

        var areaMapOffset = br.ReadUInt32();
        var areaMapSize = br.ReadUInt32();
        var heightMapOffset = br.ReadUInt32();
        var heightMapSize = br.ReadUInt32();
        // liquidMapOffset, liquidMapSize, holesOffset, holesSize — skip for now
        var liquidMapOffset = br.ReadUInt32();
        var liquidMapSize = br.ReadUInt32();
        var holesOffset = br.ReadUInt32();
        var holesSize = br.ReadUInt32();

        if (heightMapOffset == 0) return null; // no height data

        // ── Load holes data (for terrain hole detection) ──
        ushort[,]? holes = null;
        if (holesOffset != 0)
        {
            fs.Seek(holesOffset, SeekOrigin.Begin);
            holes = new ushort[16, 16];
            for (int r = 0; r < 16; r++)
                for (int c = 0; c < 16; c++)
                    holes[r, c] = br.ReadUInt16();
        }

        // ── Height header (16 bytes) ──
        fs.Seek(heightMapOffset, SeekOrigin.Begin);

        var heightFourcc = br.ReadUInt32();
        if (heightFourcc != BitConverter.ToUInt32(HEIGHT_MAGIC))
        {
            _logger.LogWarning("Map file {File} has wrong height magic", filename);
            return null;
        }

        var flags = br.ReadUInt32();
        var gridHeight = br.ReadSingle();
        var gridMaxHeight = br.ReadSingle();

        // ── Height data ──
        if ((flags & MAP_HEIGHT_NO_HEIGHT) != 0)
        {
            // Flat tile — every point is at gridHeight
            return new GridTile(gridHeight);
        }

        if ((flags & MAP_HEIGHT_AS_INT16) != 0)
        {
            var v9 = ReadUInt16Array(br, 129 * 129);
            var v8 = ReadUInt16Array(br, 128 * 128);
            float multiplier = (gridMaxHeight - gridHeight) / 65535f;
            return new GridTile(v9, v8, multiplier, gridHeight, holes);
        }

        if ((flags & MAP_HEIGHT_AS_INT8) != 0)
        {
            var v9 = br.ReadBytes(129 * 129);
            var v8 = br.ReadBytes(128 * 128);
            float multiplier = (gridMaxHeight - gridHeight) / 255f;
            return new GridTile(v9, v8, multiplier, gridHeight, holes);
        }

        // Full float
        {
            var v9 = ReadFloatArray(br, 129 * 129);
            var v8 = ReadFloatArray(br, 128 * 128);
            return new GridTile(v9, v8, holes);
        }
    }

    private static ushort[] ReadUInt16Array(BinaryReader br, int count)
    {
        var arr = new ushort[count];
        var bytes = br.ReadBytes(count * 2);
        Buffer.BlockCopy(bytes, 0, arr, 0, bytes.Length);
        return arr;
    }

    private static float[] ReadFloatArray(BinaryReader br, int count)
    {
        var arr = new float[count];
        var bytes = br.ReadBytes(count * 4);
        Buffer.BlockCopy(bytes, 0, arr, 0, bytes.Length);
        return arr;
    }

    // ═══════════════════════════════════════════════════════════════
    //  GridTile — cached height data for one 533-yard tile
    // ═══════════════════════════════════════════════════════════════

    private class GridTile
    {
        private enum StorageType { Flat, Float, UInt16, UInt8 }

        private readonly StorageType _type;
        private readonly float _gridHeight;
        private readonly float _multiplier;

        private readonly float[]? _v9Float;
        private readonly float[]? _v8Float;
        private readonly ushort[]? _v9U16;
        private readonly ushort[]? _v8U16;
        private readonly byte[]? _v9U8;
        private readonly byte[]? _v8U8;
        private readonly ushort[,]? _holes;

        // Hole lookup tables (same as VMaNGOS)
        private static readonly ushort[] HoletabH = { 0x1111, 0x2222, 0x4444, 0x8888 };
        private static readonly ushort[] HoletabV = { 0x000F, 0x00F0, 0x0F00, 0xF000 };

        /// <summary>Flat tile — constant height everywhere.</summary>
        public GridTile(float flatHeight)
        {
            _type = StorageType.Flat;
            _gridHeight = flatHeight;
        }

        /// <summary>Full float V9/V8.</summary>
        public GridTile(float[] v9, float[] v8, ushort[,]? holes)
        {
            _type = StorageType.Float;
            _v9Float = v9;
            _v8Float = v8;
            _holes = holes;
        }

        /// <summary>Compressed uint16 V9/V8.</summary>
        public GridTile(ushort[] v9, ushort[] v8, float multiplier, float gridHeight, ushort[,]? holes)
        {
            _type = StorageType.UInt16;
            _v9U16 = v9;
            _v8U16 = v8;
            _multiplier = multiplier;
            _gridHeight = gridHeight;
            _holes = holes;
        }

        /// <summary>Compressed uint8 V9/V8.</summary>
        public GridTile(byte[] v9, byte[] v8, float multiplier, float gridHeight, ushort[,]? holes)
        {
            _type = StorageType.UInt8;
            _v9U8 = v9;
            _v8U8 = v8;
            _multiplier = multiplier;
            _gridHeight = gridHeight;
            _holes = holes;
        }

        public float? GetHeight(float worldX, float worldY)
        {
            if (_type == StorageType.Flat) return _gridHeight;

            // Transform world coords to grid-local coords (same as VMaNGOS)
            float xf = MAP_RESOLUTION * (32 - worldX / SIZE_OF_GRIDS);
            float yf = MAP_RESOLUTION * (32 - worldY / SIZE_OF_GRIDS);

            int xInt = (int)xf;
            int yInt = (int)yf;
            float x = xf - xInt;
            float y = yf - yInt;
            xInt &= (MAP_RESOLUTION - 1);
            yInt &= (MAP_RESOLUTION - 1);

            // Check for terrain holes
            if (_holes != null && IsHole(xInt, yInt)) return null;

            return _type switch
            {
                StorageType.Float => InterpolateFloat(xInt, yInt, x, y),
                StorageType.UInt16 => InterpolateUInt16(xInt, yInt, x, y),
                StorageType.UInt8 => InterpolateUInt8(xInt, yInt, x, y),
                _ => null
            };
        }

        private bool IsHole(int row, int col)
        {
            if (_holes == null) return false;
            int cellRow = row / 8;
            int cellCol = col / 8;
            int holeRow = row % 8 / 2;
            int holeCol = (col - (cellCol * 8)) / 2;
            ushort hole = _holes[cellRow, cellCol];
            return (hole & HoletabH[holeCol] & HoletabV[holeRow]) != 0;
        }

        // ── Direct port of GridMap::getHeightFromFloat ──
        private float InterpolateFloat(int xInt, int yInt, float x, float y)
        {
            float a, b, c;

            if (x + y < 1)
            {
                if (x > y)
                {
                    // Triangle 1 (h1, h2, h5)
                    float h1 = _v9Float![xInt * 129 + yInt];
                    float h2 = _v9Float[(xInt + 1) * 129 + yInt];
                    float h5 = 2 * _v8Float![xInt * 128 + yInt];
                    a = h2 - h1;
                    b = h5 - h1 - h2;
                    c = h1;
                }
                else
                {
                    // Triangle 2 (h1, h3, h5)
                    float h1 = _v9Float![xInt * 129 + yInt];
                    float h3 = _v9Float[xInt * 129 + yInt + 1];
                    float h5 = 2 * _v8Float![xInt * 128 + yInt];
                    a = h5 - h1 - h3;
                    b = h3 - h1;
                    c = h1;
                }
            }
            else
            {
                if (x > y)
                {
                    // Triangle 3 (h2, h4, h5)
                    float h2 = _v9Float![(xInt + 1) * 129 + yInt];
                    float h4 = _v9Float[(xInt + 1) * 129 + yInt + 1];
                    float h5 = 2 * _v8Float![xInt * 128 + yInt];
                    a = h2 + h4 - h5;
                    b = h4 - h2;
                    c = h5 - h4;
                }
                else
                {
                    // Triangle 4 (h3, h4, h5)
                    float h3 = _v9Float![xInt * 129 + yInt + 1];
                    float h4 = _v9Float[(xInt + 1) * 129 + yInt + 1];
                    float h5 = 2 * _v8Float![xInt * 128 + yInt];
                    a = h4 - h3;
                    b = h3 + h4 - h5;
                    c = h5 - h4;
                }
            }

            return a * x + b * y + c;
        }

        // ── Direct port of GridMap::getHeightFromUint16 ──
        private float InterpolateUInt16(int xInt, int yInt, float x, float y)
        {
            // V9_h1_ptr = &m_uint16_V9[x_int * 128 + x_int + y_int]  i.e. x_int * 129 + y_int
            int h1Idx = xInt * 129 + yInt;

            int a, b, c;

            if (x + y < 1)
            {
                if (x > y)
                {
                    int h1 = _v9U16![h1Idx];
                    int h2 = _v9U16[h1Idx + 129];
                    int h5 = 2 * _v8U16![xInt * 128 + yInt];
                    a = h2 - h1;
                    b = h5 - h1 - h2;
                    c = h1;
                }
                else
                {
                    int h1 = _v9U16![h1Idx];
                    int h3 = _v9U16[h1Idx + 1];
                    int h5 = 2 * _v8U16![xInt * 128 + yInt];
                    a = h5 - h1 - h3;
                    b = h3 - h1;
                    c = h1;
                }
            }
            else
            {
                if (x > y)
                {
                    int h2 = _v9U16![h1Idx + 129];
                    int h4 = _v9U16[h1Idx + 130];
                    int h5 = 2 * _v8U16![xInt * 128 + yInt];
                    a = h2 + h4 - h5;
                    b = h4 - h2;
                    c = h5 - h4;
                }
                else
                {
                    int h3 = _v9U16![h1Idx + 1];
                    int h4 = _v9U16[h1Idx + 130];
                    int h5 = 2 * _v8U16![xInt * 128 + yInt];
                    a = h4 - h3;
                    b = h3 + h4 - h5;
                    c = h5 - h4;
                }
            }

            return (float)((a * x) + (b * y) + c) * _multiplier + _gridHeight;
        }

        // ── Direct port of GridMap::getHeightFromUint8 ──
        private float InterpolateUInt8(int xInt, int yInt, float x, float y)
        {
            int h1Idx = xInt * 129 + yInt;

            int a, b, c;

            if (x + y < 1)
            {
                if (x > y)
                {
                    int h1 = _v9U8![h1Idx];
                    int h2 = _v9U8[h1Idx + 129];
                    int h5 = 2 * _v8U8![xInt * 128 + yInt];
                    a = h2 - h1;
                    b = h5 - h1 - h2;
                    c = h1;
                }
                else
                {
                    int h1 = _v9U8![h1Idx];
                    int h3 = _v9U8[h1Idx + 1];
                    int h5 = 2 * _v8U8![xInt * 128 + yInt];
                    a = h5 - h1 - h3;
                    b = h3 - h1;
                    c = h1;
                }
            }
            else
            {
                if (x > y)
                {
                    int h2 = _v9U8![h1Idx + 129];
                    int h4 = _v9U8[h1Idx + 130];
                    int h5 = 2 * _v8U8![xInt * 128 + yInt];
                    a = h2 + h4 - h5;
                    b = h4 - h2;
                    c = h5 - h4;
                }
                else
                {
                    int h3 = _v9U8![h1Idx + 1];
                    int h4 = _v9U8[h1Idx + 130];
                    int h5 = 2 * _v8U8![xInt * 128 + yInt];
                    a = h4 - h3;
                    b = h3 + h4 - h5;
                    c = h5 - h4;
                }
            }

            return (float)((a * x) + (b * y) + c) * _multiplier + _gridHeight;
        }
    }
}