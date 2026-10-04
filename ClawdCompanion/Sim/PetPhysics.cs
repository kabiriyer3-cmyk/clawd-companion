using System;
using System.Collections.Generic;
using ClawdCompanion.Core;

namespace ClawdCompanion.Sim
{
    public enum PetMode { Grounded, Airborne, Held }

    public enum PhysEventKind { Landed, Bonk, WalkedOff, HitEdge, SupportLost }

    public struct PhysEvent
    {
        public PhysEventKind Kind;
        public double Strength;
        public int Side;
    }

    /// <summary>
    /// Clawd's body + keychain physics in screen DIPs.
    ///  * Held: position-based chain  cursor -> key ring -> jump ring -> eyelet/body  (a real triple pendulum).
    ///  * Airborne: rigid body with gravity, wall bounces and a cat-like righting reflex.
    ///  * Grounded: stands on a surface, walks, squashes; the chain becomes a springy antenna.
    /// Model-space constants (units, y up) come straight from clawd.obj.
    /// </summary>
    public sealed class PetPhysics
    {
        public const double ComY = 0.42;        // body centre of mass
        public const double FeetY = 0.02;       // bottom of the legs
        public const double BodyTopY = 0.80;
        public const double J2Y = 0.857;        // eyelet <-> jump ring contact
        public const double J1Y = 0.945;        // jump ring <-> key ring contact
        public const double GrabY = 1.13;       // inside top of the key ring (where you hook it)
        public const double KeyCenterY = 1.044; // key ring centre
        public const double KeyRadius = 0.13;
        public const double HalfWidth = 0.585;  // claw tip to claw tip / 2

        static readonly double[,] Hull =
        {
            { -0.40, 0.02 }, { 0.40, 0.02 }, { -0.40, 0.80 }, { 0.40, 0.80 },
            { -0.585, 0.44 }, { 0.585, 0.44 }, { -0.585, 0.593 }, { 0.585, 0.593 }
        };

        public double S = 140;                       // DIPs per model unit
        public double K { get { return S / 140.0; } }
        public double Gravity { get { return 3600.0 * K; } }
        public DesktopWorld World;

        public PetMode Mode = PetMode.Airborne;

        // rigid body (C = world position of the centre of mass)
        public V2 C, Vel;
        public double Theta, Omega;                  // clockwise tilt (radians)

        // squash & stretch
        public double Sy = 1, SyVel;
        public double SquashTarget = 1;
        public double PivotY = ComY;
        public double Sx { get { return 1.0 / Math.Sqrt(Sy); } }

        // set by the brain every frame
        public double WalkVel;
        public double ThetaTarget;
        public double Lift;                          // little hop/bob offset (DIPs, up)

        // chain particles: P0 = grab point (top of key ring), P1 = key/jump contact, P2 = jump/eyelet contact
        public V2 P0, P1, P2;
        V2 vel0, vel1, vel2;

        public Surface Support;
        public readonly List<PhysEvent> Events = new List<PhysEvent>();

        V2 grabOffset;
        V2 lastPin;
        bool hasPin;

        double L1 { get { return (GrabY - J1Y) * S; } }
        double L2 { get { return (J1Y - J2Y) * S; } }
        double L3 { get { return (J2Y - ComY) * S; } }

        // ------------------------------------------------------------------ geometry

        public V2 PivotWorld
        {
            get { return C + new V2(0, -(PivotY - ComY) * S).Rotate(Theta) + new V2(0, -Lift); }
        }

        /// <summary>Model-space point on the body (units, y up) to screen DIPs, including squash and tilt.</summary>
        public V2 BodyPoint(double lx, double ly)
        {
            V2 off = new V2(lx * Sx * S, -(ly - PivotY) * Sy * S).Rotate(Theta);
            return PivotWorld + off;
        }

        public V2 RingCenter
        {
            get
            {
                V2 d = (P1 - P0).Normalized();
                if (d.Length < 0.5) d = new V2(0, 1);
                return P0 + d * ((GrabY - KeyCenterY) * S);
            }
        }

        void HullExtents(out double minX, out double maxX, out double minY, out double maxY)
        {
            minX = double.MaxValue; maxX = double.MinValue; minY = double.MaxValue; maxY = double.MinValue;
            for (int i = 0; i < Hull.GetLength(0); i++)
            {
                V2 p = BodyPoint(Hull[i, 0], Hull[i, 1]);
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
        }

        public double FeetScreenY { get { return BodyPoint(0, FeetY).Y; } }

        // ------------------------------------------------------------------ control

        public void SpawnAt(double x, double y)
        {
            C = new V2(x, y);
            Vel = V2.Zero; Theta = 0; Omega = 0; Sy = 1; SyVel = 0; PivotY = ComY; Lift = 0;
            Mode = PetMode.Airborne; Support = null;
            ResetChain();
        }

        public void ResetChain()
        {
            P2 = BodyPoint(0, J2Y); P1 = BodyPoint(0, J1Y); P0 = BodyPoint(0, GrabY);
            vel0 = vel1 = vel2 = V2.Zero;
        }

        public void Grab(V2 cursor)
        {
            if (Mode == PetMode.Held) return;
            if (Mode == PetMode.Grounded) Vel = new V2(Vel.X, 0);
            Mode = PetMode.Held;
            Support = null;
            Lift = 0;
            grabOffset = P0 - cursor;
            lastPin = cursor;
            hasPin = true;
        }

        public void Release()
        {
            if (Mode != PetMode.Held) return;
            Mode = PetMode.Airborne;
            double maxV = 5200 * K;
            if (Vel.Length > maxV) Vel = Vel.Normalized() * maxV;
            Omega = M.Clamp(Omega, -30, 30);
            Theta = M.WrapAngle(Theta);
        }

        public void Jump(double vx, double vy)
        {
            if (Mode != PetMode.Grounded) return;
            Mode = PetMode.Airborne;
            Support = null;
            Vel = new V2(vx, -vy);
        }

        public void DropSupport()
        {
            if (Mode != PetMode.Grounded) return;
            Mode = PetMode.Airborne;
            Support = null;
            Vel = new V2(Vel.X, 0);
            Events.Add(new PhysEvent { Kind = PhysEventKind.SupportLost });
        }

        /// <summary>Follows a moving window (ride along) and re-validates monitor floors. Call once per frame.</summary>
        public void TrackSupport()
        {
            if (Mode != PetMode.Grounded || Support == null || World == null) return;
            if (Support.IsWindow)
            {
                double dx, dy;
                if (!World.TrackWindow(Support, out dx, out dy)) { DropSupport(); return; }
                if (dy > 40 * K) { DropSupport(); return; } // window yanked down: fall with it
                C = new V2(C.X + dx, C.Y);
                V2 shift = new V2(dx, 0);
                P0 = P0 + shift; P1 = P1 + shift; P2 = P2 + shift;
            }
            else
            {
                Surface s = World.FloorAt(C.X, Support.Y - 2);
                if (s == null) { DropSupport(); return; }
                if (s.IsWindow) { Support = s; return; }
                if (Math.Abs(s.Y - Support.Y) > 1)
                {
                    if (s.Y < Support.Y) Support = s; // floor moved up (taskbar grew): step up
                    else { DropSupport(); return; }
                }
                else Support = s;
            }
        }

        // ------------------------------------------------------------------ stepping

        public void Step(double dt, V2 cursor)
        {
            if (!hasPin) { lastPin = cursor; hasPin = true; }
            int n = Math.Max(1, Math.Min(16, (int)Math.Ceiling(dt * 240)));
            double h = dt / n;
            for (int i = 1; i <= n; i++)
            {
                V2 pin = V2.Lerp(lastPin, cursor, (double)i / n);
                switch (Mode)
                {
                    case PetMode.Held:
                        StepHeld(h, pin);
                        break;
                    case PetMode.Airborne:
                        StepAir(h);
                        if (Mode != PetMode.Held) StepChainFree(h);
                        break;
                    default:
                        StepGround(h);
                        StepChainFree(h);
                        break;
                }
                StepSquash(h);
            }
            lastPin = cursor;
        }

        static void SolvePinned(V2 a, ref V2 b, double length)
        {
            V2 d = b - a;
            double len = d.Length;
            if (len < 1e-6) return;
            b = a + d * (length / len);
        }

        static void Solve(ref V2 a, double wa, ref V2 b, double wb, double length)
        {
            V2 d = b - a;
            double len = d.Length;
            if (len < 1e-6) return;
            double diff = (len - length) / len;
            double sum = wa + wb;
            a = a + d * (wa / sum * diff);
            b = b - d * (wb / sum * diff);
        }

        void StepHeld(double h, V2 pin)
        {
            grabOffset = grabOffset * Math.Exp(-14 * h);
            V2 p0n = pin + grabOffset;
            vel0 = (p0n - P0) / h;
            P0 = p0n;

            double g = Gravity;
            double damp = Math.Exp(-0.8 * h);
            vel1 = (vel1 + new V2(0, g * h)) * damp;
            vel2 = (vel2 + new V2(0, g * h)) * damp;
            Vel = (Vel + new V2(0, g * h)) * damp;

            V2 q1 = P1 + vel1 * h, q2 = P2 + vel2 * h, qc = C + Vel * h;
            const double w1 = 20, w2 = 20, wc = 1; // inverse masses: light rings, heavy Clawd
            for (int it = 0; it < 12; it++)
            {
                SolvePinned(P0, ref q1, L1);
                Solve(ref q1, w1, ref q2, w2, L2);
                Solve(ref q2, w2, ref qc, wc, L3);
            }
            vel1 = (q1 - P1) / h; vel2 = (q2 - P2) / h; Vel = (qc - C) / h;
            P1 = q1; P2 = q2; C = qc;

            // the body hangs from the eyelet: its up vector points at P2
            V2 up = (P2 - C).Normalized();
            if (up.Length > 0.5)
            {
                double nt = Math.Atan2(up.X, -up.Y);
                double d = M.WrapAngle(nt - Theta);
                Omega = M.Lerp(Omega, d / h, 0.25);
                Theta += d;
            }
        }

        void StepAir(double h)
        {
            double g = Gravity;
            Vel = new V2(Vel.X, Vel.Y + g * h) * Math.Exp(-0.12 * h);

            double minX, maxX, minY, maxY;
            HullExtents(out minX, out maxX, out minY, out maxY);
            double prevBottom = maxY;

            C = C + Vel * h;

            // righting reflex: always tries to land on its feet
            double e = M.WrapAngle(Theta);
            Omega += (-70 * e - 7 * Omega) * h;
            Theta += Omega * h;

            Collide(prevBottom);
        }

        void Collide(double prevBottom)
        {
            if (World == null) return;
            RectD b = World.Bounds;
            double minX, maxX, minY, maxY;
            HullExtents(out minX, out maxX, out minY, out maxY);

            if (minX < b.Left)
            {
                C = new V2(C.X + (b.Left - minX), C.Y);
                if (Vel.X < 0)
                {
                    Events.Add(new PhysEvent { Kind = PhysEventKind.Bonk, Strength = -Vel.X, Side = -1 });
                    Omega += Vel.X * 0.004;
                    Vel = new V2(-Vel.X * 0.42, Vel.Y);
                }
            }
            else if (maxX > b.Right)
            {
                C = new V2(C.X - (maxX - b.Right), C.Y);
                if (Vel.X > 0)
                {
                    Events.Add(new PhysEvent { Kind = PhysEventKind.Bonk, Strength = Vel.X, Side = 1 });
                    Omega += Vel.X * 0.004;
                    Vel = new V2(-Vel.X * 0.42, Vel.Y);
                }
            }
            if (minY < b.Top)
            {
                C = new V2(C.X, C.Y + (b.Top - minY));
                if (Vel.Y < 0) Vel = new V2(Vel.X, -Vel.Y * 0.3);
            }

            if (Vel.Y >= 0)
            {
                HullExtents(out minX, out maxX, out minY, out maxY);
                Surface s = World.FindLanding(C.X, prevBottom - 2, maxY, C.Y);
                if (s != null)
                {
                    C = new V2(C.X, C.Y - (maxY - s.Y));
                    double impact = Vel.Y;
                    if (Math.Abs(M.WrapAngle(Theta)) < 0.95 || impact < 260 * K)
                    {
                        Mode = PetMode.Grounded;
                        Support = s;
                        PivotY = FeetY; // squash from the feet from now on (keeps them planted)
                        Theta = M.WrapAngle(Theta);
                        Omega *= 0.5;
                        Vel = new V2(Vel.X * 0.5, 0);
                        SyVel -= Math.Min(impact / K, 4500) * 0.0011; // squash on impact
                        Events.Add(new PhysEvent { Kind = PhysEventKind.Landed, Strength = impact });
                    }
                    else
                    {
                        Vel = new V2(Vel.X * 0.7, -impact * 0.35);
                        Omega += (M.WrapAngle(Theta) > 0 ? -1 : 1) * 4;
                        Events.Add(new PhysEvent { Kind = PhysEventKind.Bonk, Strength = impact, Side = 0 });
                    }
                }
            }

            // failsafe: fell out of the world -> drop back in from the top
            if (C.Y > b.Bottom + 3 * S)
            {
                SpawnAt(M.Clamp(C.X, b.Left + S, b.Right - S), b.Top + 0.6 * S);
            }
        }

        void StepGround(double h)
        {
            if (Support == null || World == null) { Mode = PetMode.Airborne; return; }

            Vel = new V2(M.Damp(Vel.X, WalkVel, Math.Abs(WalkVel) > 1 ? 9 : 7, h), 0);
            double nx = C.X + Vel.X * h;

            if (nx < Support.Left || nx > Support.Right)
            {
                int side = nx < Support.Left ? -1 : 1;
                Surface next = World.FloorAt(nx, Support.Y);
                if (next != null && Math.Abs(next.Y - Support.Y) < 3)
                {
                    Support = next;
                }
                else if (next != null && next.Y > Support.Y + 3)
                {
                    C = new V2(nx, C.Y);
                    Mode = PetMode.Airborne;
                    Support = null;
                    Events.Add(new PhysEvent { Kind = PhysEventKind.WalkedOff, Side = side });
                    return;
                }
                else
                {
                    nx = M.Clamp(nx, Support.Left, Support.Right);
                    Vel = new V2(0, 0);
                    Events.Add(new PhysEvent { Kind = PhysEventKind.HitEdge, Side = side });
                }
            }

            RectD b = World.Bounds;
            double half = HalfWidth * S * Sx;
            if (nx - half < b.Left) { nx = b.Left + half; Vel = V2.Zero; Events.Add(new PhysEvent { Kind = PhysEventKind.HitEdge, Side = -1 }); }
            if (nx + half > b.Right) { nx = b.Right - half; Vel = V2.Zero; Events.Add(new PhysEvent { Kind = PhysEventKind.HitEdge, Side = 1 }); }

            C = new V2(nx, Support.Y - (ComY - FeetY) * S);

            Omega += (-260 * (Theta - ThetaTarget) - 18 * Omega) * h;
            Theta += Omega * h;
        }

        /// <summary>Chain when not held: a springy antenna that lags, wobbles and dangles with the body.</summary>
        void StepChainFree(double h)
        {
            V2 np2 = BodyPoint(0, J2Y);
            vel2 = (np2 - P2) / h;
            P2 = np2;

            V2 t1 = BodyPoint(0, J1Y), t0 = BodyPoint(0, GrabY);
            const double k = 900, c = 14;
            V2 grav = new V2(0, Gravity);
            V2 a1 = grav + (t1 - P1) * k - (vel1 - Vel) * c;
            V2 a0 = grav + (t0 - P0) * k - (vel0 - Vel) * c;
            vel1 = vel1 + a1 * h;
            vel0 = vel0 + a0 * h;
            V2 q1 = P1 + vel1 * h, q0 = P0 + vel0 * h;
            for (int it = 0; it < 4; it++)
            {
                SolvePinned(P2, ref q1, L2);
                Solve(ref q1, 1, ref q0, 1, L1);
            }
            vel1 = (q1 - P1) / h; vel0 = (q0 - P0) / h;
            P1 = q1; P0 = q0;
        }

        void StepSquash(double h)
        {
            SyVel += (-380 * (Sy - SquashTarget) - 13 * SyVel) * h;
            Sy += SyVel * h;
            if (Sy < 0.55) { Sy = 0.55; SyVel = 0; }
            if (Sy > 1.6) { Sy = 1.6; SyVel = 0; }
            double pivotTarget = Mode == PetMode.Grounded ? FeetY : (Mode == PetMode.Held ? J2Y : ComY);
            PivotY = M.Damp(PivotY, pivotTarget, 25, h);
        }
    }
}
