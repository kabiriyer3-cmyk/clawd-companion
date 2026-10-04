using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Media.Media3D;

namespace ClawdCompanion.Render
{
    public sealed class ObjPart
    {
        public string Name;
        public readonly List<Point3D> Positions = new List<Point3D>();
        public readonly List<Vector3D> Normals = new List<Vector3D>();
        public readonly List<int> Indices = new List<int>();

        public Rect3D Bounds()
        {
            if (Positions.Count == 0) return Rect3D.Empty;
            double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue;
            double x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
            foreach (var p in Positions)
            {
                x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); z0 = Math.Min(z0, p.Z);
                x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); z1 = Math.Max(z1, p.Z);
            }
            return new Rect3D(x0, y0, z0, x1 - x0, y1 - y0, z1 - z0);
        }

        /// <summary>Computes smooth normals for vertices that came without one.</summary>
        public void FixNormals()
        {
            bool missing = false;
            foreach (var n in Normals) if (n.LengthSquared < 1e-12) { missing = true; break; }
            if (!missing) return;
            var acc = new Vector3D[Positions.Count];
            for (int i = 0; i + 2 < Indices.Count; i += 3)
            {
                int a = Indices[i], b = Indices[i + 1], c = Indices[i + 2];
                Vector3D fn = Vector3D.CrossProduct(Positions[b] - Positions[a], Positions[c] - Positions[a]);
                acc[a] += fn; acc[b] += fn; acc[c] += fn;
            }
            for (int i = 0; i < Normals.Count; i++)
            {
                if (Normals[i].LengthSquared < 1e-12)
                {
                    Vector3D n = acc[i];
                    if (n.LengthSquared > 1e-18) n.Normalize(); else n = new Vector3D(0, 0, 1);
                    Normals[i] = n;
                }
            }
        }
    }

    /// <summary>Minimal Wavefront OBJ reader: v / vn / o / g / f (any polygon, fan-triangulated).</summary>
    public static class ObjLoader
    {
        public static Dictionary<string, ObjPart> Load(Stream stream)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            var pos = new List<Point3D>();
            var nrm = new List<Vector3D>();
            var parts = new Dictionary<string, ObjPart>(StringComparer.OrdinalIgnoreCase);
            var maps = new Dictionary<string, Dictionary<long, int>>(StringComparer.OrdinalIgnoreCase);
            ObjPart cur = null;
            Dictionary<long, int> map = null;
            var corners = new List<int>();
            char[] separators = { ' ', '\t' };

            void Select(string name)
            {
                if (!parts.TryGetValue(name, out cur))
                {
                    cur = new ObjPart { Name = name };
                    parts[name] = cur;
                    maps[name] = new Dictionary<long, int>();
                }
                map = maps[name];
            }

            int Corner(string token)
            {
                string[] a = token.Split('/');
                int vi = int.Parse(a[0], ci);
                if (vi < 0) vi = pos.Count + vi + 1;
                int ni = 0;
                if (a.Length > 2 && a[2].Length > 0)
                {
                    ni = int.Parse(a[2], ci);
                    if (ni < 0) ni = nrm.Count + ni + 1;
                }
                long key = ((long)vi << 32) | (uint)ni;
                int idx;
                if (!map.TryGetValue(key, out idx))
                {
                    idx = cur.Positions.Count;
                    cur.Positions.Add(pos[vi - 1]);
                    cur.Normals.Add(ni > 0 && ni <= nrm.Count ? nrm[ni - 1] : new Vector3D(0, 0, 0));
                    map[key] = idx;
                }
                return idx;
            }

            using (var reader = new StreamReader(stream))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length < 2 || line[0] == '#') continue;
                    string[] tk = line.Split(separators, StringSplitOptions.RemoveEmptyEntries);
                    switch (tk[0])
                    {
                        case "v":
                            pos.Add(new Point3D(double.Parse(tk[1], ci), double.Parse(tk[2], ci), double.Parse(tk[3], ci)));
                            break;
                        case "vn":
                            nrm.Add(new Vector3D(double.Parse(tk[1], ci), double.Parse(tk[2], ci), double.Parse(tk[3], ci)));
                            break;
                        case "o":
                        case "g":
                            Select(tk.Length > 1 ? tk[1] : "default");
                            break;
                        case "f":
                            if (cur == null) Select("default");
                            corners.Clear();
                            for (int i = 1; i < tk.Length; i++) corners.Add(Corner(tk[i]));
                            for (int i = 1; i + 1 < corners.Count; i++)
                            {
                                cur.Indices.Add(corners[0]);
                                cur.Indices.Add(corners[i]);
                                cur.Indices.Add(corners[i + 1]);
                            }
                            break;
                    }
                }
            }

            foreach (var p in parts.Values) p.FixNormals();
            return parts;
        }
    }
}
