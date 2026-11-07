// -----------------------------------------------------------------------
// Title: Subpart Naming
// Summary: Builds the subpart name used in the BOM and on the GLB nodes.
// Key logic: This is the only place the name rule lives. The result must
//            match the schedule text exactly, so it is never trimmed or
//            cleaned.
// -----------------------------------------------------------------------

using Autodesk.Revit.DB;

namespace AVES.Core.GlbExport
{
    internal static class SubpartNaming
    {
        /// <summary>Builds the subpart name for a nested instance.</summary>
        /// <remarks>
        /// PROVISIONAL: "family_type" fits most of the known names, such as
        /// "LB4-PRT-0010_LB4-PRT-0010-084", but the real source is unconfirmed
        /// and "button fix set" probably does not follow it. Replace this once
        /// the spike log has identified the rule.
        /// </remarks>
        public static string BuildName(FamilyInstance instance)
        {
            var symbol = instance.Symbol;
            return $"{symbol.FamilyName}_{symbol.Name}";
        }
    }
}
