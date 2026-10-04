using System;
using System.Collections.Generic;
using System.Windows.Media.Media3D;

namespace ClawdCompanion.Render
{
    /// <summary>Builds small box-based meshes (Clawd's pixel-y expression glyphs).</summary>
    public sealed class MeshBuilder
    {
        readonly List<Point3D> pos = new List<Point3D>();
        readonly List<Vector3D> nrm = new List<Vector3D>();
        readonly List<int> idx = new List<int>();

        public void Box(double cx, double cy, double cz, double w, double h, double d, double rotDeg)
        {
            double hw = w / 2, hh = h / 2, hd = d / 2;
            double a = rotDeg * Math.PI / 180.0, c = Math.Cos(a), s = Math.Sin(a);

            Point3D P(double x, double y, double z) { return new Point3D(cx + x * c - y * s, cy + x * s + y * c, cz + z); }
            Vector3D N(double x, double y, double z) { return new Vector3D(x * c - y * s, x * s + y * c, z); }

            void Face(Point3D p0, Point3D p1, Point3D p2, Point3D p3, Vector3D n)
            {
                int b = pos.Count;
                pos.Add(p0); pos.Add(p1); pos.Add(p2); pos.Add(p3);
                nrm.Add(n); nrm.Add(n); nrm.Add(n); nrm.Add(n);
                idx.Add(b); idx.Add(b + 1); idx.Add(b + 2);
                idx.Add(b); idx.Add(b + 2); idx.Add(b + 3);
            }

            Face(P(-hw, -hh, hd), P(hw, -hh, hd), P(hw, hh, hd), P(-hw, hh, hd), N(0, 0, 1));      // front
            Face(P(hw, -hh, -hd), P(-hw, -hh, -hd), P(-hw, hh, -hd), P(hw, hh, -hd), N(0, 0, -1)); // back
            Face(P(hw, -hh, hd), P(hw, -hh, -hd), P(hw, hh, -hd), P(hw, hh, hd), N(1, 0, 0));      // right
            Face(P(-hw, -hh, -hd), P(-hw, -hh, hd), P(-hw, hh, hd), P(-hw, hh, -hd), N(-1, 0, 0)); // left
            Face(P(-hw, hh, hd), P(hw, hh, hd), P(hw, hh, -hd), P(-hw, hh, -hd), N(0, 1, 0));      // top
            Face(P(-hw, -hh, -hd), P(hw, -hh, -hd), P(hw, -hh, hd), P(-hw, -hh, hd), N(0, -1, 0)); // bottom
        }

        /// <summary>A thick line segment from (x1,y1) to (x2,y2) with slightly overlapping caps.</summary>
        public void Stroke(double x1, double y1, double x2, double y2, double thickness, double cz, double depth)
        {
            double dx = x2 - x1, dy = y2 - y1;
            double len = Math.Sqrt(dx * dx + dy * dy) + thickness * 0.6;
            double ang = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            Box((x1 + x2) / 2, (y1 + y2) / 2, cz, len, thickness, depth, ang);
        }

        /// <summary>Pixel-art glyph: rows top to bottom, '#' or 'X' = filled cell. Centred on (0,0).</summary>
        public void Pixels(string[] rows, double cell, double cz, double depth)
        {
            int h = rows.Length, w = 0;
            foreach (var r in rows) w = Math.Max(w, r.Length);
            for (int ry = 0; ry < h; ry++)
            {
                string row = rows[ry];
                int x = 0;
                while (x < row.Length)
                {
                    if (row[x] != '#' && row[x] != 'X') { x++; continue; }
                    int start = x;
                    while (x < row.Length && (row[x] == '#' || row[x] == 'X')) x++;
                    int run = x - start;
                    double cx = ((start + run / 2.0) - w / 2.0) * cell;
                    double cy = (h / 2.0 - (ry + 0.5)) * cell;
                    Box(cx, cy, cz, run * cell, cell, depth, 0);
                }
            }
        }

        public MeshGeometry3D ToMesh()
        {
            var m = new MeshGeometry3D
            {
                Positions = new Point3DCollection(pos),
                Normals = new Vector3DCollection(nrm),
                TriangleIndices = new System.Windows.Media.Int32Collection(idx)
            };
            m.Freeze();
            return m;
        }
    }
}
