using System.Collections.Generic;

namespace AdvancedPenumbraModConverter.Windows.Ui;

/// <summary>
/// Human titles for the plan's diagnostic codes. The codes are how the converter talks to
/// itself; a plan full of <c>inherited_name</c> and <c>variant_group_options</c> asks the
/// reader to know the source. The raw code stays reachable — in the tooltip, and in the
/// column itself under Advanced details — because it is what to search for in a bug report.
/// </summary>
internal static class DiagnosticText
{
    private static readonly Dictionary<string, string> Titles = new()
    {
        // Animations: swapping
        ["unpaired"]               = "Nothing to convert to",
        ["inherited_name"]         = "Inherited animation",
        ["destination_replaced"]   = "Replaces the mod's own file",
        ["destination_extras"]     = "Extra animations in the target",
        ["multiple_animations"]    = "Several animations in one file",
        ["swap_failed"]            = "Swap not possible",
        ["invalid_destination"]    = "Invalid destination",
        ["no_destination"]         = "No destination chosen",
        ["target_conflict"]        = "Two files want the same path",
        ["invalid_pap"]            = "Animation file unreadable",
        ["missing_local_file"]     = "File missing from the mod",
        ["file_swap"]              = "File swap not converted",

        // Animations: option groups
        ["slot_options_in_group"]  = "Slots chosen in the existing group",
        ["slot_options_combining"] = "Combining group cannot be split",
        ["slot_options_too_many"]  = "Too many options for the group",
        ["duplicate_option"]       = "Two options share a name",
        ["empty_option"]           = "Empty option",
        ["group_name"]             = "Group name needed",

        // Animations: retargeting
        ["authored_race"]          = "Built for another race",
        ["inherited_by"]           = "Shared with other races",
        ["retarget_note"]          = "Retargeting note",
        ["retarget_failed"]        = "Retargeting failed",
        ["retarget_unavailable"]   = "Retargeting unavailable",
        ["no_target_race"]         = "No target race chosen",
        ["mod_skeleton"]           = "Uses the mod's own skeleton",
        ["missing_skeleton"]       = "Skeleton missing",
        ["skeleton_warning"]       = "Skeleton warning",

        // Gear and customization
        ["empty_plan"]             = "Nothing to convert",
        ["identical_roots"]        = "Source and target are the same",
        ["cross_slot_geometry"]    = "Different slot shape",
        ["planning_failed"]        = "Planning failed",
        ["rewrite_failed"]         = "File could not be rewritten",
        ["missing_material"]       = "Material missing",
        ["missing_material_dependency"] = "Material dependency missing",
        ["material_animation"]     = "Material animation",
        ["unreadable_resource"]    = "File unreadable",
        ["malformed_mdl"]          = "Model file malformed",
        ["unsupported_mdl_version"] = "Unsupported model version",
        ["source_imc_missing"]     = "Source IMC missing",
        ["target_imc_missing"]     = "Target IMC missing",
        ["metadata_replaced"]      = "Metadata replaced",
        ["metadata_not_transferable"] = "Metadata cannot transfer",
        ["eqp_not_transferable"]   = "Equipment parameters cannot transfer",
        ["est_not_transferable"]   = "Extra skeleton cannot transfer",
        ["est_invalid"]            = "Extra skeleton entry invalid",
        ["est_unavailable"]        = "Extra skeleton data unavailable",
        ["extra_skeleton_missing"] = "Extra skeleton missing",
        ["extra_skeleton_not_added"] = "Extra skeleton not added",
        ["tail_bones_on_ear"]      = "Tail bones on an ear",
        ["pbd_missing"]            = "Race data missing",
        ["pbd_invalid"]            = "Race data invalid",

        // Adding to a mod
        ["additive_shared_path"]         = "Shared with the original",
        ["additive_default_dependency"]  = "Game files added to default",
        ["additive_imc_group_duplicated"] = "IMC group copied",

        // Converting several things at once
        ["queue_conflict"]               = "Overlaps another conversion",
        ["queue_unsupported_kind"]       = "Cannot be converted together",

        // Expressions
        ["no_expression"]                = "No expression chosen",
        ["expression_additive"]          = "Expression needs a new or edited mod",
        ["expression_missing"]           = "Expression not found",
        ["expression_unavailable"]       = "Expressions unavailable",
        ["expression_failed"]            = "Expression could not be attached",
        ["expression_note"]              = "Expression",

        // Output safety
        ["destination_collision"]  = "Two files want the same path",
        ["unsafe_path"]            = "Unsafe game path",
        ["unsafe_local_path"]      = "Unsafe file path",
    };

    /// <summary>The title to show for a code, falling back to the code itself.</summary>
    public static string Title(string code) => Titles.GetValueOrDefault(code, code);

    /// <summary>True when <see cref="Title"/> returned a name rather than the raw code.</summary>
    public static bool HasTitle(string code) => Titles.ContainsKey(code);
}
