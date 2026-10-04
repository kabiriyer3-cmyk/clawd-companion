using System;
using System.Collections.Generic;
using System.Windows.Media;
using ClawdCompanion.Core;

namespace ClawdCompanion.Sim
{
    public enum Expr { Open, Happy, Closed, SquintIn, Cross, Heart }

    public enum FxKind { Z, Heart, Bang, Question, Sparkle, Note }

    public struct FxSpawn
    {
        public FxKind Kind;
        public V2 Pos;
        public V2 Vel;
    }

    public struct CursorInfo
    {
        public V2 Pos;
        public double Speed;
        public double Dist;        // to Clawd's body centre
        public double StillTime;   // seconds since the cursor last moved
        public bool OverBody;
    }

    /// <summary>Everything the renderer needs besides the physics state.</summary>
    public sealed class PetPose
    {
        public Expr Expr = Expr.Open;
        public double EyeOpen = 1;
        public double EyeScale = 1;
        public double LookX, LookY;           // -1..1
        public double EyeSpinL, EyeSpinR;     // degrees
        public double YawDeg;
        public double KeyTwistDeg, JumpTwistDeg;
        public readonly double[] LegSwing = new double[4];
        public readonly double[] LegLift = new double[4];
        public double ArmL, ArmR;             // radians, positive = raise
    }

    enum Act { Idle, Walk, LookAround, Hop, Stretch, Dance, Twirl, Wave, TapFoot, Sleep, Dizzy, Happy, Ouch, Curious, Startle, Giggle, Annoyed }

    enum AirReason { Fall, Hop, Startle, Thrown, Annoyed }

    /// <summary>
    /// Clawd's personality: picks behaviours, reacts to the cursor, pokes, pets, throws and falls,
    /// and turns all of that into face, limb and body animation targets.
    /// </summary>
    public sealed class PetBrain
    {
        readonly PetPhysics ph;
        readonly Settings settings;
        readonly DesktopWorld world;
        readonly Random rng = new Random();

        public readonly PetPose Pose = new PetPose();
        public readonly List<FxSpawn> Fx = new List<FxSpawn>();

        public string Status { get; private set; } = "Vibing";
        public Color StatusColor { get; private set; } = Palette.Blue;
        public string Speech { get; private set; }
        public bool IsSleeping { get { return act == Act.Sleep; } }

        double t;
        CursorInfo cur;
        PetMode lastMode = PetMode.Airborne;

        // behaviour state
        Act act = Act.Idle;
        double actT, actDur = -1, actT2;
        bool actFlag;
        AirReason airReason = AirReason.Fall;
        double airT, heldT;
        bool greetOnLand, pendingNap, saidWhee, saidWhoa;
        double speechUntil;

        // walking
        double walkTarget, walkSpeedMul = 1, gaitPhase;
        int walkDir = 1;
        double hopPower = 1;
        int waveSide = 1;
        Expr ouchExpr = Expr.Cross;

        // meters & timers
        double lastInteraction, sleepAfter = 110, lastStartle = -10;
        double petMeter, pokes, dizzy, happyCool;
        int sleepPhase;
        double sleepT, nextZ, nextHeart, nextNote, nextHeldLine;
        bool handledEdge;

        // face
        Expr shownExpr = Expr.Open;
        double swapT = -1;
        double blinkT = -1, nextBlink = 2;
        bool inDoubleBlink;
        Expr faceOverride = Expr.Open;
        double faceOverrideUntil = -1, faceOverrideScale = 1;
        V2 look, wanderGaze;
        double nextGaze;
        double eyeOpen = 1, eyeScale = 1, spinL, spinR, spinVel;

        // body animation
        double yaw, yawVel, twirl;
        double keyTwist, keyTwistV, jumpTwist, jumpTwistV;
        double accX, lastVelX;
        readonly double[] legSwing = new double[4], legLift = new double[4];
        double armL, armR;

        // per-frame targets
        Expr gExpr;
        double gEyeOpen, gEyeScale, gYaw, gArmL, gArmR, gSpinVel;
        V2 gLook;
        readonly double[] gLegSwing = new double[4], gLegLift = new double[4];

        static readonly string[] Greetings = { "hi!", "hey there", "hello!", "o/", "sup", "hiii" };
        static readonly string[] GrabLines = { "whoa!", "up we go!", "hey!", "eep!" };
        static readonly string[] PokeLines = { "hehe", "boop!", "tickles", "hey :)" };
        static readonly string[] AnnoyedLines = { "hey!", "rude.", "stop iiit", "ok ok!" };
        static readonly string[] WakeLines = { "huh?", "i'm up!", "!", "wasn't sleeping" };
        static readonly string[] HeldLines = { "nice view", "hi up there", "...", "where we going?" };

        public PetBrain(PetPhysics physics, Settings settings, DesktopWorld world)
        {
            ph = physics;
            this.settings = settings;
            this.world = world;
            sleepAfter = Rnd(90, 150);
        }

        double Rnd(double a, double b) { return a + rng.NextDouble() * (b - a); }
        string Pick(string[] options) { return options[rng.Next(options.Length)]; }

        void Say(string text, double duration = 1.9)
        {
            if (string.IsNullOrEmpty(text)) return;
            Speech = text;
            speechUntil = t + duration;
        }

        void SetStatus(string text, Color color) { Status = text; StatusColor = color; }

        void Face(Expr e, double duration, double scale = 1)
        {
            faceOverride = e;
            faceOverrideUntil = t + duration;
            faceOverrideScale = scale;
        }

        void Spawn(FxKind kind, V2 pos, V2 vel) { Fx.Add(new FxSpawn { Kind = kind, Pos = pos, Vel = vel }); }

        V2 HeadTop(double side = 0.25) { return ph.BodyPoint(side, PetPhysics.BodyTopY + 0.12); }

        // ================================================================== public interactions

        public void OnWake()
        {
            greetOnLand = true;
            airReason = AirReason.Fall;
            lastInteraction = t;
            act = Act.Idle; actT = 0; actDur = -1;
        }

        public void OnGrab()
        {
            lastInteraction = t;
            act = Act.Idle; actT = 0; actDur = -1;
            heldT = 0; saidWhee = false; saidWhoa = false;
            faceOverrideUntil = -1;
            Spawn(FxKind.Bang, HeadTop(0.35), new V2(0, -20 * ph.K));
            if (rng.NextDouble() < 0.55) Say(Pick(GrabLines), 1.2);
        }

        public void OnPoke()
        {
            lastInteraction = t;
            pokes += 1;
            ph.SyVel -= 3.2;
            if (ph.Mode != PetMode.Grounded) { Face(Expr.SquintIn, 0.35); return; }
            if (act == Act.Sleep) { WakeUp(); return; }
            if (pokes >= 4.5) { pokes = 0; Start(Act.Annoyed); return; }
            if (rng.NextDouble() < 0.6) Start(Act.Giggle);
            else { Face(Expr.SquintIn, 0.3); Say(Pick(PokeLines), 1.1); }
        }

        public void OnDoubleClick()
        {
            lastInteraction = t;
            if (ph.Mode != PetMode.Grounded) return;
            if (act == Act.Sleep) { WakeUp(); return; }
            hopPower = 1.35;
            Start(Act.Hop);
            Say("wheee!", 1.2);
        }

        public void OnBodyDragAttempt()
        {
            lastInteraction = t;
            Say("grab my ring \u2191", 2.2);
            Face(Expr.SquintIn, 0.45);
            ph.Omega += 2.5;
        }

        public void OnPetStroke(double distance)
        {
            if (ph.Mode != PetMode.Grounded) return;
            petMeter = Math.Min(petMeter + distance, 1400 * ph.K);
            lastInteraction = t;
        }

        public void OnGreet()
        {
            lastInteraction = t;
            if (ph.Mode != PetMode.Grounded) { Say(Pick(Greetings)); return; }
            if (act == Act.Sleep) { WakeUp(); return; }
            Start(Act.Wave);
        }

        public void OnPetButton()
        {
            lastInteraction = t;
            petMeter = 900 * ph.K;
            if (ph.Mode == PetMode.Grounded && act != Act.Sleep) Start(Act.Happy);
        }

        public void OnNapButton()
        {
            lastInteraction = t;
            if (act == Act.Sleep) { WakeUp(); return; }
            if (ph.Mode == PetMode.Grounded) StartSleep(); else pendingNap = true;
        }

        public void OnHopButton()
        {
            lastInteraction = t;
            if (ph.Mode != PetMode.Grounded) return;
            if (act == Act.Sleep) { WakeUp(); return; }
            hopPower = 1.2;
            Start(Act.Hop);
        }

        // ================================================================== think

        public void Think(double dt, CursorInfo cursor)
        {
            t += dt;
            cur = cursor;
            double k = ph.K;

            handledEdge = false;
            foreach (var e in ph.Events) OnPhysEvent(e);
            ph.Events.Clear();

            if (ph.Mode != lastMode)
            {
                OnModeChanged(lastMode, ph.Mode);
                lastMode = ph.Mode;
            }

            if (cur.Dist < 220 * k && cur.Speed > 40 * k) lastInteraction = t;
            petMeter = Math.Max(0, petMeter - dt * 260 * k);
            pokes = Math.Max(0, pokes - dt * 0.8);
            dizzy = Math.Max(0, dizzy - dt * 2.5);

            ResetTargets();

            switch (ph.Mode)
            {
                case PetMode.Held: ThinkHeld(dt); break;
                case PetMode.Airborne: ThinkAir(dt); break;
                default: ThinkGround(dt); break;
            }

            if (Speech != null && t > speechUntil) Speech = null;
        }

        void ResetTargets()
        {
            gExpr = Expr.Open;
            gEyeOpen = 1;
            gEyeScale = 1;
            gYaw = 0;
            gSpinVel = 0;
            if (t > nextGaze)
            {
                nextGaze = t + Rnd(0.7, 2.6);
                double a = Rnd(0, Math.PI * 2), r = Math.Sqrt(rng.NextDouble()) * 0.75;
                wanderGaze = new V2(Math.Cos(a) * r, Math.Sin(a) * r * 0.7);
                if (rng.NextDouble() < 0.35) wanderGaze = V2.Zero;
            }
            gLook = wanderGaze;
            double sway = Math.Sin(t * 1.6);
            gArmL = 0.06 + 0.04 * sway;
            gArmR = 0.06 - 0.04 * sway;
            for (int i = 0; i < 4; i++) { gLegSwing[i] = 0; gLegLift[i] = 0; }
            ph.WalkVel = 0;
            ph.SquashTarget = 1 + 0.012 * Math.Sin(t * 2.3); // breathing
            ph.ThetaTarget = 0;
            ph.Lift = 0;
        }

        void OnModeChanged(PetMode from, PetMode to)
        {
            if (to == PetMode.Held)
            {
                heldT = 0;
                if (act == Act.Sleep) { act = Act.Idle; Say(Pick(WakeLines), 1.2); }
            }
            else if (from == PetMode.Held && to == PetMode.Airborne)
            {
                airReason = AirReason.Thrown;
                airT = 0;
                if (ph.Vel.Length > 1500 * ph.K) Say("wheee!", 1.3);
            }
            else if (to == PetMode.Airborne)
            {
                airT = 0;
            }
        }

        void OnPhysEvent(PhysEvent e)
        {
            double k = ph.K;
            switch (e.Kind)
            {
                case PhysEventKind.Landed:
                {
                    double imp = e.Strength / k;
                    if (imp > 1400) Spawn(FxKind.Sparkle, ph.BodyPoint(-0.42, 0.05), new V2(-90 * k, -40 * k));
                    if (imp > 1400) Spawn(FxKind.Sparkle, ph.BodyPoint(0.42, 0.05), new V2(90 * k, -40 * k));
                    if (greetOnLand)
                    {
                        greetOnLand = false;
                        Start(Act.Wave);
                    }
                    else if (dizzy > 16)
                    {
                        dizzy = 0;
                        Start(Act.Dizzy);
                    }
                    else if (imp > 3000)
                    {
                        ouchExpr = Expr.Cross; Start(Act.Ouch); Say("ouch", 1.3);
                    }
                    else if (imp > 2000)
                    {
                        ouchExpr = Expr.SquintIn; Start(Act.Ouch); actDur = 0.7; Say("oof", 1.0);
                    }
                    else if (airReason == AirReason.Thrown && rng.NextDouble() < 0.35)
                    {
                        Start(Act.Giggle);
                    }
                    else if (pendingNap)
                    {
                        pendingNap = false; StartSleep();
                    }
                    else
                    {
                        Start(Act.Idle);
                    }
                    break;
                }
                case PhysEventKind.Bonk:
                    if (e.Strength / k > 700)
                    {
                        Face(Expr.SquintIn, 0.55);
                        Say(rng.NextDouble() < 0.5 ? "bonk!" : "oof", 1.0);
                        Spawn(FxKind.Sparkle, ph.BodyPoint(0.6 * e.Side, 0.6), new V2(-60 * k * e.Side, -50 * k));
                    }
                    break;
                case PhysEventKind.WalkedOff:
                case PhysEventKind.SupportLost:
                    airReason = AirReason.Fall;
                    Spawn(FxKind.Bang, HeadTop(0.3), new V2(0, -20 * k));
                    Face(Expr.Open, 0.6, 1.3);
                    if (rng.NextDouble() < 0.6) Say("whoa!", 1.1);
                    break;
                case PhysEventKind.HitEdge:
                    if (handledEdge) break;
                    handledEdge = true;
                    if (act == Act.Walk || act == Act.Curious)
                    {
                        if (act == Act.Walk && rng.NextDouble() < 0.6)
                        {
                            walkDir = -walkDir;
                            walkTarget = ph.C.X + walkDir * Rnd(100, 320) * k;
                        }
                        else Start(Act.Idle);
                    }
                    break;
            }
        }

        // ================================================================== held

        void ThinkHeld(double dt)
        {
            heldT += dt;
            double w = Math.Abs(ph.Omega);
            if (w > 6) dizzy += w * dt;
            SetStatus("Hanging out", Palette.Blue);

            for (int i = 0; i < 4; i++)
                gLegSwing[i] = Math.Sin(t * 6.5 + i * 1.7) * 0.22 + M.Clamp(-ph.Omega * 0.045, -0.35, 0.35);
            double up = 0.35 + M.Clamp(w * 0.06, 0, 0.7);
            gArmL = up + 0.14 * Math.Sin(t * 8.5);
            gArmR = up + 0.14 * Math.Sin(t * 8.5 + 2.1);

            if (heldT < 0.5)
            {
                gExpr = Expr.Open; gEyeScale = 1.32;
            }
            else if (w > 9.5)
            {
                gExpr = Expr.SquintIn;
                if (!saidWhoa) { saidWhoa = true; Say("whoaaa", 1.2); }
            }
            else if (w > 3.2)
            {
                gExpr = Expr.Happy;
                if (!saidWhee && rng.NextDouble() < dt * 2) { saidWhee = true; Say("wheee!", 1.2); }
            }
            else
            {
                gExpr = Expr.Open;
                gLook = new V2(0, 0.85); // looks up at you
                if (heldT > 7 && t > nextHeldLine)
                {
                    nextHeldLine = t + Rnd(9, 15);
                    Say(Pick(HeldLines), 1.8);
                }
            }
            ph.SquashTarget = 1 + M.Clamp(ph.Vel.Length / (9000 * ph.K), 0, 0.08);
        }

        // ================================================================== air

        void ThinkAir(double dt)
        {
            airT += dt;
            double k = ph.K;
            double speed = ph.Vel.Length;
            V2 vdir = ph.Vel.Normalized();
            gLook = new V2(vdir.X * 0.6, -vdir.Y * 0.6);

            switch (airReason)
            {
                case AirReason.Hop:
                    gExpr = Expr.Happy;
                    gArmL = gArmR = 0.75;
                    for (int i = 0; i < 4; i++) gLegLift[i] = 0.55;
                    SetStatus("Bouncing", Palette.Green);
                    break;
                case AirReason.Startle:
                    gExpr = Expr.Open; gEyeScale = 1.35;
                    gArmL = gArmR = 0.95;
                    for (int i = 0; i < 4; i++) gLegSwing[i] = Math.Sin(t * 22 + i * 1.3) * 0.18;
                    SetStatus("Startled", Palette.Amber);
                    break;
                case AirReason.Annoyed:
                    gExpr = Expr.SquintIn;
                    gArmL = gArmR = 0.3;
                    SetStatus("Grumpy", Palette.Red);
                    break;
                default:
                    if (ph.Vel.Y < -200 * k || speed > 1600 * k)
                    {
                        gExpr = Expr.Happy;
                        gArmL = 1.0 + 0.2 * Math.Sin(t * 14);
                        gArmR = 1.0 + 0.2 * Math.Sin(t * 14 + 1.5);
                        for (int i = 0; i < 4; i++) gLegSwing[i] = Math.Sin(t * 12 + i * 1.6) * 0.2;
                        SetStatus("Wheee!", Palette.Blue);
                    }
                    else
                    {
                        gExpr = Expr.Open; gEyeScale = 1.25;
                        gArmL = 0.6 + 0.5 * Math.Sin(t * 19);
                        gArmR = 0.6 + 0.5 * Math.Sin(t * 19 + Math.PI);
                        for (int i = 0; i < 4; i++) gLegSwing[i] = Math.Sin(t * 21 + i * 1.4) * 0.3;
                        SetStatus("Falling", Palette.Amber);
                    }
                    break;
            }
            ph.SquashTarget = 1 + M.Clamp(Math.Abs(ph.Vel.Y) / (7000 * k), 0, 0.12);
        }

        // ================================================================== ground

        bool Busy { get { return act == Act.Ouch || act == Act.Dizzy || act == Act.Startle || act == Act.Annoyed; } }

        void ThinkGround(double dt)
        {
            double k = ph.K;
            actT += dt;

            if (pendingNap && act != Act.Sleep) { pendingNap = false; StartSleep(); }

            if (act != Act.Sleep && !Busy && t - lastInteraction > sleepAfter && cur.Dist > 200 * k) StartSleep();

            if (petMeter > 380 * k && act != Act.Happy && act != Act.Sleep && !Busy) Start(Act.Happy);

            if (act != Act.Sleep && act != Act.Startle && !Busy && cur.Dist < 150 * k && !cur.OverBody &&
                cur.Speed > 2600 * k && t - lastStartle > 6)
            {
                lastStartle = t;
                Start(Act.Startle);
            }

            SetStatus("Vibing", Palette.Blue);

            switch (act)
            {
                case Act.Idle:
                    if (actDur < 0) actDur = Rnd(2.0, 5.0);
                    LookAtCursorOrWander(700 * k);
                    if (actT > actDur) ChooseNext();
                    break;

                case Act.Walk:
                {
                    SetStatus("Exploring", Palette.Blue);
                    ph.WalkVel = walkDir * 78 * k * walkSpeedMul;
                    gYaw = walkDir * 32;
                    gLook = new V2(walkDir * 0.75, 0.05);
                    Gait(dt);
                    if ((walkTarget - ph.C.X) * walkDir <= 0 || actT > 14) Start(Act.Idle);
                    break;
                }

                case Act.LookAround:
                {
                    double s = Math.Sin(actT / actDur * Math.PI * 2);
                    gYaw = s * 30;
                    gLook = new V2(s * 0.9, 0.15);
                    if (actT > actDur) Start(Act.Idle);
                    break;
                }

                case Act.Hop:
                    gExpr = Expr.Happy;
                    if (!actFlag)
                    {
                        ph.SquashTarget = 0.78;
                        gArmL = gArmR = -0.25;
                        if (actT > 0.16)
                        {
                            actFlag = true;
                            airReason = AirReason.Hop;
                            double vx = settings.WalkAround ? Rnd(-1, 1) * 50 * k : 0;
                            ph.Jump(vx, Rnd(760, 980) * k * hopPower);
                            hopPower = 1;
                        }
                    }
                    else if (actT > 1.5) Start(Act.Idle);
                    break;

                case Act.Stretch:
                    SetStatus("Stretching", Palette.Green);
                    if (actT < 0.5) { ph.SquashTarget = 1.13; gArmL = gArmR = 1.15; gExpr = Expr.Closed; }
                    else if (actT < 1.7) { ph.SquashTarget = 1.15 + 0.01 * Math.Sin(t * 20); gArmL = gArmR = 1.25; gExpr = Expr.Closed; }
                    else
                    {
                        gExpr = Expr.Happy;
                        ph.ThetaTarget = 0.07 * Math.Sin(actT * 26) * Math.Max(0, 1 - (actT - 1.7) / 0.9);
                    }
                    if (actT > actDur) Start(Act.Idle);
                    break;

                case Act.Dance:
                {
                    SetStatus("Vibing hard", Palette.Purple);
                    double beat = actT * Math.PI * 2 * 2.0;
                    ph.SquashTarget = 1 + 0.06 * Math.Sin(beat * 2);
                    ph.ThetaTarget = 0.13 * Math.Sin(beat);
                    gArmL = 0.7 + 0.55 * Math.Sin(beat);
                    gArmR = 0.7 - 0.55 * Math.Sin(beat);
                    for (int i = 0; i < 4; i++) gLegLift[i] = Math.Max(0, Math.Sin(beat + (i % 2) * Math.PI)) * 0.5;
                    gExpr = Expr.Happy;
                    gYaw = 14 * Math.Sin(beat * 0.5);
                    if (t > nextNote) { nextNote = t + 0.6; Spawn(FxKind.Note, HeadTop(rng.NextDouble() < 0.5 ? -0.35 : 0.35), new V2(Rnd(-25, 25) * k, -45 * k)); }
                    if (actT > actDur) Start(Act.Idle);
                    break;
                }

                case Act.Twirl:
                    twirl = 360 * M.SmoothStep(actT / actDur);
                    ph.SquashTarget = 1 - 0.06 * Math.Sin(Math.PI * M.Clamp01(actT / actDur));
                    gArmL = gArmR = 0.55;
                    gExpr = Expr.Happy;
                    if (actT >= actDur) { twirl = 0; Start(Act.Idle); }
                    break;

                case Act.Wave:
                {
                    SetStatus("Saying hi", Palette.Green);
                    double a = 1.05 + 0.38 * Math.Sin(actT * 15);
                    if (waveSide == 1) gArmR = a; else gArmL = a;
                    gExpr = actT < 1.0 ? Expr.Happy : Expr.Open;
                    LookAtCursor();
                    if (actT > actDur) Start(Act.Idle);
                    break;
                }

                case Act.TapFoot:
                    gLegLift[3] = Math.Max(0, Math.Sin(actT * 13)) * 0.7;
                    gEyeOpen = 0.78;
                    LookAtCursor();
                    if (actT > actDur) Start(Act.Idle);
                    break;

                case Act.Sleep:
                    ThinkSleep(dt);
                    break;

                case Act.Dizzy:
                    SetStatus("Dizzy", Palette.Amber);
                    gSpinVel = 620;
                    ph.ThetaTarget = 0.11 * Math.Sin(t * 6.5);
                    gYaw = 12 * Math.Sin(t * 3.1);
                    gArmL = 0.2 + 0.25 * Math.Sin(t * 5);
                    gArmR = 0.2 + 0.25 * Math.Sin(t * 5 + 2);
                    for (int i = 0; i < 4; i++) gLegSwing[i] = Math.Sin(t * 5 + i) * 0.1;
                    if (actT > actDur) Start(Act.Idle);
                    break;

                case Act.Happy:
                    SetStatus("Happy", Palette.Green);
                    gExpr = Expr.Heart;
                    ph.ThetaTarget = 0.035 * Math.Sin(t * 28); // purr
                    ph.SquashTarget = 1 + 0.03 * Math.Sin(t * 9);
                    gArmL = 0.45 + 0.25 * Math.Sin(t * 10);
                    gArmR = 0.45 + 0.25 * Math.Sin(t * 10 + 1.2);
                    for (int i = 0; i < 4; i++) gLegLift[i] = Math.Max(0, Math.Sin(t * 12 + i * 1.7)) * 0.25;
                    if (t > nextHeart)
                    {
                        nextHeart = t + 0.38;
                        Spawn(FxKind.Heart, HeadTop(Rnd(-0.35, 0.35)), new V2(Rnd(-18, 18) * k, -55 * k));
                    }
                    if (petMeter < 60 * k) { happyCool += dt; if (happyCool > 1.0) Start(Act.Idle); }
                    else happyCool = 0;
                    break;

                case Act.Ouch:
                    SetStatus("Ouch", Palette.Red);
                    gExpr = ouchExpr;
                    ph.ThetaTarget = 0.06 * Math.Sin(actT * 38) * Math.Max(0, 1 - actT / actDur);
                    gArmL = gArmR = -0.15;
                    if (actT > actDur) Start(Act.Idle);
                    break;

                case Act.Curious:
                {
                    SetStatus("Curious", Palette.Blue);
                    double dx = cur.Pos.X - ph.C.X;
                    if (!actFlag)
                    {
                        int dir = dx >= 0 ? 1 : -1;
                        walkDir = dir;
                        ph.WalkVel = dir * 70 * k;
                        gYaw = dir * 30;
                        gLook = new V2(dir * 0.7, 0.3);
                        Gait(dt);
                        if (Math.Abs(dx) < 95 * k)
                        {
                            actFlag = true; actT2 = 0;
                            Spawn(FxKind.Question, HeadTop(0.3), new V2(0, -18 * k));
                            if (rng.NextDouble() < 0.4) Say("whatcha doing?", 1.8);
                        }
                        if (cur.Speed > 600 * k || actT > 9) Start(Act.Idle);
                    }
                    else
                    {
                        LookAtCursor();
                        gEyeScale = 1.1;
                        actT2 += dt;
                        if (actT2 > 2.4) Start(Act.Idle);
                    }
                    break;
                }

                case Act.Startle:
                    gExpr = Expr.Open; gEyeScale = 1.35;
                    if (!actFlag)
                    {
                        actFlag = true;
                        airReason = AirReason.Startle;
                        Spawn(FxKind.Bang, HeadTop(0.3), new V2(0, -20 * k));
                        ph.Jump(0, 520 * k);
                    }
                    else if (actT > 1.2) Start(Act.Idle);
                    break;

                case Act.Giggle:
                    SetStatus("Giggling", Palette.Green);
                    gExpr = Expr.Happy;
                    ph.SquashTarget = 1 + 0.05 * Math.Sin(actT * 25);
                    gArmL = 0.5 + 0.2 * Math.Sin(actT * 20);
                    gArmR = 0.5 + 0.2 * Math.Sin(actT * 20 + 1);
                    if (actT > actDur) Start(Act.Idle);
                    break;

                case Act.Annoyed:
                    gExpr = Expr.SquintIn;
                    if (!actFlag)
                    {
                        actFlag = true;
                        airReason = AirReason.Annoyed;
                        double away = cur.Pos.X > ph.C.X ? -1 : 1;
                        ph.Jump(away * (settings.WalkAround ? 260 : 0) * k, 620 * k);
                    }
                    else if (actT > 1.2) Start(Act.Idle);
                    break;
            }
        }

        void ThinkSleep(double dt)
        {
            double k = ph.K;
            sleepT += dt;
            switch (sleepPhase)
            {
                case 0: // yawn
                    SetStatus("Sleepy", Palette.Purple);
                    ph.SquashTarget = 1.12;
                    gArmL = gArmR = 1.0;
                    gExpr = Expr.Closed;
                    if (sleepT > 1.8) { sleepPhase = 1; sleepT = 0; }
                    break;
                case 1: // drowsy
                    SetStatus("Sleepy", Palette.Purple);
                    gEyeOpen = M.Lerp(0.45, 0.08, M.Clamp01(sleepT / 2.6));
                    ph.ThetaTarget = 0.04 * Math.Sin(sleepT * 2.2);
                    gArmL = gArmR = -0.1;
                    if (sleepT > 2.6) { sleepPhase = 2; sleepT = 0; nextZ = t + 0.4; }
                    break;
                default: // asleep
                    SetStatus("Napping", Palette.Purple);
                    gExpr = petMeter > 120 * k ? Expr.Happy : Expr.Closed; // smiles in its sleep when petted
                    ph.SquashTarget = 1 + 0.035 * Math.Sin(t * 1.3);
                    gArmL = gArmR = -0.18 + 0.03 * Math.Sin(t * 1.3);
                    if (t > nextZ)
                    {
                        nextZ = t + 1.6;
                        Spawn(FxKind.Z, HeadTop(0.32), new V2(14 * k, -26 * k));
                    }
                    if (petMeter > 120 * k && t > nextHeart)
                    {
                        nextHeart = t + 0.7;
                        Spawn(FxKind.Heart, HeadTop(Rnd(-0.3, 0.3)), new V2(Rnd(-12, 12) * k, -40 * k));
                    }
                    break;
            }
            // wakes up when the cursor rushes past (petting doesn't count)
            if (sleepPhase >= 1 && cur.Dist < 140 * k && !cur.OverBody && cur.Speed > 500 * k) WakeUp();
        }

        void StartSleep()
        {
            Start(Act.Sleep);
            sleepPhase = 0; sleepT = 0;
            Say("*yawn*", 1.6);
        }

        void WakeUp()
        {
            lastInteraction = t;
            sleepAfter = Rnd(90, 160);
            Say(Pick(WakeLines), 1.4);
            Start(Act.Startle);
        }

        void ChooseNext()
        {
            double k = ph.K;
            var opts = new List<KeyValuePair<Act, double>>();
            opts.Add(new KeyValuePair<Act, double>(Act.Idle, 30));
            if (settings.WalkAround) opts.Add(new KeyValuePair<Act, double>(Act.Walk, 34));
            opts.Add(new KeyValuePair<Act, double>(Act.LookAround, 9));
            opts.Add(new KeyValuePair<Act, double>(Act.Hop, 6));
            opts.Add(new KeyValuePair<Act, double>(Act.Stretch, 4));
            opts.Add(new KeyValuePair<Act, double>(Act.Dance, 3));
            opts.Add(new KeyValuePair<Act, double>(Act.Twirl, 4));
            opts.Add(new KeyValuePair<Act, double>(Act.TapFoot, 4));
            double adx = Math.Abs(cur.Pos.X - ph.C.X);
            bool cursorOnFloor = settings.WalkAround && cur.StillTime > 1.2 &&
                                 Math.Abs(cur.Pos.Y - ph.FeetScreenY) < 160 * k && adx > 140 * k && adx < 800 * k;
            if (cursorOnFloor) opts.Add(new KeyValuePair<Act, double>(Act.Curious, 25));

            double total = 0;
            foreach (var o in opts) total += o.Value;
            double r = rng.NextDouble() * total;
            foreach (var o in opts)
            {
                r -= o.Value;
                if (r <= 0) { Start(o.Key); return; }
            }
            Start(Act.Idle);
        }

        void Start(Act a)
        {
            double k = ph.K;
            act = a;
            actT = 0;
            actT2 = 0;
            actFlag = false;
            actDur = -1;
            if (a != Act.Twirl) twirl = 0;
            switch (a)
            {
                case Act.Walk:
                {
                    Surface sup = ph.Support;
                    double left = sup != null ? sup.Left : world.Bounds.Left;
                    double right = sup != null ? sup.Right : world.Bounds.Right;
                    double margin = 0.75 * ph.S;
                    int dir = rng.NextDouble() < 0.5 ? -1 : 1;
                    if (ph.C.X - left < 2 * margin) dir = 1;
                    else if (right - ph.C.X < 2 * margin) dir = -1;
                    double target = ph.C.X + dir * Rnd(110, 480) * k;
                    bool allowFall = sup != null && sup.IsWindow && rng.NextDouble() < 0.12;
                    if (!allowFall) target = M.Clamp(target, left + margin, right - margin);
                    if (Math.Abs(target - ph.C.X) < 30 * k) { act = Act.Idle; actDur = Rnd(1.5, 3); return; }
                    walkTarget = target;
                    walkDir = target > ph.C.X ? 1 : -1;
                    walkSpeedMul = Rnd(0.85, 1.25);
                    break;
                }
                case Act.LookAround: actDur = 2.8; break;
                case Act.Stretch: actDur = 2.6; Say("mmmh", 1.2); break;
                case Act.Dance: actDur = 4.5; nextNote = t; break;
                case Act.Twirl: actDur = 1.0; break;
                case Act.Wave:
                    actDur = 1.9;
                    waveSide = cur.Pos.X > ph.C.X ? 1 : 0;
                    Say(Pick(Greetings), 1.6);
                    break;
                case Act.TapFoot: actDur = 1.8; break;
                case Act.Dizzy: actDur = 3.2; Say(rng.NextDouble() < 0.5 ? "@_@" : "the room is spinning", 2.0); break;
                case Act.Happy: happyCool = 0; nextHeart = t; break;
                case Act.Ouch: actDur = 1.3; break;
                case Act.Giggle:
                    actDur = 0.9;
                    Say(Pick(PokeLines), 1.1);
                    Spawn(FxKind.Sparkle, HeadTop(-0.4), new V2(-50 * k, -40 * k));
                    Spawn(FxKind.Sparkle, HeadTop(0.4), new V2(50 * k, -40 * k));
                    break;
                case Act.Annoyed: Say(Pick(AnnoyedLines), 1.4); break;
            }
        }

        // ================================================================== helpers

        void Gait(double dt)
        {
            gaitPhase += Math.Abs(ph.Vel.X) * dt / (0.30 * ph.S) * Math.PI * 2;
            for (int i = 0; i < 4; i++)
            {
                double phi = gaitPhase + (i % 2 == 0 ? 0 : Math.PI);
                gLegSwing[i] = Math.Sin(phi) * 0.30;
                gLegLift[i] = Math.Max(0, Math.Cos(phi)) * 0.55;
            }
            ph.Lift = Math.Abs(Math.Sin(gaitPhase)) * 0.018 * ph.S;
            ph.ThetaTarget = Math.Sin(gaitPhase) * 0.025; // waddle
            gArmL = 0.1 + Math.Sin(gaitPhase) * 0.18;
            gArmR = 0.1 - Math.Sin(gaitPhase) * 0.18;
        }

        void LookAtCursor()
        {
            double k = ph.K;
            V2 eye = ph.BodyPoint(0, 0.66);
            V2 d = cur.Pos - eye;
            V2 l = new V2(d.X, -d.Y) / (220 * k);
            if (l.Length > 1) l = l.Normalized();
            gLook = l;
            gYaw = M.Clamp(d.X / (420 * k), -1, 1) * 16;
        }

        void LookAtCursorOrWander(double maxDist)
        {
            if (cur.Dist < maxDist) LookAtCursor();
        }

        // ================================================================== animate (after physics)

        public void Animate(double dt)
        {
            if (dt <= 0) return;

            // body acceleration drives the twisty secondary motion
            accX = M.Damp(accX, (ph.Vel.X - lastVelX) / dt, 20, dt);
            lastVelX = ph.Vel.X;

            // ---- expression with a blink-masked swap
            Expr want = gExpr;
            double wantScale = gEyeScale;
            if (t < faceOverrideUntil) { want = faceOverride; wantScale = Math.Max(wantScale, faceOverrideScale); }
            if (want != shownExpr && swapT < 0) swapT = 0;
            double swapF = 1;
            if (swapT >= 0)
            {
                swapT += dt;
                if (swapT < 0.055) swapF = 1 - swapT / 0.055;
                else
                {
                    shownExpr = want;
                    swapF = M.Clamp01((swapT - 0.055) / 0.06);
                    if (swapT > 0.115) swapT = -1;
                }
            }

            // ---- blinking
            double blinkF = 1;
            if (blinkT < 0)
            {
                if (t > nextBlink) blinkT = 0;
            }
            else
            {
                blinkT += dt;
                blinkF = blinkT < 0.07 ? 1 - blinkT / 0.07 : M.Clamp01((blinkT - 0.07) / 0.09);
                if (blinkT > 0.16)
                {
                    blinkT = -1;
                    blinkF = 1;
                    if (!inDoubleBlink && rng.NextDouble() < 0.2) { inDoubleBlink = true; nextBlink = t + 0.09; }
                    else { inDoubleBlink = false; nextBlink = t + Rnd(1.8, 5.5); }
                }
            }
            if (shownExpr != Expr.Open && shownExpr != Expr.Heart) blinkF = 1;

            eyeOpen = M.Damp(eyeOpen, gEyeOpen, 12, dt);
            eyeScale = M.Damp(eyeScale, wantScale, 14, dt);
            look = M.Damp(look, gLook, 22, dt);

            // ---- dizzy eye spin, settling on a square-friendly angle
            spinVel = M.Damp(spinVel, gSpinVel, gSpinVel > 0 ? 6 : 2.5, dt);
            spinL += spinVel * dt;
            spinR -= spinVel * dt;
            if (spinVel < 25)
            {
                spinL = M.Damp(spinL, Math.Round(spinL / 90) * 90, 10, dt);
                spinR = M.Damp(spinR, Math.Round(spinR / 90) * 90, 10, dt);
                if (Math.Abs(spinL % 90) < 0.05) spinL = 0;
                if (Math.Abs(spinR % 90) < 0.05) spinR = 0;
            }

            // ---- yaw: springy turn toward targets; when hanging it twists like a real keychain
            if (ph.Mode == PetMode.Held)
                yawVel += (-14 * yaw - 2.4 * yawVel) * dt - accX * 0.025 * dt;
            else
                yawVel += (75 * (gYaw - yaw) - 12 * yawVel) * dt;
            yawVel = M.Clamp(yawVel, -900, 900);
            yaw = M.Clamp(yaw + yawVel * dt, -140, 140);

            keyTwistV += (-35 * keyTwist - 2.2 * keyTwistV) * dt - accX * 0.035 * dt;
            keyTwist = M.Clamp(keyTwist + keyTwistV * dt, -85, 85);
            jumpTwistV += (-55 * jumpTwist - 3.0 * jumpTwistV) * dt + accX * 0.03 * dt;
            jumpTwist = M.Clamp(jumpTwist + jumpTwistV * dt, -70, 70);

            // ---- limbs
            for (int i = 0; i < 4; i++)
            {
                legSwing[i] = M.Damp(legSwing[i], gLegSwing[i], 20, dt);
                legLift[i] = M.Damp(legLift[i], gLegLift[i], 20, dt);
                Pose.LegSwing[i] = legSwing[i];
                Pose.LegLift[i] = legLift[i];
            }
            armL = M.Damp(armL, gArmL, 16, dt);
            armR = M.Damp(armR, gArmR, 16, dt);

            Pose.Expr = shownExpr;
            Pose.EyeOpen = Math.Max(0.06, eyeOpen * blinkF * swapF);
            Pose.EyeScale = eyeScale;
            Pose.LookX = look.X;
            Pose.LookY = look.Y;
            Pose.EyeSpinL = spinL;
            Pose.EyeSpinR = spinR;
            Pose.YawDeg = yaw + twirl;
            Pose.KeyTwistDeg = keyTwist;
            Pose.JumpTwistDeg = jumpTwist;
            Pose.ArmL = armL;
            Pose.ArmR = armR;
        }
    }
}
