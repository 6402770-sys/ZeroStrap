using Bloxstrap.FastFlags;

namespace Bloxstrap.UI.ViewModels.Settings
{
    /// <summary>One row of the Fast Flag workspace grid: a configured flag or a catalog entry.</summary>
    public class FastFlagRow
    {
        public string Name { get; set; }

        /// <summary>Configured value; empty for catalog-only rows (typing a value adds the flag).</summary>
        public string Value { get; set; }

        public FlagValueType Type { get; }

        public bool IsConfigured { get; }

        public bool IsExperimental { get; }

        public string? Layer { get; }

        public bool InCatalog { get; }

        public string TypeLabel => Type switch
        {
            FlagValueType.Bool => "Bool",
            FlagValueType.Int => "Int",
            FlagValueType.String => "String",
            _ => "Log"
        };

        public string EffectLabel => IsExperimental ? "EXPERIMENTAL" : "PLAYER";

        public FastFlagRow(FlagIndexItem item)
        {
            Name = item.Name;
            Value = item.Value ?? "";
            Type = item.Type;
            IsConfigured = item.IsConfigured;
            IsExperimental = item.IsExperimental;
            Layer = item.Layer;
            InCatalog = item.Origin.HasFlag(FlagOrigin.Catalog);
        }
    }
}
