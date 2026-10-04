using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using ClawdCompanion.Sim;

namespace ClawdCompanion.Render
{
    /// <summary>A reusable Pre-translate → spin → scale → yaw → roll → post-translate transform chain.</summary>
    sealed class XformChain
    {
        public readonly Transform3DGroup Group = new Transform3DGroup();
        public readonly TranslateTransform3D Pre = new TranslateTransform3D();
        public readonly AxisAngleRotation3D Spin = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0);
        public readonly ScaleTransform3D Scale = new ScaleTransform3D(1, 1, 1);
        public readonly AxisAngleRotation3D Yaw = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
        public readonly AxisAngleRotation3D Roll = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0);
        public readonly TranslateTransform3D Post = new TranslateTransform3D();

        public XformChain()
        {
            Group.Children.Add(Pre);
            Group.Children.Add(new RotateTransform3D(Spin));
            Group.Children.Add(Scale);
            Group.Children.Add(new RotateTransform3D(Yaw));
            Group.Children.Add(new RotateTransform3D(Roll));
            Group.Children.Add(Post);
        }
    }

    sealed class EyeSlot
    {
        public readonly Model3DGroup Group = new Model3DGroup();
        public readonly XformChain X = new XformChain();
        public readonly Dictionary<Expr, Model3D> Models = new Dictionary<Expr, Model3D>();
        public Point3D Center;
        bool hasCurrent;
        Expr current;

        public EyeSlot() { Group.Transform = X.Group; }

        public void Show(Expr e)
        {
            if (hasCurrent && e == current) return;
            hasCurrent = true;
            current = e;
            Model3D m;
            if (!Models.TryGetValue(e, out m)) m = Models[Expr.Open];
            Group.Children.Clear();
            Group.Children.Add(m);
        }
    }

    static class Materials
    {
        public static readonly Material Body = Make(Color.FromRgb(0xD7, 0x77, 0x57), Color.FromRgb(70, 60, 54), 22, null);
        public static readonly Material Eye = Make(Color.FromRgb(0x17, 0x16, 0x15), Color.FromRgb(160, 160, 160), 55, null);
        public static readonly Material Metal = Make(Color.FromRgb(0x9C, 0x9F, 0xA6), Color.FromRgb(255, 255, 255), 70, Color.FromRgb(28, 29, 32));
        public static readonly Material Sparkle = Make(Color.FromRgb(255, 255, 255), Color.FromRgb(0, 0, 0), 1, Color.FromRgb(230, 230, 230));

        static Material Make(Color diffuse, Color specular, double power, Color? emissive)
        {
            var g = new MaterialGroup();
            g.Children.Add(new DiffuseMaterial(new SolidColorBrush(diffuse)));
            if (emissive.HasValue) g.Children.Add(new EmissiveMaterial(new SolidColorBrush(emissive.Value)));
            if (power > 1) g.Children.Add(new SpecularMaterial(new SolidColorBrush(specular), power));
            g.Freeze();
            return g;
        }
    }

    /// <summary>
    /// The 3D Clawd: body (with bendable legs and claws), separate animated eyes, eyelet, jump ring and key ring.
    /// Part names match the Blender export: Clawd_Body, Clawd_Eye.L/R, Clawd_Eyelet, Clawd_JumpRing, Clawd_KeyRing.
    /// </summary>
    public sealed class ClawdRig
    {
        const double HipY = 0.215, LegLiftAmount = 0.07;
        const double ShoulderX = 0.43, ShoulderY = 0.516, ArmSpan = 0.155;
        const double EyeZ = 0.068, EyeDepth = 0.02, StrokeW = 0.03;
        static readonly double[] LegX = { -0.35, -0.15, 0.15, 0.35 };

        public ModelVisual3D Visual { get; } = new ModelVisual3D();

        readonly XformChain body = new XformChain();
        readonly XformChain jump = new XformChain();
        readonly XformChain key = new XformChain();
        readonly EyeSlot[] eyes = { new EyeSlot(), new EyeSlot() }; // 0 = viewer-left, 1 = viewer-right

        readonly MeshGeometry3D bodyMesh;
        readonly Point3D[] basePos;
        readonly Vector3D[] baseNrm;
        // two buffers: we write into the detached one (no change notifications), then swap it in
        readonly Point3DCollection[] posBuf = new Point3DCollection[2];
        readonly Vector3DCollection[] nrmBuf = new Vector3DCollection[2];
        int writeBuf;
        readonly int[] legOf, armSide, deformIdx;
        readonly double[] legW, armW;
        readonly double[] lastSwing = { 9, 9, 9, 9 }, lastLift = { 9, 9, 9, 9 };
        double lastArmL = 9, lastArmR = 9;

        public ClawdRig(Dictionary<string, ObjPart> parts)
        {
            ObjPart pBody = Need(parts, "Clawd_Body");
            ObjPart pEyeL = Need(parts, "Clawd_Eye.L");
            ObjPart pEyeR = Need(parts, "Clawd_Eye.R");
            ObjPart pEyelet = Need(parts, "Clawd_Eyelet");
            ObjPart pJump = Need(parts, "Clawd_JumpRing");
            ObjPart pKey = Need(parts, "Clawd_KeyRing");

            // ---- body + deformation weights
            basePos = pBody.Positions.ToArray();
            baseNrm = pBody.Normals.ToArray();
            int n = basePos.Length;
            legOf = new int[n]; armSide = new int[n]; legW = new double[n]; armW = new double[n];
            var deform = new List<int>();
            for (int i = 0; i < n; i++)
            {
                Point3D p = basePos[i];
                legOf[i] = -1; armSide[i] = -1;
                if (p.Y < HipY - 1e-5)
                {
                    int best = 0;
                    for (int l = 1; l < 4; l++) if (Math.Abs(p.X - LegX[l]) < Math.Abs(p.X - LegX[best])) best = l;
                    legOf[i] = best;
                    legW[i] = Clamp01((HipY - p.Y) / (HipY - 0.02));
                    deform.Add(i);
                }
                else if (Math.Abs(p.X) > ShoulderX + 1e-5)
                {
                    armSide[i] = p.X > 0 ? 1 : 0;
                    armW[i] = Clamp01((Math.Abs(p.X) - ShoulderX) / ArmSpan);
                    deform.Add(i);
                }
            }
            deformIdx = deform.ToArray();

            for (int b = 0; b < 2; b++)
            {
                posBuf[b] = new Point3DCollection(basePos);
                nrmBuf[b] = new Vector3DCollection(baseNrm);
            }
            bodyMesh = new MeshGeometry3D
            {
                Positions = posBuf[1],
                Normals = nrmBuf[1],
                TriangleIndices = new Int32Collection(pBody.Indices)
            };
            writeBuf = 0;

            var bodyGroup = new Model3DGroup { Transform = body.Group };
            bodyGroup.Children.Add(new GeometryModel3D(bodyMesh, Materials.Body) { BackMaterial = Materials.Body });
            bodyGroup.Children.Add(new GeometryModel3D(StaticMesh(pEyelet, 0, 0), Materials.Metal) { BackMaterial = Materials.Metal });

            // ---- eyes (viewer-left is the model's right eye)
            BuildEye(eyes[0], pEyeR, true);
            BuildEye(eyes[1], pEyeL, false);
            bodyGroup.Children.Add(eyes[0].Group);
            bodyGroup.Children.Add(eyes[1].Group);

            // ---- chain
            var jumpGroup = new Model3DGroup { Transform = jump.Group };
            jumpGroup.Children.Add(new GeometryModel3D(StaticMesh(pJump, 0, 0), Materials.Metal) { BackMaterial = Materials.Metal });
            var keyGroup = new Model3DGroup { Transform = key.Group };
            keyGroup.Children.Add(new GeometryModel3D(StaticMesh(pKey, 0, 0), Materials.Metal) { BackMaterial = Materials.Metal });

            var root = new Model3DGroup();
            root.Children.Add(bodyGroup);
            root.Children.Add(jumpGroup);
            root.Children.Add(keyGroup);
            Visual.Content = root;
        }

        static ObjPart Need(Dictionary<string, ObjPart> parts, string name)
        {
            ObjPart p;
            if (!parts.TryGetValue(name, out p) || p.Indices.Count == 0)
                throw new InvalidOperationException("The Clawd model is missing the part '" + name + "'.");
            return p;
        }

        static double Clamp01(double v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }

        static MeshGeometry3D StaticMesh(ObjPart p, double offsetX, double offsetY)
        {
            var pts = new Point3D[p.Positions.Count];
            for (int i = 0; i < pts.Length; i++) pts[i] = new Point3D(p.Positions[i].X - offsetX, p.Positions[i].Y - offsetY, p.Positions[i].Z);
            var m = new MeshGeometry3D
            {
                Positions = new Point3DCollection(pts),
                Normals = new Vector3DCollection(p.Normals),
                TriangleIndices = new Int32Collection(p.Indices)
            };
            m.Freeze();
            return m;
        }

        static Model3D Glyph(MeshBuilder b)
        {
            var g = new GeometryModel3D(b.ToMesh(), Materials.Eye) { BackMaterial = Materials.Eye };
            g.Freeze();
            return g;
        }

        void BuildEye(EyeSlot slot, ObjPart part, bool viewerLeft)
        {
            Rect3D bb = part.Bounds();
            slot.Center = new Point3D(bb.X + bb.SizeX / 2, bb.Y + bb.SizeY / 2, 0);

            // open: the modelled rounded-square eye + a tiny glint
            var open = new Model3DGroup();
            open.Children.Add(new GeometryModel3D(StaticMesh(part, slot.Center.X, slot.Center.Y), Materials.Eye) { BackMaterial = Materials.Eye });
            var glint = new MeshBuilder();
            glint.Box(-0.024, 0.024, 0.0792, 0.018, 0.018, 0.003, 0);
            open.Children.Add(new GeometryModel3D(glint.ToMesh(), Materials.Sparkle));
            open.Freeze();
            slot.Models[Expr.Open] = open;

            // ^  happy
            var happy = new MeshBuilder();
            happy.Stroke(-0.052, -0.024, 0, 0.024, StrokeW, EyeZ, EyeDepth);
            happy.Stroke(0, 0.024, 0.052, -0.024, StrokeW, EyeZ, EyeDepth);
            slot.Models[Expr.Happy] = Glyph(happy);

            // -  closed
            var closed = new MeshBuilder();
            closed.Stroke(-0.05, -0.01, 0.05, -0.01, StrokeW, EyeZ, EyeDepth);
            slot.Models[Expr.Closed] = Glyph(closed);

            // > <  squeezed (points toward the middle of the face)
            var squint = new MeshBuilder();
            double s = viewerLeft ? 1 : -1;
            squint.Stroke(-0.042 * s, 0.036, 0.034 * s, 0, StrokeW, EyeZ, EyeDepth);
            squint.Stroke(-0.042 * s, -0.036, 0.034 * s, 0, StrokeW, EyeZ, EyeDepth);
            slot.Models[Expr.SquintIn] = Glyph(squint);

            // x  knocked out
            var cross = new MeshBuilder();
            cross.Stroke(-0.044, -0.044, 0.044, 0.044, StrokeW, EyeZ, EyeDepth);
            cross.Stroke(-0.044, 0.044, 0.044, -0.044, StrokeW, EyeZ, EyeDepth);
            slot.Models[Expr.Cross] = Glyph(cross);

            // pixel heart
            var heart = new MeshBuilder();
            heart.Pixels(new[]
            {
                ".##.##.",
                "#######",
                "#######",
                ".#####.",
                "..###..",
                "...#..."
            }, 0.019, EyeZ, EyeDepth);
            slot.Models[Expr.Heart] = Glyph(heart);

            slot.Show(Expr.Open);
        }

        // ------------------------------------------------------------------ posing (scene units)

        public void SetBody(Point3D pivotWorld, double pivotLocalY, double sx, double sy, double yawDeg, double rollDeg)
        {
            body.Pre.OffsetY = -pivotLocalY;
            body.Scale.ScaleX = sx;
            body.Scale.ScaleY = sy;
            body.Scale.ScaleZ = sx;
            body.Yaw.Angle = yawDeg;
            body.Roll.Angle = rollDeg;
            body.Post.OffsetX = pivotWorld.X;
            body.Post.OffsetY = pivotWorld.Y;
            body.Post.OffsetZ = 0;
        }

        public void SetJumpRing(Point3D joint, double rollDeg, double twistDeg)
        {
            jump.Pre.OffsetY = -PetPhysics.J1Y;
            jump.Yaw.Angle = twistDeg;
            jump.Roll.Angle = rollDeg;
            jump.Post.OffsetX = joint.X;
            jump.Post.OffsetY = joint.Y;
        }

        public void SetKeyRing(Point3D joint, double rollDeg, double twistDeg)
        {
            key.Pre.OffsetY = -PetPhysics.GrabY;
            key.Yaw.Angle = twistDeg;
            key.Roll.Angle = rollDeg;
            key.Post.OffsetX = joint.X;
            key.Post.OffsetY = joint.Y;
        }

        public void SetFace(PetPose pose)
        {
            for (int i = 0; i < 2; i++)
            {
                EyeSlot e = eyes[i];
                e.Show(pose.Expr);
                e.X.Spin.Angle = i == 0 ? pose.EyeSpinL : pose.EyeSpinR;
                e.X.Scale.ScaleX = pose.EyeScale;
                e.X.Scale.ScaleY = pose.EyeScale * pose.EyeOpen;
                e.X.Post.OffsetX = e.Center.X + pose.LookX * 0.035;
                e.X.Post.OffsetY = e.Center.Y + pose.LookY * 0.022;
            }
        }

        /// <summary>Bends legs (shear + lift) and claws (weighted rotation around the shoulder).</summary>
        public void SetLimbs(double[] swing, double[] lift, double armL, double armR)
        {
            bool changed = Math.Abs(armL - lastArmL) > 1e-4 || Math.Abs(armR - lastArmR) > 1e-4;
            for (int i = 0; i < 4 && !changed; i++)
                changed = Math.Abs(swing[i] - lastSwing[i]) > 1e-4 || Math.Abs(lift[i] - lastLift[i]) > 1e-4;
            if (!changed) return;

            lastArmL = armL; lastArmR = armR;
            var tanSwing = new double[4];
            for (int i = 0; i < 4; i++)
            {
                lastSwing[i] = swing[i];
                lastLift[i] = lift[i];
                tanSwing[i] = Math.Tan(Math.Max(-1.2, Math.Min(1.2, swing[i])));
            }

            Point3DCollection pos = posBuf[writeBuf];
            Vector3DCollection nrm = nrmBuf[writeBuf];
            foreach (int i in deformIdx)
            {
                Point3D b = basePos[i];
                int leg = legOf[i];
                if (leg >= 0)
                {
                    pos[i] = new Point3D(b.X + (HipY - b.Y) * tanSwing[leg], b.Y + lift[leg] * legW[i] * LegLiftAmount, b.Z);
                }
                else
                {
                    int side = armSide[i];
                    double sgn = side == 1 ? 1 : -1;
                    double ang = (side == 1 ? armR : armL) * armW[i] * sgn;
                    double c = Math.Cos(ang), s = Math.Sin(ang);
                    double px = ShoulderX * sgn;
                    double dx = b.X - px, dy = b.Y - ShoulderY;
                    pos[i] = new Point3D(px + dx * c - dy * s, ShoulderY + dx * s + dy * c, b.Z);
                    Vector3D nb = baseNrm[i];
                    nrm[i] = new Vector3D(nb.X * c - nb.Y * s, nb.X * s + nb.Y * c, nb.Z);
                }
            }
            bodyMesh.Positions = pos;
            bodyMesh.Normals = nrm;
            writeBuf ^= 1;
        }

        public static Model3DGroup CreateLights()
        {
            var g = new Model3DGroup();
            g.Children.Add(new AmbientLight(Color.FromRgb(92, 88, 86)));
            g.Children.Add(new DirectionalLight(Color.FromRgb(188, 184, 178), new Vector3D(-0.45, -0.55, -1)));
            g.Children.Add(new DirectionalLight(Color.FromRgb(64, 68, 80), new Vector3D(0.7, 0.35, -0.6)));
            g.Children.Add(new DirectionalLight(Color.FromRgb(40, 36, 34), new Vector3D(0, 0.8, 0.3)));
            return g;
        }
    }
}
