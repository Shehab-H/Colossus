using Colossus.Domain.Baking;
using Colossus.Domain.Measures;
using Colossus.Domain.Model;
using Colossus.Domain.Tiling;

namespace Colossus.Application;

/// <summary>The internal render view for a group-regime bake: the marks table's
/// columns as ordinary channels — the mark <c>id</c>, each perMark channel (authored role/type kept),
/// and every measure materialized (numeric → an f32 measure; argmax/argmin → a categorical dict
/// dimension). perFact channels are gone (they live only in companions). The reducer and domain scanner
/// see this; the manifest keeps the authored view untouched.</summary>
public static class EffectiveView
{
    public static ViewConfig For(ViewConfig authored, FactGrouping grouping)
    {
        var perMark = grouping.PerMarkChannels.ToHashSet(StringComparer.Ordinal);

        var channels = new List<ChannelSpec>
        {
            new() { Name = TileSchema.Id, Column = TileSchema.Id, Role = ChannelRole.Identity, Type = ChannelType.Dict },
        };
        channels.AddRange(authored.Source.Channels.Where(c => perMark.Contains(c.Name)));
        foreach (var m in authored.Measures!)
        {
            bool categorical = MeasureParser.Parse(m.Expr) is ArgExt;
            channels.Add(new ChannelSpec
            {
                Name = m.Name,
                Column = m.Name,
                Role = categorical ? ChannelRole.Dimension : ChannelRole.Measure,
                Type = categorical ? ChannelType.Dict : ChannelType.F32,
            });
        }

        // No measures block: this view IS the materialized marks table, not a group-regime source.
        return authored with { Measures = null, Source = authored.Source with { Channels = channels } };
    }
}
