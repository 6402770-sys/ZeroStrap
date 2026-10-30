namespace ZeroStrap.FastFlags
{
    public enum FlagEffect
    {
        /// <summary>On Roblox's Player allowlist: the Player client reads it from ClientAppSettings.json.</summary>
        Allowlisted,

        /// <summary>Not on the allowlist: saved correctly, but the Player client ignores it (Studio is not restricted).</summary>
        Experimental
    }

    /// <summary>
    /// Roblox's own Fast Flag allowlist for the Player client. This is NOT a ZeroBootstrap restriction:
    /// ZeroBootstrap writes any valid flag, Roblox Player then ignores the ones missing from this list.
    ///
    /// Source: "Allowlist for local client configuration via Fast Flags", Roblox DevForum, 2025-09-29.
    /// Roblox states the list can change at any time; update <see cref="Names"/> and <see cref="Revision"/> together.
    /// </summary>
    public static class RobloxFlagAllowlist
    {
        public const string SourceUrl = "https://devforum.roblox.com/t/allowlist-for-local-client-configuration-via-fast-flags/3966569";

        public const string Revision = "2025-09-29";

        public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal)
        {
            // geometry
            "DFIntCSGLevelOfDetailSwitchingDistance",
            "DFIntCSGLevelOfDetailSwitchingDistanceL12",
            "DFIntCSGLevelOfDetailSwitchingDistanceL23",
            "DFIntCSGLevelOfDetailSwitchingDistanceL34",

            // rendering
            "FFlagHandleAltEnterFullscreenManually",
            "DFFlagTextureQualityOverrideEnabled",
            "DFIntTextureQualityOverride",
            "FIntDebugForceMSAASamples",
            "DFFlagDisableDPIScale",
            "FFlagDebugGraphicsPreferD3D11",
            "FFlagDebugSkyGray",
            "DFFlagDebugPauseVoxelizer",
            "DFIntDebugFRMQualityLevelOverride",
            "FIntFRMMaxGrassDistance",
            "FIntFRMMinGrassDistance",
            "FFlagDebugGraphicsPreferVulkan",
            "FFlagDebugGraphicsPreferOpenGL",

            // user interface
            "FIntGrassMovementReducedMotionFactor"
        };

        public static FlagEffect GetEffect(string name) => Names.Contains(name) ? FlagEffect.Allowlisted : FlagEffect.Experimental;
    }
}
