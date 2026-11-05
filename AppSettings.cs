namespace WindowsNotch
{
    public sealed class AppSettings
    {
        public string? Anchor { get; set; }
        public bool AutoStart { get; set; }
        public string Language { get; set; } = Localization.DefaultLanguage;

        /// <summary>
        /// Modifier keys untuk global hotkey.
        /// MOD_ALT=1, MOD_CTRL=2, MOD_SHIFT=4, MOD_WIN=8
        /// Default: Ctrl+Shift (6)
        /// </summary>
        public uint HotkeyModifiers { get; set; } = 6; // Ctrl + Shift

        /// <summary>
        /// Virtual-key code untuk global hotkey.
        /// Default: N (0x4E)
        /// </summary>
        public uint HotkeyKey { get; set; } = 0x4E; // N

        public double? NoteLeft { get; set; }
        public double? NoteTop { get; set; }
        public double? SettingsLeft { get; set; }
        public double? SettingsTop { get; set; }
        public double? DropShelfLeft { get; set; }
        public double? DropShelfTop { get; set; }

        // Notchi Mobile Settings
        public bool IsMobileServerEnabled { get; set; } = false;
        public int MobileServerPort { get; set; } = 8765;
        public long MobileMaxFileSizeMb { get; set; } = 500;
        public string? PcId { get; set; }
    }
}
