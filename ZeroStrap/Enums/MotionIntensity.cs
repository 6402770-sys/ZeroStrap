namespace ZeroStrap.Enums
{
    /// <summary>How far and how long interface transitions travel when animations are on.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MotionIntensity
    {
        Subtle,
        Standard
    }
}
