using AdvancedPenumbraModConverter.Models;
using AdvancedPenumbraModConverter.Session;
using AdvancedPenumbraModConverter.Windows.Ui;

namespace AdvancedPenumbraModConverter.Windows.Components;

/// <summary>
/// The Plan tab without Advanced details. What gets converted is listed in the conversion plan
/// under the item pickers, with its icons and switches, so this only points there; the tables
/// of game paths, metadata entries and file operations live behind Advanced details.
/// </summary>
internal static class PlanSummaryView
{
    public static void Draw(ConverterSession session, ConversionTask task)
        => Widgets.MutedWrapped("What gets converted is listed in the conversion plan under the item pickers. " +
                                "Turn on Advanced details to see every path, metadata entry and file it changes.");
}
