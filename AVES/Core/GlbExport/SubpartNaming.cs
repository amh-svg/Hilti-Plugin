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
        /// PROVISIONAL until confirmed: the spike log shows the type's
        /// Element.Name gives the expected text. The "Family Name" and
        /// "Type Name" parameters can carry a trailing space that this does
        /// not, so they are not used.
        /// </remarks>
        public static string BuildName(FamilyInstance instance) => instance.Symbol.Name;
    }
}
