using Godot;

namespace Machines.Game;

/// <summary>
/// The ground's shape in the remake look. The original's terrain tiles are coarse: some ten thousand triangles over
/// a planet, with edges of 20 m and more, so cliffs stand in wedges and mesas in octagons, and machines stand on a
/// height grid (a height every 4 m) that the tiles follow only roughly. Here the tiles are joined where they meet and
/// their triangles split until no edge is longer than 4 m on steep faces and 5 m on gentle ground (gentle ground that
/// already follows the grid stays as it is). Gentle ground then takes the grid's height, so machines stand on what is
/// drawn; steep faces are rounded off and roughened. The corner colours the tiles shade themselves with are evened out
/// where tiles meet, so their seams do not show. Water, see-through tiles and metal floors keep their shape, and the
/// ground meets them where they are. The normals are worked out again from the new shape, smooth except across edges
/// sharper than 60°, and lean halfway to the original's. The ground is drawn in squares, each simplified with
/// distance. Purely for the eye: the simulation keeps its grid.
/// </summary>
public static class TerrainGeometry
{
    /// <summary>The longest edge left on gentle ground and on steep faces, in metres.</summary>
    private const float GroundSpacing = 5, SteepSpacing = 4;

    /// <summary>The most pieces an edge is split into.</summary>
    private const int MaxSegments = 24;

    private const int MaxPoints = (MaxSegments + 1) * (MaxSegments + 2) / 2;

    /// <summary>A face is steep from this upward component of its normal (26°) on.</summary>
    private const float SteepFace = 0.9f;

    /// <summary>Between these upward components of the normal (23° and 41°) ground turns from gentle to steep.</summary>
    private const float Gentle = 0.92f, Steep = 0.75f;

    /// <summary>
    /// Ground nearer the grid than this already follows it. Farther than <see cref="Reach"/> it is something the grid
    /// does not hold (a bridge, an overhang) and stays where it is.
    /// </summary>
    private const float Tolerance = 0.15f, Reach = 1.5f;

    /// <summary>How often steep faces are smoothed (each a pass out and a pass back, so they do not shrink).</summary>
    private const int Rounding = 16;

    /// <summary>How far steep faces are pushed in and out, in metres, and the size of their bumps.</summary>
    private const float Roughness = 1.5f, BumpSize = 14;

    private const float CreaseAngle = 60;

    /// <summary>How far the new normals lean towards the original's (see <see cref="Refine"/>).</summary>
    private const float OriginalShading = 0.5f;

    /// <summary>The ground is drawn in squares this wide, so only those in view (and in the sun's shadows) are drawn.</summary>
    private const float ChunkSize = 160;

    /// <summary>One surface of the ground: its vertices, what they carry, and its triangles.</summary>
    public sealed record Part(Vector3[] Vertices, Vector3[] Normals, float[]? Tangents, Vector2[] Uvs, Color[]? Colours, int[] Indices);

    private readonly record struct Corner(int Id, Vector2 Uv, Color Colour, Vector3 Normal);

    private readonly record struct Triangle(int Surface, Corner A, Corner B, Corner C);

    /// <summary>The terrain's surfaces reshaped, in the same order.</summary>
    public static Part[] Refine(Mesh terrain, HeightField field)
    {
        var parts = new Part[terrain.GetSurfaceCount()];
        var shaped = new bool[parts.Length];
        var welder = new Welder();
        var triangles = new List<Triangle>();
        var pinned = new HashSet<int>();
        for (var s = 0; s < parts.Length; s++)
        {
            var part = parts[s] = Read(terrain.SurfaceGetArrays(s));
            var material = terrain.SurfaceGetMaterial(s);
            shaped[s] = Shapeable(material);
            // Tiles without corner colours are tinted as a whole.
            var tint = material is StandardMaterial3D standard ? standard.AlbedoColor with { A = 1 } : Colors.White;
            var ids = Array.ConvertAll(part.Vertices, welder.Id);
            var indices = part.Indices;
            for (var t = 0; t + 2 < indices.Length; t += 3)
            {
                var (a, b, c) = (indices[t], indices[t + 1], indices[t + 2]);
                if (!shaped[s])
                    pinned.UnionWith([ids[a], ids[b], ids[c]]);
                else if (ids[a] != ids[b] && ids[b] != ids[c] && ids[c] != ids[a])
                {
                    Corner At(int i) => new(ids[i], part.Uvs[i], part.Colours?[i] ?? tint, part.Normals[i]);
                    triangles.Add(new Triangle(s, At(a), At(b), At(c)));
                }
            }
        }
        var corners = welder.Positions;
        triangles = MendTJunctions(triangles, corners);

        // Edges with nothing reshaped beyond them (the shore, the planet's rim) stay as they are, and so do their ends.
        var uses = new Dictionary<long, int>();
        foreach (var t in triangles)
            foreach (var key in Edges(t))
                uses[key] = uses.GetValueOrDefault(key) + 1;
        var keptEdges = uses.Where(u => u.Value == 1).Select(u => u.Key).ToHashSet();
        foreach (var key in keptEdges)
            pinned.UnionWith([(int)(key >> 32), (int)(key & 0xffffffff)]);

        // How finely each edge is split: those of steep faces as steep on both their sides, gentle ones where the
        // ground along them strays from the grid.
        var steepEdges = new HashSet<long>();
        foreach (var t in triangles)
            if (IsSteep(corners[t.A.Id], corners[t.B.Id], corners[t.C.Id]))
                steepEdges.UnionWith(Edges(t));
        var keys = uses.Keys.ToArray();
        var pieces = new int[keys.Length];
        Parallel.For(0, keys.Length, e =>
        {
            var (p, q) = (corners[(int)(keys[e] >> 32)], corners[(int)(keys[e] & 0xffffffff)]);
            var length = p.DistanceTo(q);
            var samples = Mathf.CeilToInt(length);
            var m = keptEdges.Contains(keys[e]) ? 1
                : steepEdges.Contains(keys[e]) ? Mathf.CeilToInt(length / SteepSpacing)
                : Enumerable.Range(1, Math.Max(samples - 1, 0)).Any(i => Strays(field, p.Lerp(q, (float)i / samples))) ? Mathf.CeilToInt(length / GroundSpacing)
                : 1;
            pieces[e] = Math.Clamp(m, 1, MaxSegments);
        });
        var segments = new Dictionary<long, int>(keys.Length);
        for (var e = 0; e < keys.Length; e++)
            segments[keys[e]] = pieces[e];

        // ... and each triangle: n × n pieces, at least as fine as its edges.
        var count = triangles.Count;
        var splits = new int[count];
        Parallel.For(0, count, t =>
        {
            var tri = triangles[t];
            var (pa, pb, pc) = (corners[tri.A.Id], corners[tri.B.Id], corners[tri.C.Id]);
            var longest = Mathf.Max(pa.DistanceTo(pb), Mathf.Max(pb.DistanceTo(pc), pc.DistanceTo(pa)));
            var inner = 1;
            if (IsSteep(pa, pb, pc))
                inner = Mathf.CeilToInt(longest / SteepSpacing);
            else
            {
                var samples = Mathf.CeilToInt(longest / 2);
                var strays = false;
                for (var j = 1; j < samples && !strays; j++)
                    for (var i = 1; i + j < samples && !strays; i++)
                        strays = Strays(field, pa + (pb - pa) * i / samples + (pc - pa) * j / samples);
                if (strays)
                    inner = Mathf.CeilToInt(longest / GroundSpacing);
            }
            var edges = Math.Max(segments[Key(tri.A.Id, tri.B.Id)], Math.Max(segments[Key(tri.B.Id, tri.C.Id)], segments[Key(tri.C.Id, tri.A.Id)]));
            splits[t] = Math.Clamp(Math.Max(inner, edges), 1, MaxSegments);
        });

        // Where the new points go: first the corners, then those along the edges (shared by the triangles on both
        // sides, however finely each splits), then each triangle's own inside.
        var edgeStart = new Dictionary<long, int>();
        var total = corners.Count;
        foreach (var (key, m) in segments)
        {
            if (m <= 1)
                continue;
            edgeStart[key] = total;
            total += m - 1;
        }
        var innerStart = new int[count];
        var pieceStart = new int[count + 1];
        for (var t = 0; t < count; t++)
        {
            innerStart[t] = total;
            total += (splits[t] - 1) * (splits[t] - 2) / 2;
            pieceStart[t + 1] = pieceStart[t] + splits[t] * splits[t];
        }
        var points = new Vector3[total];
        corners.CopyTo(points);
        foreach (var (key, start) in edgeStart)
        {
            var (low, high, m) = ((int)(key >> 32), (int)(key & 0xffffffff), segments[key]);
            for (var i = 1; i < m; i++)
                points[start + i - 1] = corners[low].Lerp(corners[high], (float)i / m);
        }

        // Each piece's corners: the point, and where it lies in its triangle (towards B and C), or -1 for pieces
        // that fold flat where an edge is split less finely than its triangle.
        var pieceCount = pieceStart[count];
        var piecePoints = new int[pieceCount * 3];
        var pieceU = new float[pieceCount * 3];
        var pieceV = new float[pieceCount * 3];
        Parallel.For(0, count, () => (new int[MaxPoints], new float[MaxPoints], new float[MaxPoints]), (t, _, grid) =>
        {
            var (slot, slotU, slotV) = grid;
            var tri = triangles[t];
            var n = splits[t];
            var (pa, pb, pc) = (corners[tri.A.Id], corners[tri.B.Id], corners[tri.C.Id]);
            var (ab, bc, ca) = (segments[Key(tri.A.Id, tri.B.Id)], segments[Key(tri.B.Id, tri.C.Id)], segments[Key(tri.C.Id, tri.A.Id)]);
            int EdgePoint(int a, int b, int k, int m) =>
                k == 0 ? a : k == m ? b : edgeStart[Key(a, b)] + (a < b ? k : m - k) - 1;

            // Point (i, j) lies i steps towards B and j towards C; row j holds n + 1 - j points.
            int Slot(int i, int j) => j * (n + 1) - j * (j - 1) / 2 + i;
            var inside = innerStart[t];
            for (var j = 0; j <= n; j++)
            {
                for (var i = 0; i + j <= n; i++)
                {
                    int point;
                    float u, v;
                    if (j == 0)
                    {
                        var k = (int)MathF.Round((float)i * ab / n);
                        (point, u, v) = (EdgePoint(tri.A.Id, tri.B.Id, k, ab), (float)k / ab, 0);
                    }
                    else if (i == 0)
                    {
                        var k = (int)MathF.Round((float)j * ca / n);
                        (point, u, v) = (EdgePoint(tri.A.Id, tri.C.Id, k, ca), 0, (float)k / ca);
                    }
                    else if (i + j == n)
                    {
                        var k = (int)MathF.Round((float)j * bc / n);
                        (point, u, v) = (EdgePoint(tri.B.Id, tri.C.Id, k, bc), 1 - (float)k / bc, (float)k / bc);
                    }
                    else
                    {
                        (u, v) = ((float)i / n, (float)j / n);
                        point = inside++;
                        points[point] = pa + (pb - pa) * u + (pc - pa) * v;
                    }
                    (slot[Slot(i, j)], slotU[Slot(i, j)], slotV[Slot(i, j)]) = (point, u, v);
                }
            }
            // Each piece turns the same way round as the triangle it comes from.
            var piece = pieceStart[t];
            void Emit(int p, int q, int r)
            {
                var o = piece++ * 3;
                if (slot[p] == slot[q] || slot[q] == slot[r] || slot[r] == slot[p])
                {
                    piecePoints[o] = -1;
                    return;
                }
                (piecePoints[o], piecePoints[o + 1], piecePoints[o + 2]) = (slot[p], slot[q], slot[r]);
                (pieceU[o], pieceU[o + 1], pieceU[o + 2]) = (slotU[p], slotU[q], slotU[r]);
                (pieceV[o], pieceV[o + 1], pieceV[o + 2]) = (slotV[p], slotV[q], slotV[r]);
            }
            for (var j = 0; j < n; j++)
            {
                for (var i = 0; i + j < n; i++)
                {
                    Emit(Slot(i, j), Slot(i + 1, j), Slot(i, j + 1));
                    if (i + j < n - 1)
                        Emit(Slot(i + 1, j), Slot(i + 1, j + 1), Slot(i, j + 1));
                }
            }
            return grid;
        }, _ => { });

        // The pieces that did not fold flat, and the triangle each comes from.
        var keptPieces = 0;
        for (var o = 0; o < pieceCount; o++)
            if (piecePoints[o * 3] >= 0)
                keptPieces++;
        var dense = new int[keptPieces * 3];
        var denseU = new float[keptPieces * 3];
        var denseV = new float[keptPieces * 3];
        var from = new int[keptPieces];
        for (int t = 0, k = 0; t < count; t++)
        {
            for (var o = pieceStart[t]; o < pieceStart[t + 1]; o++)
            {
                if (piecePoints[o * 3] < 0)
                    continue;
                Array.Copy(piecePoints, o * 3, dense, k * 3, 3);
                Array.Copy(pieceU, o * 3, denseU, k * 3, 3);
                Array.Copy(pieceV, o * 3, denseV, k * 3, 3);
                from[k++] = t;
            }
        }

        var fixedPoint = new bool[points.Length];
        foreach (var id in pinned)
            fixedPoint[id] = true;
        var normals = Reshape(points, dense, fixedPoint, field);

        // What each corner carries, from its triangle; the tiles' corner colours (or tints) and their normals evened
        // out where tiles meet.
        var cornerUv = new Vector2[dense.Length];
        var cornerColour = new Color[dense.Length];
        var cornerShading = new Vector3[dense.Length];
        var surfaceOf = new int[from.Length];
        Parallel.For(0, from.Length, k =>
        {
            var tri = triangles[from[k]];
            surfaceOf[k] = tri.Surface;
            for (var o = k * 3; o < k * 3 + 3; o++)
            {
                var (u, v) = (denseU[o], denseV[o]);
                cornerUv[o] = tri.A.Uv * (1 - u - v) + tri.B.Uv * u + tri.C.Uv * v;
                cornerColour[o] = tri.A.Colour * (1 - u - v) + tri.B.Colour * u + tri.C.Colour * v;
                cornerShading[o] = tri.A.Normal * (1 - u - v) + tri.B.Normal * u + tri.C.Normal * v;
            }
        });
        var colourSum = new Color[points.Length];
        var colourCount = new int[points.Length];
        var shadingSum = new Vector3[points.Length];
        for (var o = 0; o < dense.Length; o++)
        {
            colourSum[dense[o]] += cornerColour[o];
            colourCount[dense[o]]++;
            shadingSum[dense[o]] += cornerShading[o];
        }
        // The new shape's normals lean towards the original's, which the tiles were drawn to be lit by: faces turned
        // from the sun keep some light, as they had.
        Parallel.For(0, dense.Length, o =>
        {
            var original = shadingSum[dense[o]].Normalized();
            normals[o] = (normals[o] * (1 - OriginalShading) + original * OriginalShading).Normalized();
        });

        // The surfaces' new vertices: one for each point, unless the corners there differ in what they carry
        // (another tile's picture, or either side of a crease).
        var cornersOf = new int[parts.Length];
        foreach (var s in surfaceOf)
            cornersOf[s] += 3;
        var outputs = cornersOf.Select(c => c > 0 ? new Output(c) : null).ToArray();
        var first = new int[points.Length];
        Array.Fill(first, -1);
        var (next, nodeSurface, nodeVertex) = (new int[dense.Length], new int[dense.Length], new int[dense.Length]);
        var nodes = 0;
        for (var o = 0; o < dense.Length; o++)
        {
            var s = surfaceOf[o / 3];
            var output = outputs[s]!;
            var p = dense[o];
            var colour = colourSum[p] / colourCount[p];
            var vertex = -1;
            for (var node = first[p]; node >= 0 && vertex < 0; node = next[node])
                if (nodeSurface[node] == s && output.Matches(nodeVertex[node], normals[o], cornerUv[o], colour))
                    vertex = nodeVertex[node];
            if (vertex < 0)
            {
                vertex = output.Add(points[p], normals[o], cornerUv[o], colour);
                (next[nodes], nodeSurface[nodes], nodeVertex[nodes]) = (first[p], s, vertex);
                first[p] = nodes++;
            }
            output.Index(vertex);
        }
        for (var s = 0; s < parts.Length; s++)
        {
            if (outputs[s] is not { } output)
                continue;
            parts[s] = output.ToPart();
            // A tile that had no corner colours wears them now, in place of its tint.
            if (terrain.SurfaceGetMaterial(s) is StandardMaterial3D { VertexColorUseAsAlbedo: false } plain)
            {
                var coloured = (StandardMaterial3D)plain.Duplicate();
                coloured.AlbedoColor = Colors.White with { A = plain.AlbedoColor.A };
                coloured.VertexColorUseAsAlbedo = true;
                coloured.VertexColorIsSrgb = true;
                coloured.SetMeta("texture", plain.GetMeta("texture", ""));
                terrain.SurfaceSetMaterial(s, coloured);
            }
        }
        return parts;
    }

    /// <summary>
    /// The ground cut into squares (see <see cref="ChunkSize"/>), each with a surface for each of the ground's that
    /// has triangles in it, wearing <paramref name="material"/> of that surface.
    /// </summary>
    public static IEnumerable<ArrayMesh> Chunks(Part[] parts, Func<int, Material> material)
    {
        var chunks = new Dictionary<Vector2I, ImporterMesh>();
        for (var s = 0; s < parts.Length; s++)
        {
            var part = parts[s];
            var (vertices, indices) = (part.Vertices, part.Indices);
            var squares = new Dictionary<Vector2I, List<int>>();
            for (var t = 0; t + 2 < indices.Length; t += 3)
            {
                var middle = (vertices[indices[t]] + vertices[indices[t + 1]] + vertices[indices[t + 2]]) / 3;
                var square = new Vector2I(Mathf.FloorToInt(middle.X / ChunkSize), Mathf.FloorToInt(middle.Z / ChunkSize));
                if (!squares.TryGetValue(square, out var list))
                    squares[square] = list = [];
                list.Add(t);
            }
            var map = new int[vertices.Length];
            Array.Fill(map, -1);
            foreach (var (square, list) in squares)
            {
                var order = new List<int>();
                var pieceIndices = new int[list.Count * 3];
                for (var i = 0; i < list.Count; i++)
                {
                    for (var c = 0; c < 3; c++)
                    {
                        var vertex = indices[list[i] + c];
                        if (map[vertex] < 0)
                        {
                            map[vertex] = order.Count;
                            order.Add(vertex);
                        }
                        pieceIndices[i * 3 + c] = map[vertex];
                    }
                }
                T[] Pick<T>(T[] all, int stride = 1)
                {
                    var picked = new T[order.Count * stride];
                    for (var i = 0; i < order.Count; i++)
                        Array.Copy(all, order[i] * stride, picked, i * stride, stride);
                    return picked;
                }
                var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = Pick(vertices);
                arrays[(int)Mesh.ArrayType.Normal] = Pick(part.Normals);
                if (part.Tangents is not null)
                    arrays[(int)Mesh.ArrayType.Tangent] = Pick(part.Tangents, 4);
                arrays[(int)Mesh.ArrayType.TexUV] = Pick(part.Uvs);
                if (part.Colours is not null)
                    arrays[(int)Mesh.ArrayType.Color] = Pick(part.Colours);
                arrays[(int)Mesh.ArrayType.Index] = pieceIndices;
                foreach (var vertex in order)
                    map[vertex] = -1;

                // Tiles with the HD textures' normal maps (when the ground detail is missing) need their tangents.
                var look = material(s);
                if (look is StandardMaterial3D { NormalEnabled: true } && part.Tangents is null)
                {
                    var tool = new SurfaceTool();
                    tool.CreateFromArrays(arrays);
                    tool.GenerateTangents();
                    arrays = tool.CommitToArrays();
                }
                if (!chunks.TryGetValue(square, out var mesh))
                    chunks[square] = mesh = new ImporterMesh();
                mesh.AddSurface(Mesh.PrimitiveType.Triangles, arrays, material: look);
            }
        }
        // Each square simplified for the distance, side by side (Godot picks the simplest that looks the same).
        var meshes = chunks.Values.ToArray();
        Parallel.ForEach(meshes, mesh => mesh.GenerateLods(CreaseAngle, 25, []));
        return meshes.Select(m => m.GetMesh());
    }

    private static Part Read(Godot.Collections.Array arrays)
    {
        Variant Get(Mesh.ArrayType type) => arrays[(int)type];
        bool Has(Mesh.ArrayType type) => Get(type).VariantType != Variant.Type.Nil;
        return new Part(
            Get(Mesh.ArrayType.Vertex).AsVector3Array(),
            Get(Mesh.ArrayType.Normal).AsVector3Array(),
            Has(Mesh.ArrayType.Tangent) ? Get(Mesh.ArrayType.Tangent).AsFloat32Array() : null,
            Get(Mesh.ArrayType.TexUV).AsVector2Array(),
            Has(Mesh.ArrayType.Color) ? Get(Mesh.ArrayType.Color).AsColorArray() : null,
            Get(Mesh.ArrayType.Index).AsInt32Array());
    }

    /// <summary>
    /// Moves the split ground into its new shape and returns the normal of each triangle's corners: gentle ground to
    /// the grid's height, steep faces smoothed, then pushed in and out along their normals. Fixed points stay where
    /// they are.
    /// </summary>
    private static Vector3[] Reshape(Vector3[] points, int[] triangles, bool[] fixedPoint, HeightField field)
    {
        // Which triangles meet at each point.
        var start = new int[points.Length + 1];
        foreach (var p in triangles)
            start[p + 1]++;
        for (var p = 0; p < points.Length; p++)
            start[p + 1] += start[p];
        var around = new int[triangles.Length];
        var fill = (int[])start.Clone();
        for (var o = 0; o < triangles.Length; o++)
            around[fill[triangles[o]]++] = o / 3;

        // How steep the ground is at each point, from the faces around it before anything moves.
        var normals = PointNormals(points, triangles, start, around);
        var steep = new float[points.Length];
        for (var p = 0; p < points.Length; p++)
            steep[p] = fixedPoint[p] ? 0 : Smooth(Gentle, Steep, normals[p].Y);

        // Gentle ground on the grid.
        for (var p = 0; p < points.Length; p++)
        {
            if (fixedPoint[p] || steep[p] >= 1)
                continue;
            var off = field.At(points[p].X, points[p].Z) - points[p].Y;
            points[p].Y += off * (1 - steep[p]) * Smooth(Reach, Reach / 2, Mathf.Abs(off));
        }

        // Steep faces smoothed: each point moves towards the middle of its neighbours, then a little back.
        var moving = Enumerable.Range(0, points.Length).Where(p => steep[p] > 0).ToArray();
        var neighbours = new int[moving.Length][];
        Parallel.For(0, moving.Length, m =>
        {
            var p = moving[m];
            Span<int> near = stackalloc int[2 * (start[p + 1] - start[p])];
            var count = 0;
            for (var a = start[p]; a < start[p + 1]; a++)
                for (var c = around[a] * 3; c < around[a] * 3 + 3; c++)
                    if (triangles[c] != p && !near[..count].Contains(triangles[c]))
                        near[count++] = triangles[c];
            neighbours[m] = near[..count].ToArray();
        });
        var (current, spare) = (points, (Vector3[])points.Clone());
        for (var pass = 0; pass < Rounding * 2; pass++)
        {
            var factor = pass % 2 == 0 ? 0.5f : -0.53f;
            var (from, to) = (current, spare);
            Parallel.For(0, moving.Length, m =>
            {
                var p = moving[m];
                var sum = Vector3.Zero;
                foreach (var q in neighbours[m])
                    sum += from[q];
                to[p] = neighbours[m].Length == 0 ? from[p] : from[p] + (sum / neighbours[m].Length - from[p]) * (factor * steep[p]);
            });
            (current, spare) = (to, from);
        }
        if (current != points)
            Array.Copy(current, points, points.Length);

        // ... and roughened, finer up and down than along, like the layers of rock.
        normals = PointNormals(points, triangles, start, around);
        Parallel.For(0, points.Length, p =>
        {
            if (steep[p] > 0)
            {
                var at = points[p] / BumpSize;
                points[p] += normals[p] * (steep[p] * Roughness * Bumps.At(at.X, at.Y * 1.8f, at.Z));
            }
        });

        // The corners' normals: smooth over the faces around them that turn less than the crease angle from theirs.
        normals = PointNormals(points, triangles, start, around);
        var faces = new Vector3[triangles.Length / 3];
        var directions = new Vector3[faces.Length];
        Parallel.For(0, faces.Length, t =>
        {
            faces[t] = FaceNormal(points[triangles[t * 3]], points[triangles[t * 3 + 1]], points[triangles[t * 3 + 2]]);
            directions[t] = faces[t].Normalized();
        });
        var crease = Mathf.Cos(Mathf.DegToRad(CreaseAngle));
        var cornerNormals = new Vector3[triangles.Length];
        Parallel.For(0, faces.Length, t =>
        {
            for (var c = 0; c < 3; c++)
            {
                var p = triangles[t * 3 + c];
                var sum = Vector3.Zero;
                var sharp = false;
                for (var a = start[p]; a < start[p + 1]; a++)
                {
                    if (directions[around[a]].Dot(directions[t]) >= crease)
                        sum += faces[around[a]];
                    else
                        sharp = true;
                }
                // Where nothing is sharp every corner at the point gets the same normal, so they share a vertex.
                cornerNormals[t * 3 + c] = !sharp || sum.LengthSquared() <= 1e-12f ? normals[p] : sum.Normalized();
            }
        });
        return cornerNormals;
    }

    /// <summary>Each point's normal: the faces around it, by their size.</summary>
    private static Vector3[] PointNormals(Vector3[] points, int[] triangles, int[] start, int[] around)
    {
        var normals = new Vector3[points.Length];
        Parallel.For(0, points.Length, p =>
        {
            var sum = Vector3.Zero;
            for (var a = start[p]; a < start[p + 1]; a++)
            {
                var t = around[a] * 3;
                sum += FaceNormal(points[triangles[t]], points[triangles[t + 1]], points[triangles[t + 2]]);
            }
            normals[p] = sum.LengthSquared() > 1e-12f ? sum.Normalized() : Vector3.Up;
        });
        return normals;
    }

    /// <summary>
    /// A triangle's normal, as long as twice its area, on the side the original's normals face: Godot's front
    /// faces wind clockwise, and the parts are written so (see ClassicAssets.MeshFromParts).
    /// </summary>
    private static Vector3 FaceNormal(Vector3 a, Vector3 b, Vector3 c) => (c - a).Cross(b - a);

    private static bool IsSteep(Vector3 a, Vector3 b, Vector3 c) => Mathf.Abs(FaceNormal(a, b, c).Normalized().Y) < SteepFace;

    /// <summary>Whether the ground at <paramref name="p"/> is off the grid, but not so far that it is something else.</summary>
    private static bool Strays(HeightField field, Vector3 p)
    {
        var off = Mathf.Abs(field.At(p.X, p.Z) - p.Y);
        return off is > Tolerance and < Reach;
    }

    /// <summary>
    /// Where a tile's edge runs past a corner of its neighbour (a T-junction), the edge is split at that corner, so
    /// that neighbours share all their corners and nothing can open between them once the ground moves.
    /// </summary>
    private static List<Triangle> MendTJunctions(List<Triangle> triangles, List<Vector3> corners)
    {
        var uses = new Dictionary<long, int>();
        foreach (var t in triangles)
            foreach (var key in Edges(t))
                uses[key] = uses.GetValueOrDefault(key) + 1;
        // The corners by 4 m squares of ground, to find those lying on an edge.
        const float cell = 4, near = 0.02f;
        var cells = new Dictionary<(int, int), List<int>>();
        for (var i = 0; i < corners.Count; i++)
        {
            var at = ((int)MathF.Floor(corners[i].X / cell), (int)MathF.Floor(corners[i].Z / cell));
            if (!cells.TryGetValue(at, out var list))
                cells[at] = list = [];
            list.Add(i);
        }
        var splits = new Dictionary<long, List<(float Along, int Id)>>();
        foreach (var (key, used) in uses)
        {
            if (used != 1)
                continue;
            var (a, b) = ((int)(key >> 32), (int)(key & 0xffffffff));
            var (p, q) = (corners[a], corners[b]);
            var d = q - p;
            List<(float, int)>? on = null;
            for (var x = (int)MathF.Floor((Mathf.Min(p.X, q.X) - near) / cell); x <= (int)MathF.Floor((Mathf.Max(p.X, q.X) + near) / cell); x++)
            {
                for (var z = (int)MathF.Floor((Mathf.Min(p.Z, q.Z) - near) / cell); z <= (int)MathF.Floor((Mathf.Max(p.Z, q.Z) + near) / cell); z++)
                {
                    if (!cells.TryGetValue((x, z), out var list))
                        continue;
                    foreach (var id in list)
                    {
                        var along = (corners[id] - p).Dot(d) / d.LengthSquared();
                        if (id != a && id != b && along is > 0.001f and < 0.999f && (p + d * along).DistanceSquaredTo(corners[id]) < near * near)
                            (on ??= []).Add((along, id));
                    }
                }
            }
            if (on is not null)
            {
                on.Sort();
                splits[key] = on;
            }
        }
        if (splits.Count == 0)
            return triangles;

        var mended = new List<Triangle>(triangles.Count + splits.Count * 2);
        var pending = new Stack<Triangle>(triangles);
        while (pending.TryPop(out var t))
        {
            // Turned round so that the edge to split runs from A to B; the order keeps its way round.
            Triangle[] turns = [t, new(t.Surface, t.B, t.C, t.A), new(t.Surface, t.C, t.A, t.B)];
            var turn = Array.FindIndex(turns, r => splits.ContainsKey(Key(r.A.Id, r.B.Id)));
            if (turn < 0)
            {
                mended.Add(t);
                continue;
            }
            var r = turns[turn];
            var points = splits[Key(r.A.Id, r.B.Id)];
            var forward = r.A.Id < r.B.Id;
            var previous = r.A;
            foreach (var (along, id) in forward ? points : Enumerable.Reverse(points))
            {
                if (id == r.C.Id)
                    continue;
                var f = forward ? along : 1 - along;
                var next = new Corner(id, r.A.Uv.Lerp(r.B.Uv, f), r.A.Colour.Lerp(r.B.Colour, f), r.A.Normal.Lerp(r.B.Normal, f).Normalized());
                pending.Push(new Triangle(r.Surface, previous, next, r.C));
                previous = next;
            }
            pending.Push(new Triangle(r.Surface, previous, r.B, r.C));
        }
        return mended;
    }

    /// <summary>Whether a terrain surface is reshaped: opaque ground, not metal floors.</summary>
    private static bool Shapeable(Material? material) => TerrainDetail.LayerOf(material) is { } layer && layer != "metal";

    private static long[] Edges(Triangle t) => [Key(t.A.Id, t.B.Id), Key(t.B.Id, t.C.Id), Key(t.C.Id, t.A.Id)];

    private static long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    /// <summary>0 at <paramref name="from"/>, 1 at <paramref name="to"/> (either way round), easing in and out.</summary>
    private static float Smooth(float from, float to, float x)
    {
        var t = Mathf.Clamp((x - from) / (to - from), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>The reshaped triangles of one surface, with a vertex for each different corner.</summary>
    private sealed class Output(int corners)
    {
        private readonly Vector3[] _positions = new Vector3[corners];
        private readonly Vector3[] _normals = new Vector3[corners];
        private readonly Vector2[] _uvs = new Vector2[corners];
        private readonly Color[] _colours = new Color[corners];
        private readonly int[] _indices = new int[corners];
        private int _vertices, _corners;

        public bool Matches(int vertex, Vector3 normal, Vector2 uv, Color colour) =>
            _normals[vertex] == normal && _uvs[vertex] == uv && _colours[vertex] == colour;

        public int Add(Vector3 position, Vector3 normal, Vector2 uv, Color colour)
        {
            (_positions[_vertices], _normals[_vertices], _uvs[_vertices], _colours[_vertices]) = (position, normal, uv, colour);
            return _vertices++;
        }

        public void Index(int vertex) => _indices[_corners++] = vertex;

        public Part ToPart() => new(_positions[.._vertices], _normals[.._vertices], null, _uvs[.._vertices], _colours[.._vertices], _indices);
    }

    /// <summary>Gives points within a centimetre of each other the same number.</summary>
    private sealed class Welder
    {
        private const float Cell = 0.05f, Near = 0.01f;
        private readonly Dictionary<(int, int, int), List<int>> _cells = [];
        public List<Vector3> Positions { get; } = [];

        public int Id(Vector3 p)
        {
            var (x, y, z) = ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell), (int)MathF.Floor(p.Z / Cell));
            for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                    for (var dz = -1; dz <= 1; dz++)
                        if (_cells.TryGetValue((x + dx, y + dy, z + dz), out var near))
                            foreach (var id in near)
                                if (Positions[id].DistanceSquaredTo(p) < Near * Near)
                                    return id;
            if (!_cells.TryGetValue((x, y, z), out var list))
                _cells[(x, y, z)] = list = [];
            list.Add(Positions.Count);
            Positions.Add(p);
            return Positions.Count - 1;
        }
    }

    /// <summary>Smooth random bumps (Perlin's gradient noise in three octaves), about -1 to 1, the same every time.</summary>
    private static class Bumps
    {
        private static readonly int[] Permutation = Shuffled();

        private static int[] Shuffled()
        {
            var random = new Random(5);
            var order = Enumerable.Range(0, 256).OrderBy(_ => random.Next()).ToArray();
            return [.. order, .. order];
        }

        public static float At(float x, float y, float z) =>
            (Noise(x, y, z) + 0.5f * Noise(x * 2, y * 2, z * 2) + 0.25f * Noise(x * 4, y * 4, z * 4)) / 1.75f;

        private static float Noise(float x, float y, float z)
        {
            var p = Permutation;
            var (fx, fy, fz) = (MathF.Floor(x), MathF.Floor(y), MathF.Floor(z));
            var (ix, iy, iz) = ((int)fx & 255, (int)fy & 255, (int)fz & 255);
            (x, y, z) = (x - fx, y - fy, z - fz);
            var (u, v, w) = (Fade(x), Fade(y), Fade(z));
            var (a, b) = (p[ix] + iy, p[ix + 1] + iy);
            var (aa, ab, ba, bb) = (p[a] + iz, p[a + 1] + iz, p[b] + iz, p[b + 1] + iz);
            return Mix(w,
                Mix(v, Mix(u, Grad(p[aa], x, y, z), Grad(p[ba], x - 1, y, z)), Mix(u, Grad(p[ab], x, y - 1, z), Grad(p[bb], x - 1, y - 1, z))),
                Mix(v, Mix(u, Grad(p[aa + 1], x, y, z - 1), Grad(p[ba + 1], x - 1, y, z - 1)),
                    Mix(u, Grad(p[ab + 1], x, y - 1, z - 1), Grad(p[bb + 1], x - 1, y - 1, z - 1))));
        }

        private static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);

        private static float Mix(float t, float a, float b) => a + t * (b - a);

        private static float Grad(int hash, float x, float y, float z)
        {
            var h = hash & 15;
            var u = h < 8 ? x : y;
            var v = h < 4 ? y : h is 12 or 14 ? x : z;
            return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
        }
    }
}
