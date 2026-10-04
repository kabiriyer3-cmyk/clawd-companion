using System.Windows.Media;

namespace ClawdCompanion.Core
{
    /// <summary>Codex semantic colors (dark theme) used for status dots and effects.</summary>
    public static class Palette
    {
        public static readonly Color Blue = Color.FromRgb(0x33, 0x9C, 0xFF);    // Codex accent: running / active
        public static readonly Color Green = Color.FromRgb(0x40, 0xC9, 0x77);   // Codex diff-added: ready / happy
        public static readonly Color Red = Color.FromRgb(0xFA, 0x42, 0x3E);     // Codex diff-removed: blocked / ouch
        public static readonly Color Purple = Color.FromRgb(0xAD, 0x7B, 0xF9);  // Codex skill: sleepy / dreamy
        public static readonly Color Amber = Color.FromRgb(0xF5, 0xB8, 0x3D);   // needs attention: dizzy / falling
        public static readonly Color Clawd = Color.FromRgb(0xD7, 0x77, 0x57);   // Clawd orange
    }
}
